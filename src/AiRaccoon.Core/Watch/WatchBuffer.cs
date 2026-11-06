using System.Globalization;

namespace AiRaccoon.Core.Watch;

/// <summary>The file watcher's event buffer size: the <c>watch.buffer-size.global</c> setting, clamped to what the OS documents.</summary>
public static class WatchBuffer
{
    /// <summary>The documented maximum, and the value used when the setting is absent or unreadable.</summary>
    public const int Default = 64 * 1024;

    /// <summary>The smallest size .NET honours; lower values are raised to it.</summary>
    public const int Min = 4096;

    public static int Resolve(string? raw) =>
        int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var size)
            ? Math.Clamp(size, Min, Default)
            : Default;
}
