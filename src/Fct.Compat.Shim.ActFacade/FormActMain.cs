using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Fct.Abstractions;
using Microsoft.Extensions.Logging;

namespace Advanced_Combat_Tracker
{
    /// <summary>
    /// The POCO re-projection of ACT's <c>FormActMain</c> host object. Unlike the net48 facade this is
    /// NOT a <see cref="Form"/>; it forwards the ACT host surface onto the modern <see cref="IPluginHost"/>.
    /// One instance is shared across all shimmed plugins (see <c>ActGlobals.oFormActMain</c>). The
    /// surface grows slice-by-slice: this seed carries lifecycle/identity, logging, and window chrome;
    /// audio, the raw-line event surface, and the encounter/aggregation reader arrive in later slices.
    /// </summary>
    public sealed class FormActMain : IDisposable
    {
        private readonly IDisposable _busSubscription;

        public FormActMain(IPluginHost host)
        {
            Host = host ?? throw new ArgumentNullException(nameof(host));

            // Re-fire the modern raw-line firehose as ACT's Before/OnLogLineRead (the Trig/cactbot
            // regex lifeline). Typed events (ZoneChanged/PartyList/…) map onto the SDK's
            // IDataSubscription adapter, not here — matching real ACT's split.
            _busSubscription = Host.Game.Events.Subscribe(GameEventFilter.All, OnGameEvent);
        }

        /// <summary>The modern host this hub forwards to. Migrating plugins can use it directly.</summary>
        public IPluginHost Host { get; }

        // --- Discovery / per-plugin records ------------------------------------------------

        /// <summary>Every plugin ACT has loaded. OverlayPlugin reflects over this for peer discovery.</summary>
        public List<ActPluginData> ActPlugins { get; } = new List<ActPluginData>();

        /// <summary>Resolve a plugin's own <see cref="ActPluginData"/> (matching by object identity, or
        /// through an <see cref="IActPluginAlias"/> wrapper), as ACT does when a plugin looks itself up.</summary>
        public ActPluginData PluginGetSelfData(IActPluginV1 plugin)
        {
            foreach (var p in ActPlugins)
            {
                if (ReferenceEquals(p.pluginObj, plugin)) return p;
                if (p.pluginObj is IActPluginAlias alias && ReferenceEquals(alias.Inner, plugin)) return p;
            }
            return null;
        }

        // --- Lifecycle / identity ----------------------------------------------------------

        /// <summary>Impersonates ACT's version so plugin version gates (e.g. OverlayPlugin's) pass.</summary>
        public Version GetVersion() => new Version(3, 8, 5, 288);

        /// <summary>ACT's config folder (a single global folder, as in real ACT — a plugin's own folder
        /// comes from <see cref="PluginGetSelfData"/>.<c>pluginFile.DirectoryName</c>).</summary>
        public DirectoryInfo AppDataFolder =>
            new DirectoryInfo(Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "Advanced Combat Tracker"));

        public bool InitActDone { get; set; } = true;
        public bool IsActClosing { get; set; }

        // --- Logging (ACT exposes these; forward to the host logger) -----------------------

        public void WriteExceptionLog(Exception ex, string message)
            => Host.Logger.LogError(ex, "{Message}", message);

        public void WriteInfoLog(string message)
            => Host.Logger.LogInformation("{Message}", message);

        public void WriteDebugLog(string message)
            => Host.Logger.LogDebug("{Message}", message);

        // --- Window chrome (Triggernometry corner controls / notifications) ----------------

        /// <summary>DPI scale factor. The shell owns real DPI; the shim reports 1.0.</summary>
        public float DpiScale => 1f;

        private readonly List<Control> _cornerControls = new List<Control>();

        public void CornerControlAdd(Control control)
        {
            if (control != null) _cornerControls.Add(control);
        }

        public void CornerControlRemove(Control control)
        {
            if (control != null) _cornerControls.Remove(control);
        }

        /// <summary>Corner controls a plugin has added (exposed for the UI slice / inspection).</summary>
        public IReadOnlyList<Control> CornerControls => _cornerControls;

        public void NotificationAdd(string title, string text)
            => Host.Logger.LogInformation("[Notification] {Title}: {Text}", title, text);

        // --- Audio (producers + the PlayTts/PlaySound hijack slots) ------------------------

        // ACT's delegate slot types. A plugin (Discord-Triggers / TTSYukkuri) replaces the slot to
        // route audio to itself instead of ACT's built-in speakers.
        public delegate void PlayTtsDelegate(string text);
        public delegate void PlaySoundDelegate(string soundFile, int volume);

        /// <summary>Producer entry point: speak text through the host's audio fan-out.</summary>
        public void TTS(string text) => Host.Audio.Speak(text);

        /// <summary>Producer entry point: play a sound file through the host's audio fan-out.</summary>
        public void PlaySound(string soundFile, int volume = 100) => Host.Audio.Play(soundFile, volume);

        /// <summary>Setting the TTS slot registers a <b>terminal</b> audio sink (G3 route-instead-of):
        /// the delegate takes over playback and suppresses lower-priority sinks, reproducing ACT's
        /// save-and-replace behavior over the modern multi-sink model.</summary>
        public PlayTtsDelegate PlayTtsMethod
        {
            set { if (value != null) Host.Audio.RegisterSink(new DelegateAudioSink(value, null), priority: 10, terminal: true); }
        }

        /// <summary>Setting the sound slot registers a terminal audio sink (see <see cref="PlayTtsMethod"/>).</summary>
        public PlaySoundDelegate PlaySoundMethod
        {
            set { if (value != null) Host.Audio.RegisterSink(new DelegateAudioSink(null, value), priority: 10, terminal: true); }
        }

        // --- Raw log-line surface (Trig/cactbot regex lifeline) ----------------------------

        /// <summary>Raised before each raw log line is processed (ACT's import-aware pre-hook).</summary>
        public event LogLineEventDelegate BeforeLogLineRead;

        /// <summary>Raised for each raw log line. Trig/cactbot regex over <c>logInfo.logLine</c>.</summary>
        public event LogLineEventDelegate OnLogLineRead;

        private void OnGameEvent(GameEvent e)
        {
            if (e is not RawLogLine raw) return;

            var args = new LogLineEventArgs(
                raw.Line, (int)raw.Type, raw.Timestamp.LocalDateTime,
                Host.Game.Snapshot().Zone.Name, Host.Encounters.InCombat);

            BeforeLogLineRead?.Invoke(false, args);
            OnLogLineRead?.Invoke(false, args);
        }

        // --- Named callbacks (Triggernometry peer interop) ---------------------------------

        /// <summary>Register a named callback other plugins can invoke (owner-tagged; duplicate names
        /// rejected unless <paramref name="allowDuplicate"/>). The returned handle unregisters it.</summary>
        public IDisposable RegisterNamedCallback(string name, Action<object> callback, object owner = null, bool allowDuplicate = false)
            => Host.Plugins.RegisterCallback(name, callback, owner, allowDuplicate);

        /// <summary>Invoke every callback registered under <paramref name="name"/>.</summary>
        public void InvokeNamedCallback(string name, object argument = null)
            => Host.Plugins.InvokeCallback(name, argument);

        public void Dispose() => _busSubscription.Dispose();

        // Adapts the legacy TTS/sound delegates to the modern IAudioSink (async/fire-and-return).
        private sealed class DelegateAudioSink : IAudioSink
        {
            private readonly PlayTtsDelegate _tts;
            private readonly PlaySoundDelegate _sound;

            public DelegateAudioSink(PlayTtsDelegate tts, PlaySoundDelegate sound)
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
}
