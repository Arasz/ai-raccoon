namespace AiRaccoon.Core.Memory.Promotion;

/// <summary>Settings key for the opt-in ONNX instruct promotion classifier (CLI-only config channel).</summary>
public static class PromotionModelSettings
{
    public const string EnabledKey = "promotion.model.enabled";

    public static bool ParseEnabled(string? value) => value == "true";
}
