using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Policy;
using System.Text;
using Virinco.WATS.Integration.TextConverter;
using Virinco.WATS.Interface;

namespace TAKAYA_FlyingProbeConverter
{
    public class ATD_Converter : TextConverterBase
    {
        bool IsNumeric(string input)
        {
            double res;
            return (Double.TryParse(input, NumberStyles.AllowDecimalPoint | NumberStyles.AllowLeadingSign | NumberStyles.AllowTrailingWhite, currentCulture.NumberFormat, out res));
        }

        public ATD_Converter() : base()
        {
            converterArguments["GroupByComponentType"] = "false";
            converterArguments["testModeType"] = "Active";
            converterArguments["operationTypeCode"] = "30";
        }

        UUTStatusType uutStatusFromTester;
        bool readFirstHeader = false;
        readonly Dictionary<string, SequenceCall> _componentGroups = new Dictionary<string, SequenceCall>();

        public bool SubmitCurrentUUT()
        {
            if (!String.IsNullOrEmpty(currentUUT.SerialNumber) && !String.IsNullOrEmpty(currentUUT.PartNumber))
            {
                if (uutStatusFromTester != currentUUT.Status)
                    currentUUT.AddMiscUUTInfo("TesterStatus", String.Format("FPT={0}, Active={1}", uutStatusFromTester, currentUUT.Status));
                currentUUT.Status = uutStatusFromTester;
                //Adjust operation type to SW Debug if file name contains _debug
                if (apiRef.ConversionSource.SourceFile.Name.ToLower().Contains("_debug"))
                    currentUUT.OperationType = apiRef.GetOperationType("10");
                apiRef.Submit(currentUUT); //Submit to server
                _componentGroups.Clear();
                readFirstHeader = false; //Prepare for next report
                return true;
            }
            else
            {
                ParseError("Missing SN/PN", apiRef.ConversionSource.SourceFile.FullName);
                return false;
            }
        }

