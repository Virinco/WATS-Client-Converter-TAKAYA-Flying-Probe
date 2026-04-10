using System;
using System.Collections.Generic;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System.Linq;
using System.Text;
using Virinco.WATS.Interface;
using System.IO;
using Virinco.WATS.Schemas.WSJF;

namespace TAKAYA_FlyingProbeConverter
{
    [TestClass]
    public class ConverterTests : TDM
    {
        [TestMethod]
        public void TestATDConverter()
        {
            InitializeAPI(false);
            var directory = new DirectoryInfo(@"Examples\ATD");
            var arguments = new ATD_Converter().ConverterParameters;
            arguments.Add("UnitCalcPreference", "Limits");
            foreach (var fileInfo in directory.GetFiles("*.ATD", SearchOption.TopDirectoryOnly))
            {
                SetConversionSource(fileInfo, new Dictionary<string, string>(), arguments);
                var converter = new ATD_Converter(arguments);
                using (FileStream file = fileInfo.Open(FileMode.Open))
                {
                    converter.ImportReport(this, file);
                }
            }
        }

        [TestMethod]
        public void TestATDXConverter()
        {
            InitializeAPI(true);
            var directory = new DirectoryInfo(@"Examples\ATDX");
            var arguments = new ATDX_Converter().ConverterParameters;
            arguments.Add("UnitCalcPreference", "Limits");
            foreach (var fileInfo in directory.GetFiles("*.atdx", SearchOption.TopDirectoryOnly))
            {
                SetConversionSource(fileInfo, new Dictionary<string, string>(), arguments);
                var converter = new ATDX_Converter(arguments);
                using (FileStream file = fileInfo.Open(FileMode.Open))
                {
                    converter.ImportReport(this, file);
                }
            }
        }

        [TestMethod]
        public void TestAPT94xx_ATD_Converter()
        {
            InitializeAPI(true);
            var directory = new DirectoryInfo(@"Examples\APT94xx_ATD");
            var arguments = new APT94xx_ATD_Converter().ConverterParameters;
            foreach (var fileInfo in directory.GetFiles("*.ATD", SearchOption.TopDirectoryOnly))
            {
                SetConversionSource(fileInfo, new Dictionary<string, string>(), arguments);
                var converter = new APT94xx_ATD_Converter(arguments);
                using (FileStream file = fileInfo.Open(FileMode.Open))
                {
                    converter.ImportReport(this, file);
                }
            }
        }
    }
}
