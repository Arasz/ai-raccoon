using System.CommandLine;
using System.Net.Http;
using AiRaccoon.Infrastructure.Assets;
using AiRaccoon.Infrastructure.Embedding.Download;

namespace AiRaccoon.Setup.Cli.Commands;

/// <summary>
///     Handler for <c>ai-raccoon model download &lt;repo-id&gt;</c> (plan D4/D8): resolves the HF
///     tree, verifies every download against its LFS-oid SHA-256 pin, writes manifest.json, and
///     never activates the model — <c>model set local &lt;dir&gt;</c> is the explicit next step.
///     Downloading is a one-shot CLI operation, so the HTTP client is created per run.
/// </summary>
internal sealed class ModelDownloadCommands(
    IHttpClientFactory httpClientFactory,
    IModelDownloadPlanner? planner = null,
    IOnnxGraphProbeReader? probeReader = null,
    IOnnxSmokeTester? smokeTester = null,
    IDiskSpaceProvider? diskSpace = null,
    string? endpoint = null)
{
    public async Task<int> RunAsync(ParseResult parseResult, string dataRoot, StandardStreams streams,
        CancellationToken cancellationToken)
    {
        var repoId = parseResult.GetValue<string>("repo-id")!;
        var revision = parseResult.GetResult("--revision") is not null
            ? parseResult.GetValue<string>("--revision")!
            : "main";
        var explicitFiles = parseResult.GetResult("--file") is not null
            ? parseResult.GetValue<string[]>("--file")
            : null;
        var targetDir = Path.GetFullPath(parseResult.GetResult("--dir") is not null
            ? parseResult.GetValue<string>("--dir")!
            : Path.Combine(dataRoot, "models", ModelSlug.Sanitize(repoId)));
        var dryRun = parseResult.GetValue<bool>("--dry-run");
        var yes = parseResult.GetValue<bool>("--yes");

        var http = httpClientFactory.CreateClient();
        var service = new ModelDownloadService(
            new HfTreeClient(http, endpoint ?? "https://huggingface.co"),
            new AssetDownloader(http),
            planner ?? new ModelDownloadPlanner(),
            probeReader ?? new OnnxGraphProbeReader(),
            smokeTester ?? new OrtOnnxSmokeTester(),
            diskSpace ?? new DiskSpaceProvider());
        var request = new ModelDownloadRequest(repoId, revision, targetDir, explicitFiles, dryRun, yes,
            message => PromptConfirm(streams, message));

        try
        {
            var result = await service.DownloadAsync(request, cancellationToken).ConfigureAwait(false);
            if (dryRun)
            {
                await PrintPlanAsync(result.Plan, streams).ConfigureAwait(false);
                return ExitCode.Success;
            }

            await streams.WriteOutputLineAsync(
                $"downloaded {repoId}@{revision} to {targetDir} ({result.DownloadedFiles.Count} file(s)); {AiRaccoon.Infrastructure.Embedding.Manifest.EmbeddingManifest.FileName} written. " +
                $"Activate with 'ai-raccoon model set local {targetDir}'. " +
                "Trust note: the SHA-256 pins were captured from Hugging Face's LFS oids before download — the first pin trusts the channel once; registry pins are the reviewed tier (plan D8).");
            return ExitCode.Success;
        }
        catch (ModelDownloadRejectedException ex)
        {
            await streams.WriteErrorLineAsync($"ai-raccoon: {ex.Message}").ConfigureAwait(false);
            return ExitCode.InvalidArgument;
        }
        catch (ModelDownloadPlanException ex)
        {
            await streams.WriteErrorLineAsync($"ai-raccoon: {ex.Message}").ConfigureAwait(false);
            return ExitCode.InvalidArgument;
        }
        catch (Exception ex) when (ex is ModelDownloadException or HfApiException or OnnxProbeException)
        {
            await streams.WriteErrorLineAsync($"ai-raccoon: {ex.Message}").ConfigureAwait(false);
            return ExitCode.ModelDownloadFailed;
        }
    }

    /// <summary>Prompts on stderr (stdout stays parseable) and reads the answer from stdin;
    /// EOF or anything but y/yes refuses the download.</summary>
    private static bool PromptConfirm(StandardStreams streams, string message)
    {
        streams.Error.WriteLine($"{message} [y/N] ");
        var answer = streams.Input.ReadLine();
        return answer is not null && (answer.Equals("y", StringComparison.OrdinalIgnoreCase)
                                      || answer.Equals("yes", StringComparison.OrdinalIgnoreCase));
    }

    private static async Task PrintPlanAsync(ModelDownloadPlan plan, StandardStreams streams)
    {
        await streams.WriteOutputLineAsync($"resolved {plan.RepoId}@{plan.Revision} — would download {plan.Files.Count} file(s), {plan.Files.Sum(f => f.Size):N0} bytes in total:");
        foreach (var file in plan.Files)
        {
            var pin = file.LfsSha256 is not null ? $"sha256 {file.LfsSha256[..16]}…" : "(tofu — pinned after download)";
            await streams.WriteOutputLineAsync($"  {file.Path,-48} {file.Size,14:N0} B  {pin}");
        }

        await streams.WriteOutputLineAsync(
            $"dims {plan.Dimensions}, ctx {plan.ContextWindowTokens}, tokenizer {plan.TokenizerFamily}, pooling {plan.PoolingMode} ({plan.PoolingProvenance})");
        await streams.WriteOutputLineAsync(
            "Trust note: SHA-256 pins come from Hugging Face's LFS oids — the first pin trusts the channel once; registry pins are the reviewed tier (plan D8).");
    }
}
