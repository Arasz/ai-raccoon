namespace AiRaccon.Core.Common;

internal static class Guard
{
    public static void NotNullOrWhiteSpace(string? value, string paramName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("Value cannot be null or whitespace.", paramName);
        }
    }

    public static void GreaterThan(double value, double minExclusive, string paramName)
    {
        if (value <= minExclusive)
        {
            throw new ArgumentOutOfRangeException(paramName, value, $"Value must be greater than {minExclusive}.");
        }
    }

    public static void GreaterThanOrEqualTo(double value, double minInclusive, string paramName)
    {
        if (value < minInclusive)
        {
            throw new ArgumentOutOfRangeException(paramName, value, $"Value must be greater than or equal to {minInclusive}.");
        }
    }

    public static void InRange(double value, double minInclusive, double maxInclusive, string paramName)
    {
        if (value < minInclusive || value > maxInclusive)
        {
            throw new ArgumentOutOfRangeException(paramName, value, $"Value must be between {minInclusive} and {maxInclusive}.");
        }
    }
}
