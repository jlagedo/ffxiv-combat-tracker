using System;
using Fct.Abstractions;

namespace Fct.App.Hosting;

/// <summary>
/// The wall-clock <see cref="IClock"/>. <see cref="ServerNow"/> tracks UTC until the parser supplies a
/// real FFXIV server offset (piece C — <c>GetServerTimestamp</c>).
/// </summary>
internal sealed class SystemClock : IClock
{
    public DateTimeOffset LocalNow => DateTimeOffset.Now;
    public DateTimeOffset ServerNow => DateTimeOffset.UtcNow;
}
