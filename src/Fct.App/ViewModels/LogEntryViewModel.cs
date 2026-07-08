using System.Globalization;
using System.Text;
using Fct.App.Logging;

namespace Fct.App.ViewModels;

// One console row. Immutable — log lines never change once emitted, so (unlike the encounter meter)
// there is no in-place update path. Carries both the display fields and a flat CopyText used when the
// user copies selected rows to the clipboard.
public sealed class LogEntryViewModel
{
    public LogEntryViewModel(LogEntry entry)
    {
        Level = entry.Level;
        Time = entry.Timestamp.ToLocalTime().ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture);
        LevelCode = Code(entry.Level);
        Source = entry.Source;
        Message = entry.Message;
        Exception = entry.Exception;

        var sb = new StringBuilder()
            .Append(Time).Append(' ').Append(LevelCode).Append(' ');
        if (!string.IsNullOrEmpty(Source)) sb.Append(Source).Append("  ");
        sb.Append(Message);
        if (!string.IsNullOrEmpty(Exception)) sb.Append('\n').Append(Exception);
        CopyText = sb.ToString();
    }

    public ConsoleLevel Level { get; }
    public string Time { get; }
    public string LevelCode { get; }
    public string Source { get; }
    public string Message { get; }
    public string? Exception { get; }
    public bool HasException => !string.IsNullOrEmpty(Exception);
    public bool HasSource => !string.IsNullOrEmpty(Source);
    public string CopyText { get; }

    // Three-letter codes matching Serilog's {Level:u3} in the console/file templates.
    private static string Code(ConsoleLevel level) => level switch
    {
        ConsoleLevel.Verbose => "VRB",
        ConsoleLevel.Debug => "DBG",
        ConsoleLevel.Information => "INF",
        ConsoleLevel.Warning => "WRN",
        ConsoleLevel.Error => "ERR",
        ConsoleLevel.Fatal => "FTL",
        _ => "INF",
    };
}
