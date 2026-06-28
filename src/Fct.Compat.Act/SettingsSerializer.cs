using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Windows.Forms;
using System.Xml;

namespace Advanced_Combat_Tracker
{
    // Clean-room reimplementation of ACT's per-plugin settings serializer. Persists
    // registered fields/controls to/from XML. Handles primitives and standard WinForms
    // controls; unknown (ACT-custom) control types are skipped gracefully.
    public class SettingsSerializer : IDisposable
    {
        private readonly object _parent;
        private readonly Dictionary<string, Control> _controls = new Dictionary<string, Control>();
        private readonly List<string> _str = new List<string>();
        private readonly List<string> _int = new List<string>();
        private readonly List<string> _long = new List<string>();
        private readonly List<string> _bool = new List<string>();
        private readonly List<string> _dir = new List<string>();

        public SettingsSerializer(object parentSettingsClass) => _parent = parentSettingsClass;

        public void AddStringSetting(string name) { if (!_str.Contains(name)) _str.Add(name); }
        public void AddIntSetting(string name) { if (!_int.Contains(name)) _int.Add(name); }
        public void AddLongSetting(string name) { if (!_long.Contains(name)) _long.Add(name); }
        public void AddBooleanSetting(string name) { if (!_bool.Contains(name)) _bool.Add(name); }
        public void AddDirectoryInfoSetting(string name) { if (!_dir.Contains(name)) _dir.Add(name); }

        public void AddControlSetting(string controlName, Control control)
        {
            if (!_controls.ContainsKey(controlName)) _controls.Add(controlName, control);
        }

        public bool RemoveControlSetting(string controlName) => _controls.Remove(controlName);

        public void FinializeComboBoxes() { }

        private void SetField(string name, object value)
        {
            var t = _parent.GetType();
            var f = t.GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (f != null) { f.SetValue(_parent, value); return; }
            t.GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.SetValue(_parent, value, null);
        }

        private object GetField(string name)
        {
            var t = _parent.GetType();
            var f = t.GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (f != null) return f.GetValue(_parent);
            return t.GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(_parent, null);
        }

        private static string Attr(XmlNode n, string a) => n.Attributes?[a]?.Value;

        public int ImportFromXml(XmlTextReader reader)
        {
            int errors = 0;
            var doc = new XmlDocument();
            doc.LoadXml(reader.ReadOuterXml());
            if (doc.ChildNodes.Count == 0) return 0;
            foreach (XmlNode node in doc.ChildNodes[0].ChildNodes)
            {
                try
                {
                    string name = Attr(node, "Name");
                    string value = Attr(node, "Value");
                    switch (node.Name)
                    {
                        case "String": SetField(name, value); break;
                        case "Int32": SetField(name, int.Parse(value)); break;
                        case "Int64": SetField(name, long.Parse(value)); break;
                        case "Boolean": SetField(name, bool.Parse(value)); break;
                        case "DirectoryInfo": SetField(name, new DirectoryInfo(value)); break;
                        case "CheckBox": if (_controls.TryGetValue(name, out var cb)) ((CheckBox)cb).Checked = bool.Parse(value); break;
                        case "RadioButton": if (_controls.TryGetValue(name, out var rb)) ((RadioButton)rb).Checked = bool.Parse(value); break;
                        case "TextBox": if (_controls.TryGetValue(name, out var tb)) ((TextBox)tb).Text = value; break;
                        case "ComboBox": if (_controls.TryGetValue(name, out var cbo)) ((ComboBox)cbo).Text = value; break;
                        case "NumericUpDown": if (_controls.TryGetValue(name, out var nud)) ((NumericUpDown)nud).Value = decimal.Parse(value); break;
                        case "TrackBar": if (_controls.TryGetValue(name, out var trk)) ((TrackBar)trk).Value = int.Parse(value); break;
                        default: break; // skip unknown / ACT-custom control nodes
                    }
                }
                catch { errors++; }
            }
            return errors;
        }

        public void ExportToXml(XmlTextWriter w)
        {
            foreach (var kv in _controls)
            {
                try
                {
                    switch (kv.Value)
                    {
                        case CheckBox c: Write(w, "CheckBox", kv.Key, c.Checked.ToString()); break;
                        case RadioButton c: Write(w, "RadioButton", kv.Key, c.Checked.ToString()); break;
                        case TextBox c: Write(w, "TextBox", kv.Key, c.Text); break;
                        case ComboBox c: Write(w, "ComboBox", kv.Key, c.Text); break;
                        case NumericUpDown c: Write(w, "NumericUpDown", kv.Key, c.Value.ToString()); break;
                        case TrackBar c: Write(w, "TrackBar", kv.Key, c.Value.ToString()); break;
                        default: break;
                    }
                }
                catch { }
            }
            foreach (var s in _str) Try(() => Write(w, "String", s, (string)GetField(s)));
            foreach (var s in _int) Try(() => Write(w, "Int32", s, ((int)GetField(s)).ToString()));
            foreach (var s in _long) Try(() => Write(w, "Int64", s, ((long)GetField(s)).ToString()));
            foreach (var s in _bool) Try(() => Write(w, "Boolean", s, ((bool)GetField(s)).ToString()));
            foreach (var s in _dir)
                Try(() => Write(w, "DirectoryInfo", s, ((DirectoryInfo)GetField(s))?.FullName ?? "C:\\"));
        }

        private static void Write(XmlTextWriter w, string element, string name, string value)
        {
            w.WriteStartElement(element);
            w.WriteAttributeString("Name", name);
            w.WriteAttributeString("Value", value ?? "");
            w.WriteEndElement();
        }

        private static void Try(Action a) { try { a(); } catch { } }

        public void Dispose() { }
    }
}
