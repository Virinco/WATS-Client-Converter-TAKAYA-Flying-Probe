using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using Virinco.WATS.Integration.TextConverter;
using Virinco.WATS.Interface;

namespace TAKAYA_FlyingProbeConverter
{
    public class FlyingProbeConverter : TextConverterBase
    {
        bool IsNumeric(string input)
        {
            double res;
            return (Double.TryParse(input, NumberStyles.AllowDecimalPoint | NumberStyles.AllowLeadingSign | NumberStyles.AllowTrailingWhite, currentCulture.NumberFormat, out res));
        }

        UUTStatusType uutStatusFromTester;
        //FPTFormat: A=without Net_Name, B=With Net_Name
        string converterFormat = "B"; // Assume this format
        bool readFirstHeader = false;

        protected override bool ProcessMatchedLine(TextConverterBase.SearchFields.SearchMatch match, ref TextConverterBase.ReportReadState readState)
        {
            if (match == null)
            {
                //EOF
                if (!String.IsNullOrEmpty(currentUUT.SerialNumber) && !String.IsNullOrEmpty(currentUUT.PartNumber))
                {
                    if (uutStatusFromTester != currentUUT.Status)
                    {
                        //Log difference
                        currentUUT.AddMiscUUTInfo("UUTStatusDiff", String.Format("FPT={0}, WATS={1}", uutStatusFromTester, currentUUT.Status));
                        currentUUT.Status = uutStatusFromTester; //Use status from tester
                    }
                    //Adjust operation type to SW Debug if file name contains _debug
                    if (apiRef.ConversionSource.SourceFile.Name.ToLower().Contains("_debug"))
                        currentUUT.OperationType = apiRef.GetOperationType("10");
                    SubmitUUT(); //If EndTest was missing
                    readFirstHeader = false; //Prepare for next report
                    return true;
                }
                else
                {
                    ParseError("Missing SN/PN", apiRef.ConversionSource.SourceFile.FullName);
                    return false;
                }
            }
            switch (match.matchField.fieldName)
            {
                case "TestHeading":
                    if (readFirstHeader)
                        currentReportState = ReportReadState.InTest;
                    else
                        readFirstHeader = true;
                    break;
                case "Serialnumber":
                    currentUUT.SerialNumber = (string)match.GetSubField("Revision") + (string)match.GetSubField("SerialNumber");
                    currentUUT.PartNumber = (string)match.GetSubField("PartNumber");
                    currentUUT.PartRevisionNumber = (string)match.GetSubField("Revision");
                    break;
                case "PassFail":
                    uutStatusFromTester = (UUTStatusType)match.results[0];
                    break;

                case "Step":
                    //Prepare the test step
                    NumericLimitStep step = currentUUT.GetRootSequenceCall().AddNumericLimitStep(
                        string.Format("{0}_{1}_{2}", match.GetSubField("Parts"), match.GetSubField("Value"), match.GetSubField("Function"))); //Use Parts_Value_Function as step name

                    step.ReportText = String.Format("#{0} H-pin:{1} L-pin:{2}", match.GetSubField("Step_number"), match.GetSubField("H-pin"), match.GetSubField("L-pin"));
                    if (match.ExistSubField("Net_Name_Hpin"))
                        step.ReportText += String.Format(" {0} {1}", match.GetSubField("Net_Name_Hpin"), match.GetSubField("Net_Name_Lpin"));

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
                    if (measUnit != refUnit) //If reference units differ from measure units, use measure units
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

        public FlyingProbeConverter(IDictionary<string, string> args) : base(args)
        {
            if (!args.ContainsKey("cultureCode")) //Invariantculture gives decimal symbol period
            {
                this.currentCulture = CultureInfo.InvariantCulture;
                this.searchFields.culture = CultureInfo.InvariantCulture;
            }
            if (args.ContainsKey("FPTFormat"))
            {
                converterFormat = args["FPTFormat"];
            }

            searchFields.AddExactField("TestHeading", ReportReadState.InHeader, "@", null, typeof(string), true);
            searchFields.AddRegExpField("PassFail", ReportReadState.InHeader, @"^[* ]+(?<Result>(PASS|FAIL))[* ]+", null, typeof(UUTStatusType));
            SearchFields.RegExpSearchField regExField =
            searchFields.AddRegExpField("Serialnumber", ReportReadState.Unknown, @"^Serial No.:(?<PartNumber>\w*).(?<Revision>.{3})(?<SerialNumber>.*)", null, typeof(string));
            regExField.AddSubField("SerialNumber", typeof(string), null, UUTField.SerialNumber);
            regExField.AddSubField("PartNumber", typeof(string), null, UUTField.PartNumber);
            regExField.AddSubField("Revision", typeof(string), null, UUTField.PartRevisionNumber);

            searchFields.AddExactField(UUTField.StartDateTime, ReportReadState.InHeader, "DATE ", "yy/MM/dd HH:mm:ss", typeof(DateTime));
            searchFields.AddRegExpField(UUTField.ExecutionTime, ReportReadState.InHeader, @"^Test time:(?<Time>\d+) s", null, typeof(double));
            searchFields.AddRegExpField(UUTField.SequenceName, ReportReadState.InHeader, @"^Model:(?<SequenceName>.+).SWX", null, typeof(string));
            searchFields.AddExactField(UUTField.SequenceVersion, ReportReadState.InHeader, "Version No. :", null, typeof(string));
            searchFields.AddExactField(UUTField.Operator, ReportReadState.InHeader, "User name :", null, typeof(string));
            searchFields.AddExactField(UUTField.StationName, ReportReadState.InHeader, "Test ID :", null, typeof(string));
            searchFields.AddExactField(UUTField.Comment, ReportReadState.InHeader, "Message :", null, typeof(string));

            SearchFields.ExactSearchField field;
            field = searchFields.AddExactField("Step", ReportReadState.InTest, "00", null, typeof(string));
            field.delimiters = new char[] { '\t' };
            field.AddSubField("Step_number", typeof(string));
            field.AddSubField("Judgement", typeof(string));
            field.AddSubField("Parts", typeof(string));
            field.AddSubField("Value", typeof(string));
            field.AddSubField("Unit", typeof(string));
            field.AddSubField("RefValue", typeof(string));
            field.AddSubField("RefUnit", typeof(string));
            field.AddSubField("TestValue1", typeof(string));
            field.AddSubField("Unit1", typeof(string));
            field.AddSubField("TestValue2", typeof(string));
            field.AddSubField("Unit2", typeof(string));
            field.AddSubField("Aux", typeof(string));
            field.AddSubField("Comment", typeof(string));
            field.AddSubField("Location", typeof(string));
            field.AddSubField("H-pin", typeof(string));
            if (converterFormat == "B") field.AddSubField("Net_Name_Hpin", typeof(string));
            field.AddSubField("L-pin", typeof(string));
            if (converterFormat == "B") field.AddSubField("Net_Name_Lpin", typeof(string));
            field.AddSubField("Function", typeof(string));
            field.AddSubField("Tolerance+", typeof(string));
            field.AddSubField("Tolerance-", typeof(string));
            field.AddSubField("RefValue", typeof(string));
        }
    }
}
