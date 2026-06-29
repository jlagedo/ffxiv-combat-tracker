using System;
using System.Globalization;
using Fct.Logging;
using Microsoft.Extensions.Logging;
using Serilog.Core;
using Serilog.Events;

namespace Fct.LegacyHost.Logging
{
    // Serilog sink that forwards each satellite log event to the net10 host over the bridge pipe, so
    // the satellite's records join the host's unified pipeline. Until the bridge is connected, Sender
    // is null and this is a no-op (the file sinks still capture the event locally).
    internal sealed class BridgeLogSink : ILogEventSink
    {
        // Set by Program once the bridge pipe is up. Volatile: the dispatch/UI threads both log.
        public static volatile Action<string> Sender;

        public void Emit(LogEvent logEvent)
        {
            var send = Sender;
            if (send == null) return;

            try
            {
                var record = new BridgeLogRecord
                {
                    Timestamp = logEvent.Timestamp,
                    Level = MapLevel(logEvent.Level),
                    Category = GetSourceContext(logEvent),
                    EventId = GetEventId(logEvent, out var name),
                    EventName = name,
                    Message = logEvent.RenderMessage(CultureInfo.InvariantCulture),
                    Exception = logEvent.Exception?.ToString(),
                };
                send(record.ToWire());
            }
            catch
            {
                // Bridge down or mid-teardown; the local file sinks still hold the record.
            }
        }

        private static LogLevel MapLevel(LogEventLevel level)
        {
            switch (level)
            {
                case LogEventLevel.Verbose: return LogLevel.Trace;
                case LogEventLevel.Debug: return LogLevel.Debug;
                case LogEventLevel.Information: return LogLevel.Information;
                case LogEventLevel.Warning: return LogLevel.Warning;
                case LogEventLevel.Error: return LogLevel.Error;
                case LogEventLevel.Fatal: return LogLevel.Critical;
                default: return LogLevel.Information;
            }
        }

        private static string GetSourceContext(LogEvent e)
        {
            if (e.Properties.TryGetValue("SourceContext", out var v) && v is ScalarValue sv && sv.Value is string s)
                return s;
            return "";
        }

        // Microsoft.Extensions.Logging EventIds arrive (through Serilog.Extensions.Logging) as a
        // structured "EventId" property carrying Id and Name.
        private static int GetEventId(LogEvent e, out string name)
        {
            name = "";
            if (e.Properties.TryGetValue("EventId", out var v) && v is StructureValue sv)
            {
                int id = 0;
                foreach (var prop in sv.Properties)
                {
                    if (prop.Name == "Id" && prop.Value is ScalarValue idv && idv.Value is int i)
                        id = i;
                    else if (prop.Name == "Name" && prop.Value is ScalarValue nv && nv.Value is string s)
                        name = s;
                }
                return id;
            }
            return 0;
        }
    }
}
