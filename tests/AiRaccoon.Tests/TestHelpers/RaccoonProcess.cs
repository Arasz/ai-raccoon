using System.ComponentModel;
using System.Diagnostics;
using CommunityToolkit.Diagnostics;

namespace AiRaccoon.Tests.TestHelpers;

/// <summary>What a child process left behind once it finished.</summary>
public sealed record ProcessRun(int ExitCode, string Stdout, string Stderr);

/// <summary>
///     Runs a child process to completion under a hard cap and kills the tree it leaves behind.
///     Process.Dispose() does not kill, so a cap that expires without this orphans child and
///     grandchild alike.
/// </summary>
public static class RaccoonProcess
{
    /// <summary>How long a killed tree gets to close its pipes before its output is written off.</summary>
    private static readonly TimeSpan DrainAfterKill = TimeSpan.FromSeconds(5);

    /// <summary>The built ai-raccoon binary sitting next to the test assembly.</summary>
    public static string Executable => ResolveExecutable(AppContext.BaseDirectory);

    /// <summary>Resolves the binary, failing with the path it wanted rather than a Win32Exception later.</summary>
    public static string ResolveExecutable(string baseDirectory)
    {
        Guard.IsNotNullOrWhiteSpace(baseDirectory);
        var path = Path.Combine(baseDirectory, OperatingSystem.IsWindows() ? "AiRaccoon.exe" : "AiRaccoon");
        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"no ai-raccoon binary at '{path}'; build src/AiRaccoon first", path);
        }

        return path;
    }

    public static Task<ProcessRun> RunAsync(
        IEnumerable<string> arguments, TimeSpan hardCap, CancellationToken cancellationToken) =>
        RunAsync(Executable, arguments, hardCap, cancellationToken);

    /// <summary>Throws <see cref="TimeoutException" /> naming the captured stderr once the cap passes.</summary>
    public static async Task<ProcessRun> RunAsync(
        string executable, IEnumerable<string> arguments, TimeSpan hardCap, CancellationToken cancellationToken)
    {
        Guard.IsNotNullOrWhiteSpace(executable);
        Guard.IsNotNull(arguments);
        Guard.IsGreaterThan(hardCap, TimeSpan.Zero);

        using var process = Process.Start(StartInfoFor(executable, arguments))!;
        var stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderr = process.StandardError.ReadToEndAsync(cancellationToken);
        try
        {
            await process.WaitForExitAsync(cancellationToken).WaitAsync(hardCap, cancellationToken);
            return new ProcessRun(process.ExitCode, await stdout, await stderr);
        }
        catch (TimeoutException)
        {
            KillTree(process);
            var (killedOut, killedErr) = (await DrainAsync(stdout), await DrainAsync(stderr));
            throw new TimeoutException(
                $"'{executable}' did not exit within {hardCap}; its process tree was killed. " +
                $"stderr: {killedErr} stdout: {killedOut}");
        }
        finally
        {
            KillTree(process);
        }
    }

    /// <summary>Kills the process and everything it spawned. A process already gone is not an error.</summary>
    public static void KillTree(Process process)
    {
        Guard.IsNotNull(process);
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or NotSupportedException)
        {
            // Never started, or it retired between the check and the kill: nothing left to stop.
        }
    }

    /// <summary>Kills the tree and waits for it to go. False means it is still running.</summary>
    public static async Task<bool> KillTreeAndWaitAsync(
        Process process, TimeSpan waitForExit, CancellationToken cancellationToken)
    {
        Guard.IsNotNull(process);
        Guard.IsGreaterThan(waitForExit, TimeSpan.Zero);
        KillTree(process);
        try
        {
            await process.WaitForExitAsync(cancellationToken).WaitAsync(waitForExit, cancellationToken);
            return true;
        }
        catch (TimeoutException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            // Never started: there is nothing to wait for.
            return true;
        }
    }

    private static async Task<string> DrainAsync(Task<string> output)
    {
        try
        {
            return await output.WaitAsync(DrainAfterKill);
        }
        catch (Exception ex) when (ex is TimeoutException or OperationCanceledException or IOException)
        {
            return "<unavailable>";
        }
    }

    private static ProcessStartInfo StartInfoFor(string executable, IEnumerable<string> arguments)
    {
        var startInfo = new ProcessStartInfo(executable)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        return startInfo;
    }
}
