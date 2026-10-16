using System.ComponentModel;
using System.Diagnostics;
using CommunityToolkit.Diagnostics;

namespace AiRaccoon.Infrastructure.Encryption;

/// <summary>How a secret-CLI run failed.</summary>
public enum CliSecretFailure
{
    NotInstalled,
    TimedOut,
    Failed
}

/// <summary>The secret CLI could not be invoked or did not produce usable output; <see cref="Failure" /> says how.</summary>
public sealed class CliSecretInvocationException(CliSecretFailure failure, string message, Exception? inner = null) : InvalidOperationException(message, inner)
{
    public CliSecretFailure Failure { get; } = failure;
}

/// <summary>Runs a secret-manager CLI (bws or op) with redirected output and a hard timeout.</summary>
public sealed class CliSecretManager : ICliSecretManager
{
    private readonly string _executable;
    private readonly CliSecretTool _tool;

    public CliSecretManager(CliSecretTool tool, string? executable = null)
    {
        Guard.IsNotNull(tool);
        _tool = tool;
        _executable = executable ?? tool.Name;
        Guard.IsNotNullOrWhiteSpace(_executable);
    }

    public async Task<CliSecretResult> RunAsync(IReadOnlyList<string> args, string? token, TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        Guard.IsNotNull(args);
        Guard.IsGreaterThan(timeout, TimeSpan.Zero);

        var startInfo = new ProcessStartInfo
        {
            FileName = _executable,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (var arg in args)
        {
            startInfo.ArgumentList.Add(arg);
        }

        foreach (var other in CliSecretTool.All.Where(other => other.TokenEnvVar != _tool.TokenEnvVar))
        {
            startInfo.Environment.Remove(other.TokenEnvVar);
        }

        if (token is not null)
        {
            // Never argv: any same-user process can read another process's command line (ps aux)
            // for its whole lifetime. The env var is the same channel the inherited-token path
            // already relies on (see ICliSecretManager), just set explicitly for this run.
            startInfo.EnvironmentVariables[_tool.TokenEnvVar] = token;
        }

        Process process;
        try
        {
            process = Process.Start(startInfo) ?? throw new CliSecretInvocationException(CliSecretFailure.NotInstalled, _tool.NotFoundText);
        }
        catch (Exception ex) when (ex is FileNotFoundException or Win32Exception)
        {
            throw new CliSecretInvocationException(CliSecretFailure.NotInstalled, _tool.NotFoundText);
        }

        using (process)
        {
            // Never let the child read the terminal this process inherited: close its stdin at once.
            process.StandardInput.Close();

            // Started before the wait, same as a sync ReadToEnd would be: the child's pipes must
            // drain concurrently with the wait, or a full buffer deadlocks it.
            var stdoutTask = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
            var stderrTask = process.StandardError.ReadToEndAsync(CancellationToken.None);

            using var timeoutCts = new CancellationTokenSource(timeout);
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

            try
            {
                await process.WaitForExitAsync(linkedCts.Token);
            }
            catch (OperationCanceledException)
            {
                TryKill(process);
                await process.WaitForExitAsync(CancellationToken.None);
                if (timeoutCts.IsCancellationRequested)
                {
                    throw new CliSecretInvocationException(CliSecretFailure.TimedOut, $"{_tool.Name} timed out after {(int)timeout.TotalSeconds}s");
                }

                throw; // the caller's own token cancelled the run — propagate as-is
            }

            var stdout = await stdoutTask;
            var stderr = await stderrTask;

            if (process.ExitCode == 0 && string.IsNullOrWhiteSpace(stdout))
            {
                throw new CliSecretInvocationException(CliSecretFailure.Failed, $"{_tool.Name} returned no output");
            }

            return new CliSecretResult(process.ExitCode, stdout, stderr);
        }

        static void TryKill(Process process)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
            {
                // The process already exited between the timeout check and the kill.
            }
        }
    }
}
