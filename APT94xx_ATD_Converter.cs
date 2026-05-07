using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using Virinco.WATS.Interface;
using Virinco.WATS.Integration.TextConverter;

namespace TAKAYA_FlyingProbeConverter
{
    /// <summary>
    /// Converter for APT94xx-format TAKAYA ATD files.
    /// 
    /// Format differences from standard ATD:
    /// - Date format: yy/MM/dd HH:mm:ss  (not d/M/yyyy)
    /// - No "User name :" header line; operator encoded as "OPnnn" in Serial No. line
    /// - No "Test time:" or "Test ID :" lines
    /// - Step lines use quoted, space-padded fields (no tab-delimited column header row)
    /// - Some variants have an extra trailing model-path field on each step line
    /// - AT24 files contain two sequential @-blocks (BOT + TOP), producing two UUT reports
    /// 
    /// Serial No. line format:  Serial No.:&lt;serial&gt; (&lt;operator1&gt;[ - &lt;operator2&gt;])
    ///   serial    → everything before the first ' (' or '('
    ///   operator1 → first token inside parentheses, always logged as UUT operator
    ///   operator2 → second token (if present), logged as MiscUUTInfo "Operator2"
    /// </summary>
    public class APT94xx_ATD_Converter : TextConverterBase
    {
        // Matches a quoted field: "content"
        private static readonly Regex QuotedField = new Regex(@"""([^""]*)""", RegexOptions.Compiled);
        // Matches a bare numeric token (used for H-pin / L-pin integers between quoted fields)
        private static readonly Regex BareInt = new Regex(@"^\s*(\d+)", RegexOptions.Compiled);
        // Matches tolerance like +20% or -20%
        private static readonly Regex Tolerance = new Regex(@"([+-]\d+(?:\.\d+)?)%", RegexOptions.Compiled);

        private static readonly CultureInfo InvariantCulture = CultureInfo.InvariantCulture;
        private static readonly string[] DateFormats = { "dd/MM/yy HH:mm:ss", "dd/MM/yy H:mm:ss" };

        private UUTStatusType _uutStatusFromTester;
        private readonly Dictionary<string, SequenceCall> _componentGroups = new Dictionary<string, SequenceCall>();

        // Pending header values accumulated before Serial No. is seen
        private string _pendingPartNumber;
        private string _pendingRevision;
        private DateTime _pendingDate;
        private string _pendingStationName; // populated when Test ID field present (post 25/3/26)
        private string _pendingSide; // "T", "B", or empty (single-sided)
        private string _pendingSoftwareVersion; // e.g. "V2"
        private string _pendingSoftwareFileName; // e.g. "SWX2"

        // Track whether we are inside the second (or later) @-block in a multi-block file
        private bool _firstBlockDone;

        // Accumulated execution time across all steps (milliseconds)
        private double _totalExecutionTimeMs;

