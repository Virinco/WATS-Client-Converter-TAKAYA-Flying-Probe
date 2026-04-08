using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using Virinco.WATS.Integration.TextConverter;
using Virinco.WATS.Interface;

namespace TAKAYA_FlyingProbeConverter
{
    /// <summary>
    /// Converter for TAKAYA Flying Probe ATDX format (semicolon-delimited, newer format).
    /// Header structure uses "#############" section markers, steps use ';' as delimiter.
    /// </summary>
    public class ATDX_Converter : TextConverterBase
    {
        bool IsNumeric(string input)
        {
            double res;
            return Double.TryParse(input, NumberStyles.AllowDecimalPoint | NumberStyles.AllowLeadingSign | NumberStyles.AllowTrailingWhite, currentCulture.NumberFormat, out res);
        }

        public ATDX_Converter() : base()
        {
            converterArguments["GroupByComponentType"] = "true";
            converterArguments["operationTypeCode"] = "30";

            
        }

        UUTStatusType _uutStatus = UUTStatusType.Passed;
        int _attemptCount = 0;
        bool _hasSeenGroup = false;
        SequenceCall _currentGroupSequence;
        readonly Dictionary<string, SequenceCall> _componentGroups = new Dictionary<string, SequenceCall>();

        private void SubmitCurrentUUT()
        {
            if (String.IsNullOrEmpty(currentUUT.SerialNumber) || String.IsNullOrEmpty(currentUUT.PartNumber))
            {
                ParseError("Missing SN/PN", apiRef.ConversionSource.SourceFile.FullName);
                return;
            }

            // Strip any suffix after first whitespace from part number (e.g. "289-0254-4.0 DEV25042")
            int spaceIdx = currentUUT.PartNumber.IndexOf(' ');
            if (spaceIdx > 0)
            {
                string suffix = currentUUT.PartNumber.Substring(spaceIdx + 1).Trim();
                currentUUT.PartNumber = currentUUT.PartNumber.Substring(0, spaceIdx);
                if (!string.IsNullOrEmpty(suffix))
                    currentUUT.AddMiscUUTInfo("barcode_suffix", suffix);
            }
            // SequenceVersion also comes from the Model: line — strip the same suffix
            if (!string.IsNullOrEmpty(currentUUT.SequenceVersion))
            {
                int svIdx = currentUUT.SequenceVersion.IndexOf(' ');
                if (svIdx > 0)
                    currentUUT.SequenceVersion = currentUUT.SequenceVersion.Substring(0, svIdx);
            }

            // Add synthetic time offset so each attempt gets a unique timestamp
            if (_attemptCount > 1)
                currentUUT.StartDateTime = currentUUT.StartDateTime.AddSeconds(30 * (_attemptCount - 1));

            currentUUT.Status = _uutStatus;

            apiRef.Submit(currentUUT);
            _componentGroups.Clear();
        }