        protected override bool ProcessMatchedLine(TextConverterBase.SearchFields.SearchMatch match, ref TextConverterBase.ReportReadState readState)
        {
            if (match == null)
            {
                return SubmitCurrentUUT(); //EOF
            }
            switch (match.matchField.fieldName)
            {
                case "TestHeading1":
                    if (readFirstHeader)
                        currentReportState = ReportReadState.InTest;
                    else
                        readFirstHeader = true;
                    break;
                case "Group":
                    if (match.completeLine != "* GROUP No.1   *") //Skip first group, submit following 
                    {
                        SubmitCurrentUUT();
                        if (match.matchField.fieldName == "Group")
                        {
                            UUTReport newUUT = apiRef.CreateUUTReport(currentUUT.Operator, currentUUT.PartNumber, currentUUT.PartRevisionNumber, "", currentUUT.OperationType, currentUUT.SequenceName, currentUUT.SequenceVersion);
                            newUUT.StartDateTime = currentUUT.StartDateTime;
                            newUUT.ExecutionTime = currentUUT.ExecutionTime;
                            newUUT.StationName = currentUUT.StationName;
                            currentUUT = newUUT;
                        }
                    }
                    break;

                case "PassFail":
                    uutStatusFromTester = (UUTStatusType)match.results[0];
                    break;
                case "NoTest":
                    uutStatusFromTester = UUTStatusType.Failed;
                    break;

                case "Step":
                    if (!IsNumeric(match.completeLine.Substring(0, 1)))
                        return true;

                    string stepParts = ((string)match.GetSubField("Parts")).Trim();
                    string netH = match.ExistSubField("Net_Name_Hpin") ? ((string)match.GetSubField("Net_Name_Hpin")).Trim() : "";
                    string netL = match.ExistSubField("Net_Name_Lpin") ? ((string)match.GetSubField("Net_Name_Lpin")).Trim() : "";
                    string comment = ((string)match.GetSubField("Comment")).Trim();
                    string group = ComponentTypeGroup(stepParts);
                    string stepName = (group != stepParts)
                        ? string.Format("{0}_{1}_{2}", stepParts, netH, netL)  // component ref: C476_GNDM_...
                        : string.Format("{0}_{1}_{2}", comment, netH, netL);   // category: 153-9_PWR_VOUT_GND
                    bool groupByType = converterArguments.ContainsKey("GroupByComponentType") &&
                        converterArguments["GroupByComponentType"].Equals("true", StringComparison.OrdinalIgnoreCase);
                    SequenceCall seq = groupByType
                        ? GetOrAddSequenceCall(group)
                        : currentUUT.GetRootSequenceCall();
                    NumericLimitStep step = seq.AddNumericLimitStep(stepName);

                    step.ReportText = String.Format("#{0} {1} Tol+:{2} Tol-:{3} H-pin:{4} L-pin:{5}", match.GetSubField("Step_number"), match.GetSubField("Comment"), match.GetSubField("Tolerance+"), match.GetSubField("Tolerance-"), match.GetSubField("H-pin"), match.GetSubField("L-pin"));
                    if (!string.IsNullOrEmpty(netH))
                        step.ReportText += String.Format(" {0} {1}", netH, netL);

                    if (((string)match.GetSubField("Judgement")).Trim().ToLower() == "jump" ||
                        ((string)match.GetSubField("Function")).Trim().ToLower() == "jp")
                    {
                        step.Status = StepStatusType.Skipped;
                        return true;
                    }

                    double measure, refValue, highTolerance, lowTolerance, highLimit, lowLimit;
                    string refUnit = (string)match.GetSubField("RefUnit");
                    refUnit = refUnit.ToUpper();
                    //Calculate limits
                    highTolerance = double.Parse(((string)match.GetSubField("Tolerance+")).TrimEnd(new char[] { '%', ' ' })) / 100;
                    lowTolerance = double.Parse(((string)match.GetSubField("Tolerance-")).TrimEnd(new char[] { '%', ' ' })) / 100;
                    if (!IsNumeric((string)match.GetSubField("RefValue")))
                        break;
                    refValue = (double)ConvertStringToAny((string)match.GetSubField("RefValue"), typeof(double), null, currentCulture);
                    highLimit = refValue + (refValue * highTolerance);
                    lowLimit = refValue + (refValue * lowTolerance);


                    string measUnit = "";
                    if (IsNumeric((string)match.GetSubField("TestValue2"))) //Skip test value 1 if failed and measured again
                    {
                        measure = (double)ConvertStringToAny((string)match.GetSubField("TestValue2"), typeof(double), null, currentCulture);
                        measUnit = (string)match.GetSubField("Unit2");
                    }
                    else if (IsNumeric((string)match.GetSubField("TestValue1")))
                    {
                        measure = (double)ConvertStringToAny((string)match.GetSubField("TestValue1"), typeof(double), null, currentCulture);
                        measUnit = (string)match.GetSubField("Unit1");
                    }
                    else
                        measure = double.NaN;
                    measUnit = measUnit.ToUpper();
                    if (measUnit != refUnit)
                    {
                        if (converterArguments.ContainsKey("UnitCalcPreference") && converterArguments["UnitCalcPreference"].ToLower() == "limits") //To better support TSA, use this argument to use limits unit
                        {
                            double factorMeas = measUnit.Length <= 1 ? 1 :
                                measUnit[0] == 'K' ? 1000 : measUnit[0] == 'M' ? 1000000 : measUnit[0] == 'U' ? 0.000001 : measUnit[0] == 'N' ? 0.000000001 : 1;
                            double factorRef = refUnit.Length <= 1 ? 1 :
                                refUnit[0] == 'K' ? 1000 : refUnit[0] == 'M' ? 1000000 : refUnit[0] == 'U' ? 0.000001 : refUnit[0] == 'N' ? 0.000000001 : 1;
                            double factorAdjustMeasWith = factorMeas / factorRef;
                            measure = measure * factorAdjustMeasWith;
                            measUnit = refUnit;
                        }
                        else //Default, use Measure Unit on limits
                        {
                            double factorMeas = measUnit.Length <= 1 ? 1 :
                                measUnit[0] == 'K' ? 1000 : measUnit[0] == 'M' ? 1000000 : measUnit[0] == 'U' ? 0.000001 : measUnit[0] == 'N' ? 0.000000001 : 1;
                            double factorRef = refUnit.Length <= 1 ? 1 :
                                refUnit[0] == 'K' ? 1000 : refUnit[0] == 'M' ? 1000000 : refUnit[0] == 'U' ? 0.000001 : refUnit[0] == 'N' ? 0.000000001 : 1;
                            double factorAdjustRefWith = factorMeas * factorRef;
                            lowLimit = lowLimit * factorAdjustRefWith;
                            highLimit = highLimit * factorAdjustRefWith;
                            refUnit = measUnit;
                        }
                    }

                    //Check Function
                    switch ((string)match.GetSubField("Function"))
                    {
                        case "**":
                            step.AddTest(measure, CompOperatorType.GELE, lowLimit, highLimit, measUnit); break;
                        case "SH":
                            step.AddTest(measure, CompOperatorType.LT, 10, measUnit); break;
                        case "OP":
                            step.AddTest(measure, CompOperatorType.GT, 100, measUnit); break;
                        case "D":
                            step.AddTest(measure, CompOperatorType.LTGE, lowLimit, highLimit, measUnit); break;
                        case "E":
                            step.AddTest(measure, CompOperatorType.LE, refValue, measUnit); break;
                        case "F":
                            step.AddTest(measure, CompOperatorType.GE, refValue, measUnit); break;
                    }
                    break;
            }
            return true;
        }

