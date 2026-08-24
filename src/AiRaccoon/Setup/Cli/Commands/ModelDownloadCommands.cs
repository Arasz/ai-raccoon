using AiRaccoon.Infrastructure.Embedding.Manifest;
using System.CommandLine;
using AiRaccoon.Infrastructure.Assets;
using AiRaccoon.Core.Memory.Code;
using AiRaccoon.Infrastructure.Embedding.Download;

namespace AiRaccoon.Setup.Cli.Commands;

/// <summary>
///     Handler for <c>ai-raccoon model download &lt;repo-id&gt;</c> (plan D4/D8): resolves the HF
///     tree, verifies every download against its LFS-oid SHA-256 pin, writes manifest.json, and
///     never activates the model — <c>model embedding set local &lt;dir&gt;</c> (or <c>model code set local &lt;dir&gt;</c> for the code corpus) is the explicit next step.
///     Downloading is a one-shot CLI operation, so the HTTP client is created per run.
/// </summary>
internal sealed class ModelDownloadCommands(
    IHttpClientFactory httpClientFactory,
    IModelDownloadPlanner? planner = null,
    IOnnxGraphProbeReader? probeReader = null,
    IOnnxSmokeTester? smokeTester = null,
    IDiskSpaceProvider? diskSpace = null,
    ISentencePieceVocabularyReader? vocabularyReader = null,
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

        var request = new ModelDownloadRequest(repoId, revision, targetDir, explicitFiles, dryRun, yes,
            message => PromptConfirm(streams, message));
        return await ExecuteAsync(request, streams, true, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    ///     #422: the download half of <c>model code set default</c>. Same service, same pins, same
    ///     target-directory convention as <c>model download</c> — it just knows the repo id, so a
    ///     fresh install never has to.
    /// </summary>
    public Task<int> DownloadDefaultCodeModelAsync(string targetDir, StandardStreams streams,
        CancellationToken cancellationToken) =>
        ExecuteAsync(
            new ModelDownloadRequest(CodeEngineSetup.DefaultModelRepoId, "main", targetDir,
                Confirm: message => PromptConfirm(streams, message)),
            // No "activate it next" hint: this call IS the download half of an activating verb, and
            // pointing at 'model embedding set local' here would send the reader at the MEMORY engine.
            streams, activationHint: false, cancellationToken);

    private async Task<int> ExecuteAsync(ModelDownloadRequest request, StandardStreams streams,
        bool activationHint, CancellationToken cancellationToken)
    {
        var (repoId, revision, targetDir, dryRun) = (request.RepoId, request.Revision, request.TargetDirectory, request.DryRun);
        var http = httpClientFactory.CreateClient();
        var service = new ModelDownloadService(
            new HfTreeClient(http, endpoint ?? "https://huggingface.co"),
            new AssetDownloader(http),
            planner ?? new ModelDownloadPlanner(),
            probeReader ?? new OnnxGraphProbeReader(),
            smokeTester ?? new OrtOnnxSmokeTester(),
            diskSpace ?? new DiskSpaceProvider(),
            new EmbeddingManifestSerializer(),
            new EmbeddingManifestValidator(),
            vocabularyReader ?? new SentencePieceVocabularyReader());

        try
        {
            var result = await service.DownloadAsync(request, cancellationToken).ConfigureAwait(false);
            if (dryRun)
            {
                await PrintPlanAsync(result.Plan, streams).ConfigureAwait(false);
                return ExitCode.Success;
            }

            await streams.WriteOutputLineAsync(
                $"downloaded {repoId}@{revision} to {targetDir} ({result.DownloadedFiles.Count} file(s)); {EmbeddingManifest.FileName} written. " +
                (activationHint ? $"Activate with 'ai-raccoon model embedding set local {targetDir}' (or 'ai-raccoon model code set local {targetDir}'). " : string.Empty) +
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