        protected override bool ProcessMatchedLine(TextConverterBase.SearchFields.SearchMatch match, ref TextConverterBase.ReportReadState readState)
        {
            if (match == null)
            {
                SubmitCurrentUUT(); // EOF — submit last UUT
                currentUUT = null;  // prevent base class from submitting again
                return true;
            }

            switch (match.matchField.fieldName)
            {
                case "MainHeader":
                    currentReportState = ReportReadState.InHeader;
                    break;

                case "GroupHeader":
                    currentReportState = ReportReadState.InTest;
                    break;

                case "Group":
                    int groupNo = (int)match.GetSubField("GroupNo");
                    UUTStatusType groupVerdict = (UUTStatusType)match.GetSubField("Result");
                    if (groupNo == 1 && _hasSeenGroup)
                    {
                        // New attempt starting — submit the previous attempt's UUT
                        SubmitCurrentUUT();
                        CreateDefaultUUT();
                        _uutStatus = UUTStatusType.Passed; // reset for new attempt
                    }
                    // Worst-of: if any group FAILs, the UUT FAILs
                    if (groupVerdict == UUTStatusType.Failed)
                        _uutStatus = UUTStatusType.Failed;
                    if (groupNo == 1)
                        _attemptCount++;
                    _hasSeenGroup = true;
                    _componentGroups.Clear();
                    _currentGroupSequence = currentUUT.GetRootSequenceCall().AddSequenceCall(String.Format("Group {0}", groupNo));
                    break;

                case "Step":
                    if (string.IsNullOrEmpty(match.completeLine) || !IsNumeric(match.completeLine.Substring(0, 1)))
                        return true;

                    string stepParts = ((string)match.GetSubField("Parts")).Trim();
                    string stepFunc  = ((string)match.GetSubField("Func")).Trim();
                    string netH = ((string)match.GetSubField("Netname_H_pin")).Trim();
                    string netL = ((string)match.GetSubField("Netname_L_pin")).Trim();
                    string comment   = ((string)match.GetSubField("Comment")).Trim();
                    string group = ComponentTypeGroup(stepParts);
                    string stepName = (group != stepParts)
                        ? string.Format("{0}_{1}_{2}", stepParts, netH, netL)  // component ref: C476_GNDM_...
                        : string.Format("{0}_{1}_{2}", comment, netH, netL);   // category: 153-9_PWR_VOUT_GND
                    bool groupByType = converterArguments.ContainsKey("GroupByComponentType") &&
                        converterArguments["GroupByComponentType"].Equals("true", StringComparison.OrdinalIgnoreCase);
                    SequenceCall seq = groupByType
                        ? GetOrAddSequenceCall(group)
                        : (_currentGroupSequence ?? currentUUT.GetRootSequenceCall());
                    NumericLimitStep step = seq.AddNumericLimitStep(stepName);

                    step.ReportText = String.Format("#{0} {1} H-pin:{2} L-pin:{3}",
                        match.GetSubField("Order"),
                        match.GetSubField("Comment"),
                        match.GetSubField("H_pin"),
                        match.GetSubField("L_pin"));

                    if (!string.IsNullOrEmpty(netH))
                        step.ReportText += String.Format(" {0} {1}", netH, netL);

                    string judge = ((string)match.GetSubField("Judge")).Trim().ToUpper();
                    if (stepFunc.ToLower() == "jp" || judge == "SKIP")
                    {
                        step.Status = StepStatusType.Skipped;
                        return true;
                    }

                    string refUnit     = ((string)match.GetSubField("RefUnit")).Trim().ToUpper();
                    string refValueStr = ((string)match.GetSubField("Reference")).Trim();

                    if (!IsNumeric(refValueStr))
                        break;

                    double refValue = (double)ConvertStringToAny(refValueStr, typeof(double), null, currentCulture);

                    string tolPlusStr  = ((string)match.GetSubField("Tol_plus")).Trim().TrimEnd('%', ' ');
                    string tolMinusStr = ((string)match.GetSubField("Tol_minus")).Trim().TrimEnd('%', ' ');

                    double highLimit, lowLimit;
                    if (IsNumeric(tolPlusStr) && IsNumeric(tolMinusStr))
                    {
                        double highTolerance = double.Parse(tolPlusStr,  currentCulture) / 100;
                        double lowTolerance  = double.Parse(tolMinusStr, currentCulture) / 100;
                        highLimit = refValue + (refValue * highTolerance);
                        lowLimit  = refValue + (refValue * lowTolerance);
                    }
                    else
                    {
                        highLimit = refValue;
                        lowLimit  = refValue;
                    }

                    string measUnit;
                    double measure;
                    string testValue2Str = ((string)match.GetSubField("TestValue2")).Trim();
                    string testValue1Str = ((string)match.GetSubField("TestValue1")).Trim();

                    if (IsNumeric(testValue2Str))
                    {
                        measure  = (double)ConvertStringToAny(testValue2Str, typeof(double), null, currentCulture);
                        measUnit = ((string)match.GetSubField("Unit2")).Trim().ToUpper();
                    }
                    else if (IsNumeric(testValue1Str))
                    {
                        measure  = (double)ConvertStringToAny(testValue1Str, typeof(double), null, currentCulture);
                        measUnit = ((string)match.GetSubField("Unit1")).Trim().ToUpper();
                    }
                    else
                    {
                        measure  = double.NaN;
                        measUnit = refUnit;
                    }

                    if (measUnit != refUnit)
                    {
                        if (converterArguments.ContainsKey("UnitCalcPreference") && converterArguments["UnitCalcPreference"].ToLower() == "limits")
                        {
                            double factorMeas = UnitFactor(measUnit);
                            double factorRef  = UnitFactor(refUnit);
                            measure  = measure * (factorMeas / factorRef);
                            measUnit = refUnit;
                        }
                        else
                        {
                            double factorMeas = UnitFactor(measUnit);
                            double factorRef  = UnitFactor(refUnit);
                            double factorAdjustRefWith = factorMeas * factorRef;
                            lowLimit  = lowLimit  * factorAdjustRefWith;
                            highLimit = highLimit * factorAdjustRefWith;
                            refUnit   = measUnit;
                        }
                    }

                    switch (stepFunc)
                    {
                        case "EQ":
                            step.AddTest(measure, CompOperatorType.GELE, lowLimit, highLimit, measUnit); break;
                        case "SH":
                            step.AddTest(measure, CompOperatorType.LT, refValue, measUnit); break;
                        case "OP":
                            step.AddTest(measure, CompOperatorType.GT, refValue, measUnit); break;
                        case "LE":
                            step.AddTest(measure, CompOperatorType.LE, refValue, measUnit); break;
                        case "GE":
                            step.AddTest(measure, CompOperatorType.GE, refValue, measUnit); break;
                    }

                    // Trust the tester's judgment for step status
                    step.Status = judge == "FAIL" ? StepStatusType.Failed : StepStatusType.Passed;
                    break;
            }
            return true;
        }

