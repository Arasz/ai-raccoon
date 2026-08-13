using System.Threading;
using System.Threading.Tasks;
using AiRaccoon.Core.Memory;
using AiRaccoon.Core.Memory.Promotion;

namespace AiRaccoon.Setup.Cli.Commands;

/// <summary>One-shot promotion verb handlers: the opt-in ONNX instruct promotion classifier toggle (CLI-only channel).</summary>
public sealed class PromotionCommands
{
    public async Task<int> SetEnabledAsync(bool enabled, IMemoryStore store, StandardStreams streams,
        CancellationToken cancellationToken)
    {
        await store.SetSettingAsync(PromotionModelSettings.EnabledKey, enabled ? "true" : "false", cancellationToken);
        await streams.WriteOutputLineAsync($"promotion model {(enabled ? "enabled" : "disabled")}");
        return 0;
    }

    public async Task<int> ShowAsync(IMemoryStore store, StandardStreams streams,
        CancellationToken cancellationToken)
    {
        var enabled = PromotionModelSettings.ParseEnabled(
            await store.GetSettingAsync(PromotionModelSettings.EnabledKey, cancellationToken));
        await streams.WriteOutputLineAsync($"promotion model: {(enabled ? "enabled" : "disabled")}");
        return 0;
    }
}
