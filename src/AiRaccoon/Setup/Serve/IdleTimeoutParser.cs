namespace AiRaccoon.Setup.Serve;

/// <summary>Parses serve's --idle-timeout span: 90s/30m/4h/1d, or 0 (watchdog disabled).</summary>
public static class IdleTimeoutParser
{
    public static bool TryParse(string? value, out TimeSpan timeout)
    {
        timeout = default;
        if (string.IsNullOrEmpty(value))
        {
            return false;
        }

        if (value == "0")
        {
            return true; // Zero = watchdog disabled
        }

        var amountText = value[..^1];
        var unit = value[^1];
        if (!int.TryParse(amountText, out var amount) || amount <= 0)
        {
            return false;
        }

        try
        {
            timeout = unit switch
            {
                's' => TimeSpan.FromSeconds(amount),
                'm' => TimeSpan.FromMinutes(amount),
                'h' => TimeSpan.FromHours(amount),
                'd' => TimeSpan.FromDays(amount),
                _ => default
            };
        }
        catch (ArgumentOutOfRangeException)
        {
            return false;
        }

        return timeout != default;
    }
}