        // Matches a time field like "7.0 ms" or "150.0 ms"
        private static readonly Regex TimeField = new Regex(@"^(\d+\.?\d*)\s*ms$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        public APT94xx_ATD_Converter() : base()
        {
            converterArguments["operationTypeCodeTop"] = "30";
            converterArguments["operationTypeCodeBottom"] = "31";
            converterArguments["GroupByComponentType"] = "true";
            converterArguments["testModeType"] = "Import";
            converterArguments["stationNameFromOperator"] = "false";
        }

        public APT94xx_ATD_Converter(IDictionary<string, string> args) : base(args)
        {
            currentCulture = InvariantCulture;
            searchFields.culture = InvariantCulture;

            // ── Header markers ──────────────────────────────────────────────────
            // Outer @ starts a new record block
            searchFields.AddExactField("RecordStart", ReportReadState.InHeader, "@", null, typeof(string), true);

            // Inner @ (after GROUP block header) transitions to test body
            searchFields.AddExactField("BodyStart", ReportReadState.InHeader, "@", null, typeof(string), false);

            searchFields.AddRegExpField("PassFail", ReportReadState.InHeader,
                @"^\*\s+(?<Result>PASS|FAIL)\s+\*", null, typeof(UUTStatusType));
            // Duplicate for InTest: multi-block files have block 2 header parsed in InTest state
            searchFields.AddRegExpField("PassFail", ReportReadState.InTest,
                @"^\*\s+(?<Result>PASS|FAIL)\s+\*", null, typeof(UUTStatusType));

            searchFields.AddExactField("Model", ReportReadState.InHeader, "Model:", null, typeof(string));
            searchFields.AddExactField("Model", ReportReadState.InTest, "Model:", null, typeof(string));
            searchFields.AddRegExpField("Date", ReportReadState.InHeader,
                @"^DATE\s+(?<Value>\S+\s+\S+)", null, typeof(string));
            searchFields.AddRegExpField("Date", ReportReadState.InTest,
                @"^DATE\s+(?<Value>\S+\s+\S+)", null, typeof(string));

            // Optional Test ID field (appears in files produced after 25/3/26)
            searchFields.AddExactField("TestID", ReportReadState.InHeader, "Test ID :", null, typeof(string));
            searchFields.AddExactField("TestID", ReportReadState.InTest, "Test ID :", null, typeof(string));

            // GROUP header — always GROUP No.1 in these files; just signals group start
            searchFields.AddRegExpField("Group", ReportReadState.InHeader,
                @"^\*\s+GROUP No\.", null, typeof(string));

            // Serial No. line — can appear in both InHeader and InTest states
            searchFields.AddRegExpField("SerialNo", ReportReadState.InHeader,
                @"^Serial No\.\:(?<Value>.+)", null, typeof(string));
            searchFields.AddRegExpField("SerialNoInTest", ReportReadState.InTest,
                @"^Serial No\.\:(?<Value>.+)", null, typeof(string));

            // ── Step lines ───────────────────────────────────────────────────────
            // A step line starts with a 6-digit step number followed by a space
            searchFields.AddRegExpField("Step", ReportReadState.InTest,
                @"^(?<StepNum>\d{6})\s+", null, typeof(string));

            // End of block marker
            searchFields.AddRegExpField("BlockEnd", ReportReadState.InTest,
                @"^@\s*$", null, typeof(string));
        }

        // ── TextConverterBase entry point ────────────────────────────────────────

        protected override bool ProcessMatchedLine(SearchFields.SearchMatch match, ref ReportReadState readState)
        {
            if (match == null)
            {
                // EOF — submit whatever is current
                return SubmitCurrentUUT();
            }

            switch (match.matchField.fieldName)
            {
                case "RecordStart":
                    // First @ in file: begin accumulating header. Subsequent @ within a GROUP
                    // block head is handled by BodyStart below.
                    // Note: do NOT reset pending values here — the second @ in the header
                    // also matches this case and would wipe Model/Date before SerialNo fires.
                    break;

                case "PassFail":
                    _uutStatusFromTester = (UUTStatusType)match.results[0];
                    break;

                case "Model":
                    ParseModel(((string)match.results[0]).Trim(), out _pendingPartNumber, out _pendingRevision, out _pendingSide, out _pendingSoftwareVersion, out _pendingSoftwareFileName);
                    break;

                case "Date":
                    string dateStr = ((string)match.results[0]).Trim();
                    DateTime.TryParseExact(dateStr, DateFormats,
                        InvariantCulture, DateTimeStyles.None, out _pendingDate);
                    break;

                case "TestID":
                    _pendingStationName = ((string)match.results[0]).Trim();
                    break;

                case "Group":
                    // GROUP No. header seen — next @ transitions to InTest
                    break;

                case "BodyStart":
                    // The @ after GROUP header: switch to InTest
                    readState = ReportReadState.InTest;
                    break;

                case "SerialNo":
                case "SerialNoInTest":
                {
                    string raw = ((string)match.results[0]).Trim();
                    ParseSerialAndBatch(raw, out string serial, out string batchCode);
                    ParseOperators(raw, out string operator1, out string operator2);

                    if (_firstBlockDone)
                    {
                        // Second block in file (AT24 BOT+TOP): submit the first UUT first
                        SubmitCurrentUUT();
                    }

                    // When stationNameFromOperator=true, the first token in parentheses is the
                    // machine/station name (e.g. FP1, FP2, FP3) and the second is the human operator.
                    bool stationFromOp = converterArguments.ContainsKey("stationNameFromOperator") &&
                        converterArguments["stationNameFromOperator"].Equals("true", StringComparison.OrdinalIgnoreCase);
                    string uutOperator = stationFromOp ? operator2 : operator1;
                    string serialStationName = stationFromOp ? operator1 : null;

                    // Pick operation type code based on side (T/B)
                    string opCode;
                    if (string.Equals(_pendingSide, "B", StringComparison.OrdinalIgnoreCase))
                        opCode = converterArguments.ContainsKey("operationTypeCodeBottom")
                            ? converterArguments["operationTypeCodeBottom"] : "31";
                    else
                        opCode = converterArguments.ContainsKey("operationTypeCodeTop")
                            ? converterArguments["operationTypeCodeTop"] : "30";

                    currentUUT = apiRef.CreateUUTReport(
                        uutOperator,
                        _pendingPartNumber ?? string.Empty,
                        _pendingRevision ?? string.Empty,
                        serial,
                        apiRef.GetOperationType(opCode),
                        _pendingPartNumber ?? string.Empty,
                        _pendingRevision ?? string.Empty);

                    if (!string.IsNullOrEmpty(batchCode))
                        currentUUT.BatchSerialNumber = batchCode;
                    if (_pendingDate != default)
                        currentUUT.StartDateTime = _pendingDate;
                    // Station name priority:
                    // 1. Test ID from file (ground truth — which machine actually ran the test)
                    // 2. First operator token (when stationNameFromOperator=true)
                    // 3. stationName converter arg (fallback for old firmware without Test ID,
                    //    or explicit override — note: base class auto-fills this with the
                    //    WATS Client machine name, so it is only used when no file value exists)
                    string resolvedStationName = null;
                    if (!string.IsNullOrEmpty(_pendingStationName))
                        resolvedStationName = _pendingStationName;
                    else if (!string.IsNullOrEmpty(serialStationName))
                        resolvedStationName = serialStationName;
                    else if (converterArguments.ContainsKey("stationName") && !string.IsNullOrEmpty(converterArguments["stationName"]))
                        resolvedStationName = converterArguments["stationName"];
                    if (!string.IsNullOrEmpty(resolvedStationName))
                        currentUUT.StationName = resolvedStationName;
                    // When not using stationNameFromOperator, add operator2 as misc info (legacy behaviour)
                    if (!stationFromOp && !string.IsNullOrEmpty(operator2))
                        currentUUT.AddMiscUUTInfo("Operator 2", operator2);
                    if (!string.IsNullOrEmpty(_pendingSoftwareVersion))
                        currentUUT.SequenceVersion = _pendingSoftwareVersion;
                    if (!string.IsNullOrEmpty(_pendingSoftwareFileName))
                        currentUUT.SequenceName = _pendingSoftwareFileName;
                    if (string.Equals(_pendingSide, "T", StringComparison.OrdinalIgnoreCase))
                        currentUUT.AddMiscUUTInfo("Side", "Top");
                    else if (string.Equals(_pendingSide, "B", StringComparison.OrdinalIgnoreCase))
                        currentUUT.AddMiscUUTInfo("Side", "Bottom");

                    _componentGroups.Clear();
                    _totalExecutionTimeMs = 0;
                    readState = ReportReadState.InTest;
                    break;
                }

                case "Step":
                    ProcessStepLine(match.completeLine);
                    break;

                case "BlockEnd":
                    // @ at end of block: submit current UUT; next block (if any) re-enters InHeader
                    SubmitCurrentUUT();
                    _firstBlockDone = true;
                    readState = ReportReadState.InHeader;
                    break;
            }

            return true;
        }

        // ── Step line parser ─────────────────────────────────────────────────────

        private void ProcessStepLine(string line)
        {
            // Extract all quoted fields
            var quoted = new List<string>();
            int searchFrom = 0;

            // Skip the 6-digit step number at the start
            int firstSpace = line.IndexOf(' ');
            if (firstSpace < 0) return;
            searchFrom = firstSpace;

            foreach (Match m in QuotedField.Matches(line, searchFrom))
                quoted.Add(m.Groups[1].Value.Trim());

            // Extract bare integers (H-pin, L-pin) between quoted fields
            // They appear after the 3rd quoted field (comment/net ref) and before the tolerances
            var bareInts = new List<int>();
            // Find position after 3rd quoted group
            int afterThird = -1;
            int qcount = 0;
            foreach (Match m in QuotedField.Matches(line, searchFrom))
            {
                qcount++;
                if (qcount == 3) { afterThird = m.Index + m.Length; break; }
            }
            if (afterThird >= 0)
            {
                // Find where the first +/- tolerance starts
                int tolStart = line.IndexOf('%', afterThird);
                while (tolStart > 0 && line[tolStart - 1] != '+' && line[tolStart - 1] != '-' && tolStart > afterThird)
                    tolStart--;
                // Back up past the digits
                while (tolStart > afterThird && (char.IsDigit(line[tolStart - 1]) || line[tolStart - 1] == '.'))
                    tolStart--;
                // Now extract bare ints in the window between afterThird and tolStart
                string intWindow = tolStart > afterThird ? line.Substring(afterThird, tolStart - afterThird) : "";
                foreach (Match m in new Regex(@"\b(\d+)\b").Matches(intWindow))
                {
                    if (int.TryParse(m.Groups[1].Value, out int v))
                        bareInts.Add(v);
                }
            }

            // Extract tolerances
            var tolerances = new List<double>();
            foreach (Match m in Tolerance.Matches(line))
            {
                if (double.TryParse(m.Groups[1].Value, NumberStyles.Any, InvariantCulture, out double t))
                    tolerances.Add(t);
            }

            // Field layout (quoted):
            // [0] Parts / component ref
            // [1] Value (component value or pin description)
            // [2] Comment / net ref
            // [3] Judgement   (PASS / ...... / JP)
            // [4] Measuring mode
            // [5] Range
            // [6] Measuring time
            // [7] Probe config
            // [8] Ref value with unit    (e.g. "409.5 O" or "100.0 nF")
            // [9] Test value 1 with unit (e.g. "409.5 O" or "........")
            // [10] Test value 2 with unit (optional, "........" if unused)
            // [11] Function              ("**", "OP", "SH", "JP", "D", "E", "F", "E ")
            // [12] Optional model path   (AT33 variant only)

            if (quoted.Count < 8) return;

            string parts = quoted[0];
            string valueFld = quoted[1];
            string comment = quoted[2];

            // Judgement is the first field that is PASS/FAIL/......
            // Function is the last meaningful quoted field before optional model path
            string judgement = "";
            string refValStr = "";
            string refUnit = "";
            string measValStr = "";
            string measUnit = "";
            string function = "";

            // Find judgement field (contains PASS, FAIL, OPEN, SHORT, or 6-dot skip marker).
            // All verdict fields in ATD files have trailing spaces (e.g. "PASS   "), so trim before comparing.
            int judgementIdx = -1;
            for (int i = 3; i < quoted.Count; i++)
            {
                string vt = quoted[i].Trim();
                if (vt == "PASS" || vt == "FAIL" || vt == "OPEN" || vt == "SHORT" ||
                    vt == "......")
                {
                    judgementIdx = i;
                    judgement = vt;
                    break;
                }
            }

            // Ref value is the quoted field just before judgement (index judgementIdx - 1 if it looks like a measurement)
            // Actually layout: [3]=ref, [4]=meas1, [5]=meas2, [6]=judgement  — but varies
            // Safer: scan for fields that look like measurements before judgement
            var measureFields = new List<string>();
            for (int i = 3; i < (judgementIdx > 0 ? judgementIdx : quoted.Count); i++)
                measureFields.Add(quoted[i]);

            if (measureFields.Count >= 1)
            {
                ParseMeasurement(measureFields[0], out refValStr, out refUnit);
            }
            // When 3 measurement fields are present (no dots on the 3rd), TAKAYA uses the
            // final (3rd) field as the decisive measurement; the middle field is an intermediate
            // probe pass that can be out of the acceptance window.
            if (measureFields.Count >= 3 && !IsDots(measureFields[2]))
            {
                ParseMeasurement(measureFields[2], out measValStr, out measUnit);
            }
            else if (measureFields.Count >= 2 && !IsDots(measureFields[1]))
            {
                ParseMeasurement(measureFields[1], out measValStr, out measUnit);
            }
            else if (measureFields.Count >= 1)
            {
                measValStr = refValStr;
                measUnit = refUnit;
            }

            // Function is the last quoted field that is a known function code
            for (int i = quoted.Count - 1; i > judgementIdx; i--)
            {
                string v = quoted[i].Trim();
                if (v == "**" || v == "OP" || v == "SH" || v == "JP" ||
                    v == "D" || v == "E" || v == "F" || v == "E ")
                {
                    function = v;
                    break;
                }
            }
            if (string.IsNullOrEmpty(function) && judgementIdx >= 0)
            {
                // Fallback: last short quoted field
                for (int i = quoted.Count - 1; i > judgementIdx; i--)
                {
                    if (quoted[i].Length <= 4) { function = quoted[i].Trim(); break; }
                }
            }

            // If jumped, mark skipped
            bool isJump = judgement == "......" || function.Equals("JP", StringComparison.OrdinalIgnoreCase);

            // Build step name
            string group = ComponentTypeGroup(parts);
            string stepName = (group != parts)
                ? string.Format("{0}_{1}", parts, comment)
                : string.Format("{0}_{1}", comment, valueFld);

            // Find execution time field ("X.X ms") among quoted fields after judgement
            double stepTimeMs = 0;
            if (judgementIdx >= 0)
            {
                for (int i = judgementIdx + 1; i < quoted.Count; i++)
                {
                    Match tm = TimeField.Match(quoted[i]);
                    if (tm.Success)
                    {
                        double.TryParse(tm.Groups[1].Value, NumberStyles.Any, InvariantCulture, out stepTimeMs);
                        break;
                    }
                }
            }
            _totalExecutionTimeMs += stepTimeMs;

            bool groupByType = converterArguments.ContainsKey("GroupByComponentType") &&
                               converterArguments["GroupByComponentType"].Equals("true", StringComparison.OrdinalIgnoreCase);
            SequenceCall seq = groupByType ? GetOrAddSequenceCall(group) : currentUUT.GetRootSequenceCall();

            string reportText = string.Format("Parts:{0} Value:{1} Ref:{2} Meas:{3} Function:{4}",
                parts, valueFld, measureFields.Count > 0 ? measureFields[0] : "", measValStr.Length > 0 ? measValStr + " " + measUnit : "", function);

            // Parse ref value and limits (needed for both single-step and loop paths)
            if (!double.TryParse(refValStr, NumberStyles.Any, InvariantCulture, out double refValue))
            {
                // Non-numeric ref — log as single step with no test (jump/skip already handled above)
                if (!isJump)
                {
                    NumericLimitStep s = seq.AddNumericLimitStep(stepName);
                    if (stepTimeMs > 0) s.StepTime = stepTimeMs / 1000.0;
                    s.ReportText = reportText;
                    s.Status = judgement == "PASS" ? StepStatusType.Passed : StepStatusType.Failed;
                }
                return;
            }

            double highTol = tolerances.Count > 0 ? Math.Abs(tolerances[0]) / 100.0 : 0.1;
            double lowTol = tolerances.Count > 1 ? Math.Abs(tolerances[1]) / 100.0 : highTol;
            double highLimit = refValue + refValue * highTol;
            double lowLimit = refValue - refValue * lowTol;
            string unit = NormalizeUnit(!string.IsNullOrEmpty(measUnit) ? measUnit : refUnit);

            // When two non-dots measurement fields exist, TAKAYA ran two probe attempts.
            // Log them as a looped step so both tries are preserved in the report.
            bool isDualMeasurement = !isJump && measureFields.Count >= 3 && !IsDots(measureFields[2]);
            if (isDualMeasurement)
            {
                ParseMeasurement(measureFields[1], out string meas1Str, out string meas1Unit);
                ParseMeasurement(measureFields[2], out string meas2Str, out string meas2Unit);
                double.TryParse(meas1Str, NumberStyles.Any, InvariantCulture, out double meas1Val);
                double.TryParse(meas2Str, NumberStyles.Any, InvariantCulture, out double meas2Val);
                string unit1 = NormalizeUnit(!string.IsNullOrEmpty(meas1Unit) ? meas1Unit : refUnit);
                string unit2 = NormalizeUnit(!string.IsNullOrEmpty(meas2Unit) ? meas2Unit : refUnit);

                bool iter0Pass = EvaluateFunctionTest(function, meas1Val, lowLimit, highLimit, refValue);
                bool iter1Pass = judgement == "PASS";
                int passedCount = (iter0Pass ? 1 : 0) + (iter1Pass ? 1 : 0);
                int failedCount = 2 - passedCount;

                // Summary step: shows final-iteration value, total counts, ends at index 2 (1-based = last of 2 iterations)
                NumericLimitStep summary = seq.StartLoop<NumericLimitStep>(stepName, (short)2, (short)passedCount, (short)failedCount, (short)2);
                if (stepTimeMs > 0) summary.StepTime = stepTimeMs / 1000.0;
                summary.ReportText = reportText;
                summary.Status = iter1Pass ? StepStatusType.Passed : StepStatusType.Failed;
                AddFunctionTest(summary, function, meas2Val, lowLimit, highLimit, refValue, unit2);

                // Iteration 0 — intermediate probe attempt
                NumericLimitStep iter0 = seq.AddNumericLimitStep(stepName, 0);
                iter0.Status = iter0Pass ? StepStatusType.Passed : StepStatusType.Failed;
                AddFunctionTest(iter0, function, meas1Val, lowLimit, highLimit, refValue, unit1);

                // Iteration 1 — final decisive measurement
                NumericLimitStep iter1 = seq.AddNumericLimitStep(stepName, 1);
                iter1.Status = iter1Pass ? StepStatusType.Passed : StepStatusType.Failed;
                AddFunctionTest(iter1, function, meas2Val, lowLimit, highLimit, refValue, unit2);

                seq.StopLoop();
                return;
            }

            // Single-measurement path
            NumericLimitStep step = seq.AddNumericLimitStep(stepName);
            if (stepTimeMs > 0)
                step.StepTime = stepTimeMs / 1000.0;
            step.ReportText = reportText;

            if (isJump)
            {
                step.Status = StepStatusType.Skipped;
                return;
            }
            step.Status = judgement == "PASS" ? StepStatusType.Passed : StepStatusType.Failed;

            if (!double.TryParse(measValStr, NumberStyles.Any, InvariantCulture, out double measure))
                measure = double.NaN;

            AddFunctionTest(step, function, measure, lowLimit, highLimit, refValue, unit);
        }

        private static void AddFunctionTest(NumericLimitStep step, string function, double measure,
            double lowLimit, double highLimit, double refValue, string unit)
        {
            switch (function)
            {
                case "**":
                    step.AddTest(measure, CompOperatorType.GELE, lowLimit, highLimit, unit); break;
                case "SH":
                    step.AddTest(measure, CompOperatorType.LT, 10, unit); break;
                case "OP":
                    step.AddTest(measure, CompOperatorType.GT, 100, unit); break;
                case "D":
                    step.AddTest(measure, CompOperatorType.LTGE, lowLimit, highLimit, unit); break;
                case "E":
                case "E ":
                    step.AddTest(measure, CompOperatorType.LE, refValue, unit); break;
                case "F":
                    step.AddTest(measure, CompOperatorType.GE, refValue, unit); break;
                default:
                    step.AddTest(measure, CompOperatorType.GELE, lowLimit, highLimit, unit); break;
            }
        }

        private static bool EvaluateFunctionTest(string function, double measure,
            double lowLimit, double highLimit, double refValue)
        {
            if (double.IsNaN(measure)) return false;
            switch (function)
            {
                case "**":
                case "D":   return measure >= lowLimit && measure <= highLimit;
                case "SH":  return measure < 10;
                case "OP":  return measure > 100;
                case "E":
                case "E ":  return measure <= refValue;
                case "F":   return measure >= refValue;
                default:    return measure >= lowLimit && measure <= highLimit;
            }
        }

        // ── Helpers ───────────────────────────────────────────────────────────────

        private bool SubmitCurrentUUT()
        {
            if (currentUUT != null &&
                !string.IsNullOrEmpty(currentUUT.SerialNumber) &&
                !string.IsNullOrEmpty(currentUUT.PartNumber))
            {
                currentUUT.Status = _uutStatusFromTester;
                if (_totalExecutionTimeMs > 0)
                    currentUUT.ExecutionTime = _totalExecutionTimeMs / 1000.0; // Convert ms to seconds
                apiRef.Submit(currentUUT);
                currentUUT = null;
                _componentGroups.Clear();
                _totalExecutionTimeMs = 0;
                return true;
            }
            return false;
        }

        /// <summary>
        /// Parses the model string into its components.
        /// Format: &lt;PartNumber&gt;[T|B][_panel|_single][_&lt;SoftwareVersion&gt;.&lt;SoftwareFileName&gt;]
        /// E.g. "MBEU254288-3T_V2.SWX2" → partNumber="MBEU254288-3", revision="V2.SWX2",
        ///       side="T", softwareVersion="V2", softwareFileName="SWX2"
        /// </summary>
        private static void ParseModel(string model, out string partNumber, out string revision,
            out string side, out string softwareVersion, out string softwareFileName)
        {
            side = string.Empty;
            softwareVersion = string.Empty;
            softwareFileName = string.Empty;

            int underscoreIdx = model.IndexOf('_');
            string left, right;
            if (underscoreIdx > 0)
            {
                left = model.Substring(0, underscoreIdx);
                right = model.Substring(underscoreIdx + 1);
                // Drop leading "panel" or "single" segments (customer: safely ignore)
                right = Regex.Replace(right, @"^(?:panel|single)_?", string.Empty, RegexOptions.IgnoreCase).Trim('_');
            }
            else
            {
                left = model;
                right = string.Empty;
            }
            // Strip trailing T or B side indicator (only if preceded by a digit)
            if (left.Length >= 2 && char.IsDigit(left[left.Length - 2]))
            {
                char last = left[left.Length - 1];
                if (last == 'T' || last == 'B' || last == 't' || last == 'b')
                {
                    side = char.ToUpperInvariant(last).ToString();
                    left = left.Substring(0, left.Length - 1);
                }
            }
            partNumber = left;
            revision = right;

            // Split revision "V2.SWX2" into softwareVersion="V2" and softwareFileName="SWX2"
            if (!string.IsNullOrEmpty(right))
            {
                int dotIdx = right.IndexOf('.');
                if (dotIdx > 0)
                {
                    softwareVersion = right.Substring(0, dotIdx);
                    softwareFileName = right.Substring(dotIdx + 1);
                }
                else
                {
                    softwareVersion = right;
                }
            }
        }

        /// <summary>
        /// Parses "&lt;BatchCode&gt; - &lt;Serial&gt; (...)" into its components.
        /// BatchCode is the leading digit sequence; Serial is the alphanumeric UUT identifier.
        /// </summary>
        private static void ParseSerialAndBatch(string raw, out string serial, out string batchCode)
        {
            int parenIdx = raw.IndexOf('(');
            string sn = parenIdx > 0 ? raw.Substring(0, parenIdx).Trim() : raw.Trim();
            int dashIdx = sn.IndexOf('-');
            if (dashIdx > 0)
            {
                batchCode = sn.Substring(0, dashIdx).Trim();
                serial = sn.Substring(dashIdx + 1).Trim();
            }
            else
            {
                batchCode = string.Empty;
                serial = sn;
            }
        }

        /// <summary>
        /// Parses "... (&lt;Operator1&gt;[ - &lt;Operator2&gt;])" to extract operators.
        /// The first token is always returned as operator1; the second (after " - ") as operator2.
        /// </summary>
        private static void ParseOperators(string raw, out string operator1, out string operator2)
        {
            operator1 = string.Empty;
            operator2 = string.Empty;
            int open = raw.IndexOf('(');
            int close = raw.LastIndexOf(')');
            if (open < 0 || close <= open) return;
            string inner = raw.Substring(open + 1, close - open - 1).Trim();
            int sep = inner.IndexOf(" - ", StringComparison.Ordinal);
            if (sep > 0)
            {
                operator1 = inner.Substring(0, sep).Trim();
                operator2 = inner.Substring(sep + 3).Trim();
            }
            else
            {
                operator1 = inner;
            }
        }

        private static void ParseMeasurement(string field, out string value, out string unit)
        {
            // Field looks like "409.5 O" or "100.0 nF" or " 97.2 nF" or "0.824 V"
            string f = field.Trim();
            int lastSpace = f.LastIndexOf(' ');
            if (lastSpace > 0)
            {
                value = f.Substring(0, lastSpace).Trim();
                unit = f.Substring(lastSpace + 1).Trim();
            }
            else
            {
                value = f;
                unit = "";
            }
        }

        private static bool IsDots(string s) =>
            string.IsNullOrEmpty(s) || s.Replace(".", "").Replace(" ", "").Length == 0;

        private static string NormalizeUnit(string unit)
        {
            // Map ATD unit abbreviations to standard
            switch (unit.ToUpper())
            {
                case "O": return "OHM";
                case "KO": return "KOHM";
                case "MO": return "MOHM";
                case "NF": return "NF";
                case "PF": return "PF";
                case "UF": return "UF";
                case "V": return "V";
                case "MV": return "MV";
                default: return unit;
            }
        }

        private static string ComponentTypeGroup(string parts)
        {
            int i = 0;
            while (i < parts.Length && char.IsLetter(parts[i])) i++;
            if (i > 0 && i < parts.Length && char.IsDigit(parts[i]))
                return parts.Substring(0, i);
            return parts;
        }

        private SequenceCall GetOrAddSequenceCall(string groupName)
        {
            if (!_componentGroups.TryGetValue(groupName, out SequenceCall subSeq))
            {
                subSeq = currentUUT.GetRootSequenceCall().AddSequenceCall(groupName);
                _componentGroups[groupName] = subSeq;
            }
            return subSeq;
        }
    }
}
