using CommunityToolkit.Diagnostics;

namespace AiRaccoon.Core.Decisions;

/// <summary>Bounds untrusted decision content before persistence.</summary>
public static class DecisionText
{
    public static void Required(string value, int maximum, string parameter)
    {
        Guard.IsNotNullOrWhiteSpace(value, parameter);
        Guard.HasSizeLessThanOrEqualTo(value, maximum, parameter);
    }

    public static void Identifier(string value, string parameter)
    {
        Required(value, 200, parameter);
        Guard.IsFalse(value.Any(char.IsControl), parameter);
    }
}
