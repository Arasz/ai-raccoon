using Xunit;

namespace AiRaccoon.Tests.BDD;

/// <summary>
///     The code-corpus BDD feature drives a real watch pipeline (WatchHostedService/WatchCatchUp)
///     over real SQLite for its ignore-file and watch-containment scenarios — same Class B flake
///     exposure as FileWatcherCollection, so it runs serially against all other tests too.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class CodeCorpusCollection
{
    public const string Name = "code-corpus-bdd";
}
