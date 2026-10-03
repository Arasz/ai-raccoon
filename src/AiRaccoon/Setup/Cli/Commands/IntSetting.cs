using System.CommandLine;
using System.Globalization;
using AiRaccoon.Core.Memory;

namespace AiRaccoon.Setup.Cli.Commands;

/// <summary>
///     A global setting holding a positive integer (optionally capped at <paramref name="Max" />).
///     <see cref="SetAsync" /> parses the CLI argument, rejects bad values with a usage error, saves,
///     and prints <paramref name="Confirmation" />.
/// </summary>
public sealed record IntSetting(string Key, string Argument, string Noun, string Unit, Func<int, string> Confirmation,
    int? Max = null)
{
    public async Task<int> SetAsync(ParseResult parseResult, IMemoryStore store, StandardStreams streams,
        CancellationToken cancellationToken)
    {
        if (!int.TryParse(parseResult.GetValue<string>(Argument), CultureInfo.InvariantCulture, out var parsed) || parsed <= 0)
        {
            await streams.WriteErrorLineAsync($"ai-raccoon: {Noun} must be a positive number of {Unit}");
            return ErrorCode.Usage.InvalidValue;
        }

        if (parsed > Max)
        {
            await streams.WriteErrorLineAsync($"ai-raccoon: {Noun} must be at most {Max} {Unit}");
            return ErrorCode.Usage.InvalidValue;
        }

        await store.SetSettingAsync(Key, parsed.ToString(CultureInfo.InvariantCulture), cancellationToken);
        await streams.WriteOutputLineAsync(Confirmation(parsed));
        return 0;
    }
}
