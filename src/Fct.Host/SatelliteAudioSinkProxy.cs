using System;
using System.Threading;
using System.Threading.Tasks;
using Fct.Abstractions;
using Fct.Bridge;

namespace Fct.Host;

// A terminal <see cref="IAudioSink"/> the host registers on its one shared <see cref="IAudioOutput"/>
// on behalf of an out-of-process satellite whose plugin took over the ACT PlayTts/PlaySound slot (P6).
// When a producer in ANY satellite speaks, the host fans it here and this proxy marshals the call DOWN
// the owning satellite's command pipe; the satellite invokes the plugin's local delegate. One proxy per
// satellite carries both capabilities so the terminal registration never mis-orders (a Speak is relayed
// only when the satellite registered a TTS slot; a Play only when it registered a sound slot).
// Fire-and-return (returns default) so a producer in another satellite never blocks on it.
internal sealed class SatelliteAudioSinkProxy : IAudioSink
{
    private readonly bool _tts;
    private readonly bool _sound;
    private readonly Func<string, bool> _sendDown;

    public SatelliteAudioSinkProxy(bool tts, bool sound, Func<string, bool> sendDown)
    {
        _tts = tts;
        _sound = sound;
        _sendDown = sendDown;
    }

    public ValueTask SpeakAsync(string text, AudioOptions options, CancellationToken ct)
    {
        if (_tts)
            _sendDown(SatelliteProtocol.FormatSpeak(text ?? "", options.Volume, (int)options.Channel, options.Synchronous));
        return default;
    }

    public ValueTask PlayAsync(string filePath, int volume, CancellationToken ct)
    {
        if (_sound)
            _sendDown(SatelliteProtocol.FormatPlaySound(filePath ?? "", volume));
        return default;
    }
}
