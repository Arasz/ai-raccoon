namespace AiRaccoon.Infrastructure.Encryption;

/// <summary>A secret-manager CLI the bank key can come from: its executable name, the env var carrying its token, and what to tell a user who lacks it.</summary>
public sealed record CliSecretTool(string Name, string TokenEnvVar, string InstallHint)
{
    public static readonly CliSecretTool Bitwarden = new("bws", "BWS_ACCESS_TOKEN",
        "install the Bitwarden CLI (bws) and configure BWS_ACCESS_TOKEN (https://bitwarden.com/help/cli/)");

    public static readonly CliSecretTool OnePassword = new("op", "OP_SERVICE_ACCOUNT_TOKEN",
        "install the 1Password CLI (op) and sign in or set OP_SERVICE_ACCOUNT_TOKEN (https://www.1password.dev/cli)");

    /// <summary>Every supported tool; a child is started with every other tool's token removed.</summary>
    public static IReadOnlyList<CliSecretTool> All { get; } = [Bitwarden, OnePassword];

    public string NotFoundText => $"{Name} not found — {InstallHint}";
}
