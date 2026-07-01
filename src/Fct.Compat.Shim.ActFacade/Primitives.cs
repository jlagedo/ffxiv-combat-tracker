using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Windows.Forms;

namespace Advanced_Combat_Tracker
{
    // The plugin host contract. Real plugins implement this; the shim's LegacyPluginHost drives it.
    public interface IActPluginV1
    {
        void InitPlugin(TabPage pluginScreenSpace, Label pluginStatusText);
        void DeInitPlugin();
    }

    // Implemented by a pluginObj that stands in for another IActPluginV1 (a wrapper that re-exposes a
    // real plugin's SDK surface). PluginGetSelfData resolves through it so the wrapped plugin still
    // finds its own ActPluginData when it looks itself up by `this`.
    public interface IActPluginAlias
    {
        IActPluginV1 Inner { get; }
    }

    // Per-plugin record ACT keeps. OverlayPlugin reflects over cbEnabled/lblPluginTitle/pluginObj for
    // discovery; FFXIV_ACT_Plugin reads pluginFile.DirectoryName.
    public class ActPluginData
    {
        public TabPage tpPluginSpace;
        public Label lblPluginStatus;
        public Label lblPluginTitle;
        public CheckBox cbEnabled;
        public Panel pPluginInfo;
        public System.IO.FileInfo pluginFile;
        public IActPluginV1 pluginObj;
        public string pluginVersion;
    }

    // --- Log-line event surface (events plugins subscribe to) ---

    public delegate void LogLineEventDelegate(bool isImport, LogLineEventArgs logInfo);
    public delegate void LogFileChangedDelegate(bool IsImport, string newLogFileName);

    // --- Cross-cutting FormActMain event surface (ACT raises these from UI/clipboard/XML-share/URL
    // paths the shim does not have; declared as no-op publishers so plugin `+=` binds). ---

    public delegate void ActLifecycleEventDelegate(ActLifecycleEventArgs args);
    public delegate void LogFileRenamedDelegate(LogFileRenamedEventArgs LogFileRenamedArgs);
    public delegate void XmlSnippetAddedDelegate(object sender, XmlSnippetEventArgs e);
    public delegate void ClipboardEventDelegate(ClipboardEventArgs ClipArgs);
    public delegate void UrlRequestEventDelegate(UrlRequestEventArgs urlInfo);

    public class ActLifecycleEventArgs : EventArgs
    {
        public enum ActLifecycleEnum
        {
            Initial,
            PluginsDone,
            ConfigDone,
            InitActDone,
            FormActMainShown,
            FormActMainClosing
        }

        public ActLifecycleEnum CurrentState;
    }

    public class LogFileRenamedEventArgs : EventArgs
    {
        public string From { get; set; } = string.Empty;
        public string To { get; set; } = string.Empty;

        public LogFileRenamedEventArgs(string From, string To)
        {
            this.From = From;
            this.To = To;
        }
    }

    public class XmlSnippetEventArgs : EventArgs
    {
        public readonly string ShareType;
        public readonly Dictionary<string, string> XmlAttributes;
        public readonly string RawXml;
        private bool handled;

        public bool Handled
        {
            get => handled;
            set { if (value) handled = value; }
        }

        public XmlSnippetEventArgs(string ShareType, Dictionary<string, string> XmlAttributes, string RawXml)
        {
            this.ShareType = ShareType;
            this.XmlAttributes = XmlAttributes;
            this.RawXml = RawXml;
        }
    }

    public class ClipboardEventArgs : EventArgs
    {
        public bool CopyLocal { get; set; }
        public string ClipContent { get; set; }
        public string CallerName => new StackTrace().GetFrame(3).GetMethod().Name;

        public ClipboardEventArgs(string ClipContent, bool CopyLocal)
        {
            this.ClipContent = ClipContent;
            this.CopyLocal = CopyLocal;
        }
    }

    public class UrlRequestEventArgs : EventArgs
    {
        public readonly string url;
        public readonly Dictionary<string, string> headers;
        public readonly Dictionary<string, string> urlVars;

        public string ReturnContentType { get; private set; }
        public string ReturnText { get; private set; }
        public byte[] ReturnBinary { get; private set; }
        public bool UrlHandled { get; private set; }
        public bool ReturnIsText { get; private set; }

        public void SetTextData(string Data, string ReturnContentType)
        {
            ReturnText = Data;
            this.ReturnContentType = ReturnContentType;
            UrlHandled = true;
            ReturnIsText = true;
        }

        public void SetBinaryData(byte[] Data, string ReturnContentType)
        {
            ReturnBinary = Data;
            this.ReturnContentType = ReturnContentType;
            UrlHandled = true;
            ReturnIsText = false;
        }

        public UrlRequestEventArgs(string Url, Dictionary<string, string> Headers, Dictionary<string, string> UrlVars)
        {
            url = Url;
            headers = Headers;
            urlVars = UrlVars;
        }
    }

    public class LogLineEventArgs : EventArgs
    {
        public string logLine;
        public int detectedType;
        public readonly DateTime detectedTime;
        public readonly string detectedZone;
        public readonly bool inCombat;
        public readonly string originalLogLine;
        public readonly string companionLogName;

        public LogLineEventArgs(string logLine, int detectedType, DateTime detectedTime,
            string detectedZone, bool inCombat)
        {
            this.logLine = logLine;
            this.detectedType = detectedType;
            this.detectedTime = detectedTime;
            this.detectedZone = detectedZone;
            this.inCombat = inCombat;
            originalLogLine = logLine;
            companionLogName = string.Empty;
        }

        public LogLineEventArgs(string logLine, int detectedType, DateTime detectedTime,
            string detectedZone, bool inCombat, string companionLogName)
        {
            this.logLine = logLine;
            this.detectedType = detectedType;
            this.detectedTime = detectedTime;
            this.detectedZone = detectedZone;
            this.inCombat = inCombat;
            originalLogLine = logLine;
            this.companionLogName = companionLogName;
        }
    }
}
