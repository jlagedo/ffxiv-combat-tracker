using System.IO;
using System.Text;
using System.Windows.Forms;
using System.Xml;
using Advanced_Combat_Tracker;
using Xunit;

namespace Fct.Compat.Act.Tests
{
    // SettingsSerializer persists a plugin's registered fields/controls to/from XML. The
    // plugins call this during InitPlugin/DeInitPlugin, so a broken round-trip silently
    // loses user settings.
    public class SettingsSerializerTests
    {
        private sealed class Sample
        {
            public string S;
            public int I;
            public long L;
            public bool B;
            public DirectoryInfo Dir;
        }

        private static string Export(SettingsSerializer ser)
        {
            var sb = new StringBuilder();
            using (var sw = new StringWriter(sb))
            using (var xw = new XmlTextWriter(sw))
            {
                xw.WriteStartElement("Config");
                ser.ExportToXml(xw);
                xw.WriteEndElement();
            }
            return sb.ToString();
        }

        private static int Import(SettingsSerializer ser, string xml)
        {
            using (var sr = new StringReader(xml))
            using (var xr = new XmlTextReader(sr))
            {
                xr.MoveToContent(); // ACT calls ImportFromXml positioned on the element
                return ser.ImportFromXml(xr);
            }
        }

        [Fact]
        public void Primitive_fields_round_trip()
        {
            var src = new Sample { S = "hello", I = 42, L = 9000000000L, B = true, Dir = new DirectoryInfo(@"C:\Temp\Fct") };
            var ser = new SettingsSerializer(src);
            ser.AddStringSetting("S");
            ser.AddIntSetting("I");
            ser.AddLongSetting("L");
            ser.AddBooleanSetting("B");
            ser.AddDirectoryInfoSetting("Dir");
            var xml = Export(ser);

            var dst = new Sample();
            var ser2 = new SettingsSerializer(dst);
            ser2.AddStringSetting("S");
            ser2.AddIntSetting("I");
            ser2.AddLongSetting("L");
            ser2.AddBooleanSetting("B");
            ser2.AddDirectoryInfoSetting("Dir");
            int errors = Import(ser2, xml);

            Assert.Equal(0, errors);
            Assert.Equal("hello", dst.S);
            Assert.Equal(42, dst.I);
            Assert.Equal(9000000000L, dst.L);
            Assert.True(dst.B);
            Assert.Equal(new DirectoryInfo(@"C:\Temp\Fct").FullName, dst.Dir.FullName);
        }

        [Fact]
        public void Control_values_round_trip()
        {
            var srcCheck = new CheckBox { Checked = true };
            var srcText = new TextBox { Text = "abc" };
            var ser = new SettingsSerializer(new object());
            ser.AddControlSetting("Check", srcCheck);
            ser.AddControlSetting("Text", srcText);
            var xml = Export(ser);

            var dstCheck = new CheckBox { Checked = false };
            var dstText = new TextBox { Text = "" };
            var ser2 = new SettingsSerializer(new object());
            ser2.AddControlSetting("Check", dstCheck);
            ser2.AddControlSetting("Text", dstText);
            Import(ser2, xml);

            Assert.True(dstCheck.Checked);
            Assert.Equal("abc", dstText.Text);
        }

        [Fact]
        public void Unknown_control_type_is_skipped_without_throwing()
        {
            var ser = new SettingsSerializer(new object());
            ser.AddControlSetting("Weird", new Panel()); // not a handled control type
            var xml = Export(ser);
            Assert.DoesNotContain("Weird", xml); // silently omitted, no exception
        }

        [Fact]
        public void RemoveControlSetting_drops_the_control()
        {
            var ser = new SettingsSerializer(new object());
            ser.AddControlSetting("Check", new CheckBox { Checked = true });
            Assert.True(ser.RemoveControlSetting("Check"));
            Assert.DoesNotContain("Check", Export(ser));
        }

        [Fact]
        public void Import_counts_malformed_values_as_errors()
        {
            var dst = new Sample();
            var ser = new SettingsSerializer(dst);
            ser.AddIntSetting("I");
            int errors = Import(ser, "<Config><Int32 Name=\"I\" Value=\"not-a-number\" /></Config>");
            Assert.Equal(1, errors);
        }
    }
}
