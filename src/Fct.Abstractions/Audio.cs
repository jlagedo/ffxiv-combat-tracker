using System;
using System.Threading;
using System.Threading.Tasks;

namespace Fct.Abstractions
{
    /// <summary>
    /// Audio output. Replaces the single mutable <c>PlayTtsMethod</c>/<c>PlaySoundMethod</c> delegate
    /// slot — which forced Discord-Triggers and TTSYukkuri into last-writer-wins — with a registry of
    /// composable, async sinks. Producers (Triggernometry/cactbot/Hojoring) call
    /// <see cref="Speak"/>/<see cref="Play"/>; sink providers (Discord-Triggers/TTSYukkuri) register.
    /// </summary>
    public interface IAudioOutput
    {
        /// <summary>Speak <paramref name="text"/> through every registered sink (replaces ACT's <c>PlayTtsMethod</c>).</summary>
        void Speak(string text, AudioOptions? options = null);

        /// <summary>Play the sound file at <paramref name="filePath"/> through every registered sink (replaces ACT's <c>PlaySoundMethod</c>).</summary>
        void Play(string filePath, int volume = 100);

        /// <summary>
        /// Register an output sink. Higher priority runs first. Dispose to unregister. A
        /// <paramref name="terminal"/> sink stops the chain once it handles a call, so lower-priority
        /// sinks never fire — the additive form of Discord-Triggers/TTSYukkuri routing audio
        /// <em>instead of</em> ACT's built-in speakers (they save and replace the delegate slot today).
        /// </summary>
        IDisposable RegisterSink(IAudioSink sink, int priority = 0, bool terminal = false);
    }

    /// <summary>
    /// A registered audio destination. Async + fire-and-return — the host never blocks a producer on
    /// a sink, so an out-of-process bridge (Discord) fits without <c>async void</c>.
    /// </summary>
    public interface IAudioSink
    {
        /// <summary>Render <paramref name="text"/> as speech. Return without blocking the producer.</summary>
        ValueTask SpeakAsync(string text, AudioOptions options, CancellationToken ct);

        /// <summary>Play the sound file at <paramref name="filePath"/>. Return without blocking the producer.</summary>
        ValueTask PlayAsync(string filePath, int volume, CancellationToken ct);
    }

    /// <summary>Playback channel for spatialized audio (TTSYukkuri's L/R/Both routing).</summary>
    public enum AudioChannel { Both = 0, Left = 1, Right = 2 }

    /// <summary>TTS/sound options. <see cref="Volume"/> is 0–100.</summary>
    public sealed record AudioOptions(int Volume = 100, string? Voice = null, float Rate = 1f)
    {
        /// <summary>Shared default options (volume 100, no voice override, rate 1, both channels, async).</summary>
        public static readonly AudioOptions Default = new AudioOptions();

        /// <summary>Output channel (TTSYukkuri routes to L/R/Both). Defaults to <see cref="AudioChannel.Both"/>.</summary>
        public AudioChannel Channel { get; init; } = AudioChannel.Both;

        /// <summary>When true, the producer wants playback to complete before returning (TTSYukkuri sync flag).</summary>
        public bool Synchronous { get; init; }
    }
}
