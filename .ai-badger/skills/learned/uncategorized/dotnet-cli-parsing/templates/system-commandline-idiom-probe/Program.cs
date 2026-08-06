// Throwaway probe for re-pinning System.CommandLine behavior per package version.
// Mirrors the ai-raccoon CLI surface so its pinned idioms get re-verified verbatim.
// Expected 2.0.10 output (verified 2026-08-04):
//   [--version]            action=System.CommandLine.VersionOption+VersionOptionAction showVersion=True  exit=0
//   [--help]               action=System.CommandLine.Help.HelpAction                  showHelp=True      exit=0
//   [--help --bogus]       help wins, 0 errors, exit=0
//   [--data-root /a --data-root /b]  errors=1, action=ParseErrorAction, exit=1  (NOT last-wins)
//   [--environment Dev --contentRoot /tmp/x --applicationName App]  errors=0, action=null, exit=0
//       -> action=null means Program falls through to host build: the WebApplicationFactory path.
// PITFALLS (each cost real time once):
//   - VersionOptionAction is NESTED internal: Assembly.GetType("System.CommandLine.VersionOptionAction")
//     returns null; use "System.CommandLine.VersionOption+VersionOptionAction" if you need the Type.
//     parseResult.Action?.GetType().Name == "VersionOptionAction" works (nested Name has no `+`).
//   - `pr.Action?.GetType() is HelpAction` is ALWAYS false (a Type object vs HelpAction) — CS0184.
//     Test the instance: `pr.Action is HelpAction`.
//   - csproj needs <ImplicitUsings>enable</ImplicitUsings> or Console/TextWriter don't resolve.
using System.CommandLine;
using System.CommandLine.Help;
using System.CommandLine.Parsing;

var root = new RootCommand("probe");
root.Add(new Option<McpTransport>("--transport"));
root.Add(new Option<string>("--data-root"));
root.Add(new Option<string>("--access-mode"));
// WebApplicationFactory bootstrap flags (declare hidden in the real app):
root.Add(new Option<string>("--environment") { Hidden = true });
root.Add(new Option<string>("--contentRoot") { Hidden = true });
root.Add(new Option<string>("--applicationName") { Hidden = true });

foreach (var argSet in new[] {
    new[] { "--version" },
    new[] { "--help" },
    new[] { "--help", "--bogus" },
    new[] { "--version", "--bogus" },
    new[] { "--data-root", "/a", "--data-root", "/b" },
    new[] { "--transport", "ftp" },
    new[] { "--data-root" },
    new[] { "--bogus" },
    new[] { "--environment", "Development", "--contentRoot", "/tmp/x", "--applicationName", "AiRaccoon" },
})
{
    var pr = root.Parse(argSet, new ParserConfiguration { EnablePosixBundling = true });
    var action = pr.Action?.GetType();
    var showVersion = action?.Name == "VersionOptionAction";
    var showHelp = pr.Action is HelpAction; // the INSTANCE, never action (a Type) — see pitfalls
    Console.WriteLine(
        $"[{string.Join(' ', argSet)}] errors={pr.Errors.Count} action={action?.FullName ?? "null"} " +
        $"showHelp={showHelp} showVersion={showVersion} " +
        $"exit={pr.Invoke(new InvocationConfiguration { Output = TextWriter.Null, Error = TextWriter.Null })}");
}

public enum McpTransport
{
    Stdio = 0,
    Http = 1,
    Https = 2
}
