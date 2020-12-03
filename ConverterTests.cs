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
            SetupAPI(null, "", "Test", true);
            RegisterClient("your wats", "username", "password");
            InitializeAPI(true);
        }

        [TestMethod]
        public void TestFlyingProbeConverter()
        {
            InitializeAPI(true);

            var fileInfo = new FileInfo(@"Examples\ATVG02_43393848.1023824957283_20141227_152724.ATD");
            var arguments = new Dictionary<string, string>
            {
                { "FPTFormat", "A" }
            };

            SetConversionSource(fileInfo, new Dictionary<string, string>(), arguments);

            var converter = new FlyingProbeConverter(arguments);
            using (FileStream file = fileInfo.Open(FileMode.Open))
            {
                converter.ImportReport(this, file);
            }
        }
    }
}
