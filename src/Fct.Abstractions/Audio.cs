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
        void Speak(string text, AudioOptions? options = null);
        void Play(string filePath, int volume = 100);

        /// <summary>Register an output sink. Higher priority runs first. Dispose to unregister.</summary>
        IDisposable RegisterSink(IAudioSink sink, int priority = 0);
    }

    /// <summary>
    /// A registered audio destination. Async + fire-and-return — the host never blocks a producer on
    /// a sink, so an out-of-process bridge (Discord) fits without <c>async void</c>.
    /// </summary>
    public interface IAudioSink
    {
        ValueTask SpeakAsync(string text, AudioOptions options, CancellationToken ct);
        ValueTask PlayAsync(string filePath, int volume, CancellationToken ct);
    }

    /// <summary>TTS/sound options. <see cref="Volume"/> is 0–100.</summary>
    public sealed record AudioOptions(int Volume = 100, string? Voice = null, float Rate = 1f)
    {
        public static readonly AudioOptions Default = new AudioOptions();
    }
}
