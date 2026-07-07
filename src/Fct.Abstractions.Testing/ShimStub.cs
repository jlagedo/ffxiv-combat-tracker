using System;
using System.Threading;
using System.Threading.Tasks;

namespace Fct.Abstractions.Testing
{
    /// <summary>
    /// The seam a recompiled legacy plugin calls: an <c>ActGlobals.oFormActMain</c>-shaped surface
    /// exposing the exact ACT entry points the five plugins use, each forwarding to/from the modern
    /// host fakes. This is the shim's seed — it carries only the members the B1→B5 flow tests exercise.
    /// </summary>
    public sealed class ShimStub : IDisposable
    {
        private readonly IPluginHost _host;
        private readonly IDisposable _logSubscription;
        private IDisposable? _packetSubscription;

        public ShimStub(IPluginHost host)
        {
            _host = host ?? throw new ArgumentNullException(nameof(host));
            // Re-fire the modern typed/raw bus as the legacy Before/OnLogLineRead + IDataSubscription surface.
            _logSubscription = _host.Game.Events.Subscribe(GameEventFilter.All, e =>
            {
                switch (e)
                {
                    case RawLogLine raw:
                        var args = new LogLineEventArgs(raw.Line, (int)raw.Type);
                        BeforeLogLineRead?.Invoke(false, args);
                        OnLogLineRead?.Invoke(false, args);
                        break;
                    case Fct.Abstractions.ZoneChanged z:
                        ZoneChanged?.Invoke(z.ZoneId, z.ZoneName);
                        break;
                    case Fct.Abstractions.PartyChanged p:
                        // PartySize is the SDK's real second argument (distinct from Members.Count in
                        // alliance content, G7/P3.5) — forward it verbatim.
                        PartyListChanged?.Invoke(p.Members, p.PartySize);
                        break;
                }
            });
        }

        // --- Legacy delegate types (mirror ACT's PlayTtsMethod/PlaySoundMethod shapes) ---
        public delegate void TextToSpeechDelegate(string text);
        public delegate void PlaySoundDelegate(string soundFile, int volume);
        public delegate void LogLineEventDelegate(bool isImport, LogLineEventArgs args);

        // --- Audio producer side (legacy TTS()/PlaySound() a plugin calls) ---
        public void TTS(string text) => _host.Audio.Speak(text);
        public void PlaySound(string soundFile, int volume = 100) => _host.Audio.Play(soundFile, volume);

        // --- Audio sink hijack slots (Discord-Triggers / TTSYukkuri route-instead-of pattern) ---
        // Setting a slot registers a terminal IAudioSink over the modern host, reproducing the legacy
        // save-and-replace behavior: audio routes to this sink *instead of* ACT's built-in speakers.
        public TextToSpeechDelegate? PlayTtsMethod
        {
            set { if (value != null) _host.Audio.RegisterSink(new DelegateAudioSink(value, null), priority: 10, terminal: true); }
        }

        public PlaySoundDelegate? PlaySoundMethod
        {
            set { if (value != null) _host.Audio.RegisterSink(new DelegateAudioSink(null, value), priority: 10, terminal: true); }
        }

        // --- Raw log line surface (Trig/cactbot regex lifeline) ---
        public event LogLineEventDelegate? BeforeLogLineRead;
        public event LogLineEventDelegate? OnLogLineRead;

        // --- IDataSubscription typed surface (OverlayPlugin consumer, mapped from GameEvent records) ---
        public delegate void ZoneChangedDelegate(uint zoneId, string zoneName);
        public delegate void PartyListChangedDelegate(System.Collections.Generic.IReadOnlyList<uint> partyList, int partySize);
        public event ZoneChangedDelegate? ZoneChanged;
        public event PartyListChangedDelegate? PartyListChanged;

        // --- Encounter / combat-state driver (Triggernometry) ---
        public bool InCombat => _host.Encounters.InCombat;
        public void SetEncounter(string? title = null, string? zone = null) => _host.Encounters.StartCombat(title, zone);
        public void EndCombat(bool export = false) => _host.Encounters.EndCombat(export);
        public EncounterSnapshot? ActiveEncounter => _host.Encounters.Active;
        public EncounterSnapshot? LastEncounter => _host.Encounters.Last;
        public void ActEncounterLogAppend(string line) => _host.Encounters.AppendLogLine(line);

        // --- Named callbacks (Triggernometry peer interop) ---
        public IDisposable RegisterNamedCallback(string name, Action<object?> callback, object? owner = null, bool allowDuplicate = false)
            => _host.Plugins.RegisterCallback(name, callback, owner, allowDuplicate);

        public void InvokeNamedCallback(string name, object? argument = null)
            => _host.Plugins.InvokeCallback(name, argument);

        // --- Raw packet surface (OverlayPlugin's RegisterNetworkParser read path) ---
        // Binds the inbound firehose exactly as FFXIVRepository.RegisterNetworkParser does
        // (sub.NetworkReceived += handler); the handler gets the (connection, epoch, bytes) triple.
        public void RegisterNetworkParser(Action<string, long, byte[]> handler)
        {
            if (handler is null) throw new ArgumentNullException(nameof(handler));
            _packetSubscription = _host.RawPackets.Subscribe(p =>
            {
                if (p.Direction == PacketDirection.Received)
                    handler(p.Connection, p.Epoch, p.Bytes);
            });
        }

        public void Dispose()
        {
            _logSubscription.Dispose();
            _packetSubscription?.Dispose();
        }

        private sealed class DelegateAudioSink : IAudioSink
        {
            private readonly TextToSpeechDelegate? _tts;
            private readonly PlaySoundDelegate? _sound;

            public DelegateAudioSink(TextToSpeechDelegate? tts, PlaySoundDelegate? sound)
            {
                _tts = tts;
                _sound = sound;
            }

            public ValueTask SpeakAsync(string text, AudioOptions options, CancellationToken ct)
            {
                _tts?.Invoke(text);
                return default;
            }

            public ValueTask PlayAsync(string filePath, int volume, CancellationToken ct)
            {
                _sound?.Invoke(filePath, volume);
                return default;
            }
        }
    }

    /// <summary>Minimal stand-in for ACT's <c>LogLineEventArgs</c> (the raw line + detected type).</summary>
    public sealed class LogLineEventArgs : EventArgs
    {
        public LogLineEventArgs(string logLine, int detectedType)
        {
            logLine ??= string.Empty;
            this.logLine = logLine;
            detectedType = detectedType < 0 ? 0 : detectedType;
            this.detectedType = detectedType;
        }

        // Legacy field names (lowercase) — plugins read args.logLine directly.
        public string logLine { get; }
        public int detectedType { get; }
    }
}
