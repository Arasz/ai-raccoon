using System.CommandLine;
using System.CommandLine.Help;
using System.CommandLine.Invocation;
using System.CommandLine.Parsing;
using AiRaccoon.Infrastructure.Options;

namespace AiRaccoon.Setup;

/// <summary>What the user typed: 9 nullable properties; null means the option was not given.</summary>
internal sealed record CliOptions(
    string? Transport,
    string? DataRoot,
    InstallScope? InstallScope,
    string? AccessMode,
    string? EmbeddingModel,
    string? SyncEndpoint,
    string? SyncBucket,
    string? SyncRegion,
    string? SyncObjectKey);

/// <summary>
///     Parse outcome: options (null on help/version/errors), the help/version flags, the
///     collected error messages, and the raw parse result for rendering (help/errors go to
///     the writer passed to <see cref="CliArgs.Render"/> — never to stdout).
/// </summary>
internal sealed record CliParseResult(
    CliOptions? Options,
    bool ShowHelp,
    bool ShowVersion,
    IReadOnlyList<string> Errors,
    ParseResult ParseResult);

/// <summary>
///     The only type touching System.CommandLine: builds the 9-option root command, parses
///     args, and renders help/errors/version through a caller-supplied writer. Secrets are
///     never declared as options — the unknown-option parse error is the defense.
/// </summary>
internal static class CliArgs
{
    private const string Description = "MCP server exposing agent memory over sqlite-memory";

    internal static RootCommand BuildRootCommand()
    {
        var root = new RootCommand(Description);
        root.Add(new Option<McpTransport>("--transport") { Description = "MCP transport; https unsupported", HelpName = "stdio|http|https" });
        root.Add(new Option<string>("--data-root") { Description = "Bank data root", HelpName = "path" });
        root.Add(new Option<InstallScope>("--install-scope") { Description = "Install scope", HelpName = "user|project" });
        root.Add(new Option<string>("--access-mode") { Description = "Global access-mode seed", HelpName = "ro|rw|full" });
        root.Add(new Option<string>("--embedding-model") { Description = "Custom ONNX model path", HelpName = "path" });
        root.Add(new Option<string>("--sync-endpoint") { Description = "S3-compatible endpoint URL (sync off when unset)", HelpName = "url" });
        root.Add(new Option<string>("--sync-bucket") { Description = "S3 bucket name", HelpName = "name" });
        root.Add(new Option<string>("--sync-region") { Description = "S3 region", HelpName = "name" });
        root.Add(new Option<string>("--sync-object-key") { Description = "S3 object key", HelpName = "key" });
        // WebApplicationFactory bootstraps the entry point with these host-config flags;
        // declared hidden so the E2E host builds — values are intentionally never consumed
        // (CreateBuilder([]) drops generic host flags by design).
        root.Add(new Option<string>("--environment") { Hidden = true });
        root.Add(new Option<string>("--contentRoot") { Hidden = true });
        root.Add(new Option<string>("--applicationName") { Hidden = true });
        return root;
    }

    /// <summary>Parses args; never writes anything (stdout stays reserved for the stdio protocol).</summary>
    internal static CliParseResult Parse(string[] args)
    {
        var parseResult = BuildRootCommand().Parse(args, new ParserConfiguration { EnablePosixBundling = true });
        var errors = parseResult.Errors.Select(e => e.Message).ToList();
        var showHelp = parseResult.Action is HelpAction;
        // VersionOptionAction is a nested type with no public accessibility, so the
        // help/version idiom pins on its type name (pinned against 2.0.10).
        var showVersion = parseResult.Action?.GetType().Name == "VersionOptionAction";
        var options = showHelp || showVersion || errors.Count > 0 ? null : ReadOptions(parseResult);
        return new CliParseResult(options, showHelp, showVersion, errors, parseResult);
    }

    /// <summary>
    ///     Renders help, version, or parse errors to the given writer and returns the exit
    ///     code (0 help/version, 1 parse errors). Program.cs passes Console.Error.
    /// </summary>
    internal static int Render(CliParseResult result, TextWriter output) =>
        result.ParseResult.Invoke(new InvocationConfiguration { Output = output, Error = output });

    private static CliOptions? ReadOptions(ParseResult parseResult)
    {
        var transport = OptionValue(parseResult, "--transport", r => r.GetValueOrDefault<McpTransport>().ToString().ToLowerInvariant());
        var dataRoot = OptionValue(parseResult, "--data-root", r => r.GetValueOrDefault<string>());
        var scope = InstallScopeValue(parseResult);
        var accessMode = OptionValue(parseResult, "--access-mode", r => r.GetValueOrDefault<string>());
        var embeddingModel = OptionValue(parseResult, "--embedding-model", r => r.GetValueOrDefault<string>());
        var syncEndpoint = OptionValue(parseResult, "--sync-endpoint", r => r.GetValueOrDefault<string>());
        var syncBucket = OptionValue(parseResult, "--sync-bucket", r => r.GetValueOrDefault<string>());
        var syncRegion = OptionValue(parseResult, "--sync-region", r => r.GetValueOrDefault<string>());
        var syncObjectKey = OptionValue(parseResult, "--sync-object-key", r => r.GetValueOrDefault<string>());

        if (transport is null && dataRoot is null && scope is null && accessMode is null && embeddingModel is null &&
            syncEndpoint is null && syncBucket is null && syncRegion is null && syncObjectKey is null)
        {
            return null;
        }

        return new CliOptions(transport, dataRoot, scope, accessMode, embeddingModel,
            syncEndpoint, syncBucket, syncRegion, syncObjectKey);
    }

    private static T? OptionValue<T>(ParseResult parseResult, string name, Func<OptionResult, T> read) =>
        parseResult.GetResult(name) is OptionResult result ? read(result) : default;

    private static InstallScope? InstallScopeValue(ParseResult parseResult) =>
        parseResult.GetResult("--install-scope") is OptionResult result
            ? result.GetValueOrDefault<InstallScope>()
            : null;
}
