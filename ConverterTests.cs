using System;
using System.Collections.Generic;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System.Linq;
using System.Text;
using Virinco.WATS.Interface;
using System.IO;

namespace TAKAYA_FlyingProbeConverter
{
    [TestClass]
    public class ConverterTests : TDM
    {
        [TestMethod]
        public void SetupClient()
        {
            //SetupAPI(null, "", "Test", true);
            RegisterClient("Your server", "your user", "your password");
            InitializeAPI(true);
        }

        [TestMethod]
        public void TestFlyingProbeConverter()
        {
            InitializeAPI(true);
            var fileInfo = new FileInfo(@"Examples\V682793_H_TOP-20230821081637.ATD");
            var arguments = new FlyingProbeConverter().ConverterParameters;
            SetConversionSource(fileInfo, new Dictionary<string, string>(), arguments);
            //arguments.Add("UnitCalcPreference", "Measure"); //Calculate from Measure or Limits unit
            arguments.Add("UnitCalcPreference", "Limits"); 
            var converter = new FlyingProbeConverter(arguments);
            using (FileStream file = fileInfo.Open(FileMode.Open))
            {
                converter.ImportReport(this, file);
            }
            SubmitPendingReports();
        }
    }
}