        public ATD_Converter(IDictionary<string, string> args) : base(args)
        {
            if (!args.ContainsKey("cultureCode")) //Invariantculture gives decimal symbol period
            {
                this.currentCulture = CultureInfo.InvariantCulture;
                this.searchFields.culture = CultureInfo.InvariantCulture;
            }

            searchFields.AddExactField("TestHeading1", ReportReadState.InHeader, "@", null, typeof(string), true);
            searchFields.AddExactField(UUTField.PartNumber, ReportReadState.InHeader, "Model:", null, typeof(string));
            searchFields.AddExactField(UUTField.StartDateTime, ReportReadState.InHeader, "DATE ", "d/M/yyyy HH:mm:ss", typeof(DateTime));
            searchFields.AddRegExpField(UUTField.ExecutionTime, ReportReadState.InHeader, @"^Test time:(?<Time>\d+) s", null, typeof(double));
            searchFields.AddExactField(UUTField.StationName, ReportReadState.InHeader, "Test ID :", null, typeof(string));
            searchFields.AddExactField(UUTField.SequenceVersion, ReportReadState.InHeader, "Model:", null, typeof(string));
            searchFields.AddExactField(UUTField.Operator, ReportReadState.InHeader, "User name :", null, typeof(string));

            searchFields.AddExactField(UUTField.SerialNumber, ReportReadState.Unknown, "Serial No.:", null, typeof(string));

            searchFields.AddExactField("Group", ReportReadState.InTest, "* GROUP No.", null, typeof(string));
            searchFields.AddRegExpField("NoTest", ReportReadState.InTest, @"^[* ]+NO-TEST[* ]+", null, typeof(string));
            searchFields.AddRegExpField("PassFail", ReportReadState.InTest, @"^[* ]+(?<Result>(PASS|FAIL))[* ]+", null, typeof(UUTStatusType));

            SearchFields.ExactSearchField field;
            field = searchFields.AddExactField("Step", ReportReadState.InTest, "", null, typeof(string));
            field.delimiters = new char[] { '\t' };
            field.AddSubField("Group No.", typeof(string));
            field.AddSubField("Step_number", typeof(string));
            field.AddSubField("Parts", typeof(string));
            field.AddSubField("Value", typeof(string));
            field.AddSubField("Unit", typeof(string));
            field.AddSubField("Comment", typeof(string));
            field.AddSubField("Location", typeof(string));
            field.AddSubField("H-pin", typeof(string));
            field.AddSubField("Net_Name_Hpin", typeof(string));
            field.AddSubField("L-pin", typeof(string));
            field.AddSubField("Net_Name_Lpin", typeof(string));
            field.AddSubField("G-P1", typeof(string));
            field.AddSubField("Net_Name_G-P1", typeof(string));
            field.AddSubField("G-P2", typeof(string));
            field.AddSubField("Net_Name_G-P2", typeof(string));
            field.AddSubField("Judgement", typeof(string));
            field.AddSubField("Ref._Value_(EL)", typeof(string));
            field.AddSubField("Ref._Value_(Fig)", typeof(string));
            field.AddSubField("RefValue", typeof(string));
            field.AddSubField("RefUnit", typeof(string));
            field.AddSubField("Test_Value_1_(EL)", typeof(string));
            field.AddSubField("Test_Value_1_(Fig)", typeof(string));
            field.AddSubField("TestValue1", typeof(string));
            field.AddSubField("Unit1", typeof(string));
            field.AddSubField("Test_Value_2_(EL)", typeof(string));
            field.AddSubField("Test_Value_2_(Fig)", typeof(string));
            field.AddSubField("TestValue2", typeof(string));
            field.AddSubField("Unit2", typeof(string));
            field.AddSubField("Date_of_test", typeof(string));
            field.AddSubField("Time_of_test", typeof(string));
            field.AddSubField("Measuring_mode", typeof(string));
            field.AddSubField("Measuring_range", typeof(string));
            field.AddSubField("Measuring_time", typeof(string));
            field.AddSubField("Tolerance+", typeof(string));
            field.AddSubField("Tolerance-", typeof(string));
            field.AddSubField("Access_probe", typeof(string));
            field.AddSubField("Total_Pass_no", typeof(string));
            field.AddSubField("Total_NG_No", typeof(string));
            field.AddSubField("Daily_PASS_no", typeof(string));
            field.AddSubField("Daily_NG_No", typeof(string));
            field.AddSubField("Model", typeof(string));
            field.AddSubField("Testing_time", typeof(string));
            field.AddSubField("Serial_No1", typeof(string));
            field.AddSubField("Tester_ID", typeof(string));
            field.AddSubField("Index", typeof(string));
            field.AddSubField("XCoor1", typeof(string));
            field.AddSubField("YCoor1", typeof(string));
            field.AddSubField("XCoor2", typeof(string));
            field.AddSubField("YCoor2", typeof(string));
            field.AddSubField("XCoor3", typeof(string));
            field.AddSubField("YCoor3", typeof(string));
            field.AddSubField("XCoor4", typeof(string));
            field.AddSubField("YCoor4", typeof(string));
            field.AddSubField("Rack_No", typeof(string));
            field.AddSubField("A/B_Side", typeof(string));
            field.AddSubField("Function", typeof(string));
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
                subSeq = currentUUT.GetRootSequenceCall().AddSequenceCall(groupName);
                _componentGroups[groupName] = subSeq;
            }
            return subSeq;
        }
    }
}
