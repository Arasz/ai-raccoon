namespace AiRaccoon.Tests.TestHelpers;

/// <summary>Atomically swaps a fixture trust anchor while retaining owner-only permissions.</summary>
internal static class IdentityTestKey
{
    public static async Task WriteAsync(string path, string content, CancellationToken cancellationToken)
    {
        var temporary = path + ".fixture-swap";
        try
        {
            await File.WriteAllTextAsync(temporary, content, cancellationToken);
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(temporary, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }

            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            File.Delete(temporary);
        }
    }
}