        public ATDX_Converter(IDictionary<string, string> args) : base(args)
        {
            if (!args.ContainsKey("cultureCode"))
            {
                this.currentCulture = CultureInfo.InvariantCulture;
                this.searchFields.culture = CultureInfo.InvariantCulture;
            }

            // ── Header fields (matched while in InHeader state) ──────────────────
            searchFields.AddExactField(UUTField.StationName,     ReportReadState.InHeader, "Tester ID:",      null,                   typeof(string));
            searchFields.AddExactField(UUTField.PartNumber,      ReportReadState.InHeader, "Model:",          null,                   typeof(string));
            searchFields.AddExactField(UUTField.SequenceVersion, ReportReadState.InHeader, "Model:",          null,                   typeof(string));
            searchFields.AddExactField(UUTField.SerialNumber,    ReportReadState.Unknown,  "Serial number :", null,                   typeof(string));
            searchFields.AddExactField(UUTField.StartDateTime,   ReportReadState.InHeader, "Test Date:",      "d/M/yyyy HH:mm:ss",    typeof(DateTime));

            // ── Main header marker — resets state to InHeader so header fields re-parse between groups ──
            searchFields.AddExactField("MainHeader", ReportReadState.Unknown,
                "#############\"Main header\"############", null, typeof(string));

            // ── Group section marker ──
            searchFields.AddExactField("GroupHeader", ReportReadState.Unknown,
                "#############\"Group header\"#########", null, typeof(string));

            // ── Group result line, e.g. "Group No.: 1 Side: A FAIL" ──────────────
            SearchFields.RegExpSearchField groupField = searchFields.AddRegExpField("Group", ReportReadState.Unknown,
                @"^Group No\.: (?<GroupNo>\d+) Side: [AB] (?<Result>PASS|FAIL)", null, typeof(UUTStatusType));
            groupField.AddSubField("GroupNo", typeof(int));
            groupField.AddSubField("Result", typeof(UUTStatusType));

            // ── Step data (semicolon-delimited, matched only in InTest state) ─────
            // Column order matches ATDX header row:
            //   Order;Aux;M.Aux;Parts;Value;Unit;Comment;Loc.;EL;Reference;Unit;Func;
            //   +%;Unit;-%;Unit;Test.Base(Test1);Unit;Test.Base(Test2);Unit;Judge;
            //   H-pin;Netname-H-pin;L-pin;Netname-L-pin;G-P1;Netname-G-P1;G-P2;Netname-G-P2;
            //   BU1;Netname-B1;BU2;Netname-B2;BU3;Netname-B3
            SearchFields.ExactSearchField field;
            field = searchFields.AddExactField("Step", ReportReadState.InTest, "", null, typeof(string));
            field.delimiters = new char[] { ';' };
            field.AddSubField("Order",          typeof(string));
            field.AddSubField("Aux",            typeof(string));
            field.AddSubField("MAux",           typeof(string));
            field.AddSubField("Parts",          typeof(string));
            field.AddSubField("Value",          typeof(string));
            field.AddSubField("Unit",           typeof(string));
            field.AddSubField("Comment",        typeof(string));
            field.AddSubField("Loc",            typeof(string));
            field.AddSubField("EL",             typeof(string));
            field.AddSubField("Reference",      typeof(string));
            field.AddSubField("RefUnit",        typeof(string));
            field.AddSubField("Func",           typeof(string));
            field.AddSubField("Tol_plus",       typeof(string));
            field.AddSubField("UnitTolPlus",    typeof(string));
            field.AddSubField("Tol_minus",      typeof(string));
            field.AddSubField("UnitTolMinus",   typeof(string));
            field.AddSubField("TestValue1",     typeof(string));
            field.AddSubField("Unit1",          typeof(string));
            field.AddSubField("TestValue2",     typeof(string));
            field.AddSubField("Unit2",          typeof(string));
            field.AddSubField("Judge",          typeof(string));
            field.AddSubField("H_pin",          typeof(string));
            field.AddSubField("Netname_H_pin",  typeof(string));
            field.AddSubField("L_pin",          typeof(string));
            field.AddSubField("Netname_L_pin",  typeof(string));
            field.AddSubField("G_P1",           typeof(string));
            field.AddSubField("Netname_G_P1",   typeof(string));
            field.AddSubField("G_P2",           typeof(string));
            field.AddSubField("Netname_G_P2",   typeof(string));
            field.AddSubField("BU1",            typeof(string));
            field.AddSubField("Netname_B1",     typeof(string));
            field.AddSubField("BU2",            typeof(string));
            field.AddSubField("Netname_B2",     typeof(string));
            field.AddSubField("BU3",            typeof(string));
            field.AddSubField("Netname_B3",     typeof(string));
        }

        private static double UnitFactor(string unit)
        {
            if (unit.Length <= 1) return 1;
            return unit[0] == 'K' ? 1000 :
                   unit[0] == 'M' ? 1000000 :
                   unit[0] == 'U' ? 0.000001 :
                   unit[0] == 'N' ? 0.000000001 : 1;
        }

        // Returns component type prefix: "C476"→"C", "R1"→"R", "POWER_SHORTS"→"POWER_SHORTS"
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
                SequenceCall parent = _currentGroupSequence ?? currentUUT.GetRootSequenceCall();
                subSeq = parent.AddSequenceCall(groupName);
                _componentGroups[groupName] = subSeq;
            }
            return subSeq;
        }
    }
}
