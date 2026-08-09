using System.ComponentModel;
using System.Diagnostics;
using CommunityToolkit.Diagnostics;

namespace AiRaccoon.Infrastructure.Encryption;

/// <summary>bws could not be invoked or did not produce usable output.</summary>
public sealed class BwsInvocationException(string message, Exception? inner = null) : InvalidOperationException(message, inner);

/// <summary>Runs the bws CLI with redirected output and a hard timeout.</summary>
public sealed class BitwardenCliSecretManager : ICliSecretManager
{
    private const string NotFoundText =
        "bws not found — install the Bitwarden CLI (bws) and configure BWS_ACCESS_TOKEN (https://bitwarden.com/help/cli/)";

    private readonly string _executable;

    public BitwardenCliSecretManager(string executable = "bws")
    {
        Guard.IsNotNullOrWhiteSpace(executable);
        _executable = executable;
    }

    public BwsResult Run(IReadOnlyList<string> args, string? token, TimeSpan timeout)
    {
        Guard.IsNotNull(args);
        Guard.IsGreaterThan(timeout, TimeSpan.Zero);

        var startInfo = new ProcessStartInfo
        {
            FileName = _executable,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (var arg in args)
        {
            startInfo.ArgumentList.Add(arg);
        }

        if (token is not null)
        {
            // Never argv: any same-user process can read another process's command line (ps aux)
            // for its whole lifetime. The env var is the same channel the inherited-token path
            // already relies on (see ICliSecretManager), just set explicitly for this run.
            startInfo.EnvironmentVariables["BWS_ACCESS_TOKEN"] = token;
        }

        Process process;
        try
        {
            process = Process.Start(startInfo) ?? throw new BwsInvocationException(NotFoundText);
        }
        catch (Exception ex) when (ex is FileNotFoundException or Win32Exception)
        {
            throw new BwsInvocationException(NotFoundText);
        }

        using (process)
        {
            var stdoutTask = process.StandardOutput.ReadToEndAsync();
            var stderrTask = process.StandardError.ReadToEndAsync();

            if (!process.WaitForExit((int)timeout.TotalMilliseconds))
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
                {
                    // The process already exited between the timeout check and the kill.
                }

                process.WaitForExit();
                throw new BwsInvocationException($"bws timed out after {(int)timeout.TotalSeconds}s");
            }

            var stdout = stdoutTask.GetAwaiter().GetResult();
            var stderr = stderrTask.GetAwaiter().GetResult();

            if (process.ExitCode == 0 && string.IsNullOrWhiteSpace(stdout))
            {
                throw new BwsInvocationException("bws returned no output");
            }

            return new BwsResult(process.ExitCode, stdout, stderr);
        }
    }
}
