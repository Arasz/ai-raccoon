namespace AiRaccoon.Benchmarks.Corpus;

/// <summary>A document in the benchmark corpus.</summary>
public sealed record CorpusDocument(string Id, string Title, string Body)
{
    public string Text => $"{Title}. {Body}";
}

/// <summary>A query with the ids of the documents judged relevant to it.</summary>
public sealed record CorpusQuery(string Id, string Text, IReadOnlyList<string> RelevantDocIds);

/// <summary>
///     Synthetic retrieval corpus: 8 topics x 5 documents, 16 queries (2 per topic), each query
///     judged relevant to its own topic's documents. Every topic uses distinctive vocabulary so
///     ranking quality is sensitive to the embedding model, not to keyword overlap alone.
/// </summary>
public static class BenchmarkCorpus
{
    public static IReadOnlyList<CorpusDocument> Documents { get; } = BuildDocuments();

    public static IReadOnlyList<CorpusQuery> Queries { get; } = BuildQueries();

    private static IReadOnlyList<CorpusDocument> BuildDocuments()
    {
        var docs = new List<CorpusDocument>();
        AddTopic(docs, "csharp-di", "C# dependency injection",
            "Microsoft.Extensions.DependencyInjection",
            "services.AddSingleton registers one shared instance for the whole application lifetime",
            "constructor injection declares the required services as constructor parameters",
            "the service collection resolves the dependency graph when the provider is built",
            "scoped services live for the duration of one request scope",
            "the container maps an interface to its concrete implementation at registration time");
        AddTopic(docs, "sqlite-vector", "SQLite vector search",
            "SQLite vector search",
            "sqlite-vec stores float32 embedding vectors as blobs in ordinary SQLite tables",
            "the vec0 virtual table builds a brute-force k-nearest-neighbour index over rows",
            "cosine distance is the default metric when comparing normalized embedding vectors",
            "vector indexes let an application run semantic search without a separate vector database",
            "query vectors are compared against every stored row when the table has no hierarchical index");
        AddTopic(docs, "middleware", "ASP.NET Core middleware",
            "ASP.NET Core middleware pipeline",
            "middleware components are chained and each one may short-circuit the request",
            "the Use extension appends a middleware delegate to the request pipeline",
            "exception handling middleware wraps downstream components in a try-catch boundary",
            "middleware runs in registration order for requests and reverse order for responses",
            "the terminal middleware produces the final HTTP response for the request");
        AddTopic(docs, "async", "async and await",
            "C# async await",
            "an async method returns a task that represents the ongoing operation",
            "await suspends the current method until the awaited task completes",
            "the thread pool resumes the continuation on a thread pool thread when the task finishes",
            "async void methods swallow exceptions and should be reserved for event handlers",
            "ConfigureAwait(false) avoids capturing the synchronization context on the continuation");
        AddTopic(docs, "docker", "Docker containers",
            "Docker containers",
            "a Docker image is a read-only template with layers for each build step",
            "containers share the host kernel but keep isolated filesystems and process namespaces",
            "the Dockerfile COPY instruction copies build context files into the image",
            "docker compose declares services, networks and volumes in a YAML file",
            "an entrypoint command runs when the container starts from the image");
        AddTopic(docs, "git-rebase", "git rebase",
            "git rebase",
            "git rebase replays commits from one branch onto the tip of another",
            "an interactive rebase lets you squash, reorder and reword commits",
            "rebasing rewrites commit hashes so it should not be used on shared branches",
            "a merge conflict during rebase pauses the operation until you resolve it",
            "git rebase --onto moves a range of commits onto a different base commit");
        AddTopic(docs, "unittest", "unit testing",
            "unit testing",
            "a unit test exercises one unit of behaviour in isolation from its dependencies",
            "test doubles stand in for collaborators so the unit under test is the only real object",
            "the arrange-act-assert structure separates setup from the exercised action and the checks",
            "code coverage measures which lines the test suite executed, not how well it asserts",
            "a fast unit test suite gives developers feedback within seconds after each change");
        AddTopic(docs, "rest", "REST APIs",
            "REST API design",
            "REST resources are identified by URLs and manipulated with HTTP verbs",
            "GET requests are safe and idempotent while POST creates a new resource",
            "a 201 Created response carries the Location header of the newly created resource",
            "hypermedia links tell the client which transitions are available next",
            "status codes 4xx report client errors and 5xx report server failures");

        return docs;
    }

    private static IReadOnlyList<CorpusQuery> BuildQueries()
    {
        var q = new List<CorpusQuery>();
        AddQuery(q, "csharp-di", "How do I register a singleton service in the dependency injection container?",
            "csharp-di");
        AddQuery(q, "csharp-di", "What happens when the service provider resolves the dependency graph?",
            "csharp-di");
        AddQuery(q, "sqlite-vector", "How does sqlite-vec compare embedding vectors for similarity?",
            "sqlite-vector");
        AddQuery(q, "sqlite-vector", "Can I run semantic search over vectors stored inside ordinary SQLite tables?",
            "sqlite-vector");
        AddQuery(q, "middleware", "What order does the request pipeline execute middleware components in?",
            "middleware");
        AddQuery(q, "middleware", "How do I stop the pipeline early from inside a middleware component?",
            "middleware");
        AddQuery(q, "async", "What does await do when the task has not finished yet?",
            "async");
        AddQuery(q, "async", "Why should async void methods be avoided?",
            "async");
        AddQuery(q, "docker", "What is the difference between a Docker image and a running container?",
            "docker");
        AddQuery(q, "docker", "How does docker compose declare the services of an application?",
            "docker");
        AddQuery(q, "git-rebase", "Why does rebasing rewrite commit hashes?",
            "git-rebase");
        AddQuery(q, "git-rebase", "What happens when a conflict occurs during an interactive rebase?",
            "git-rebase");
        AddQuery(q, "unittest", "What does arrange-act-assert structure look like in a unit test?",
            "unittest");
        AddQuery(q, "unittest", "How do test doubles help keep a unit test isolated?",
            "unittest");
        AddQuery(q, "rest", "Which HTTP status code reports a newly created resource?",
            "rest");
        AddQuery(q, "rest", "Are GET requests safe and idempotent in REST?",
            "rest");

        return q;
    }

    private static void AddTopic(List<CorpusDocument> docs, string id, string title, params string[] bodies)
    {
        for (var i = 0; i < bodies.Length; i++)
        {
            docs.Add(new CorpusDocument($"{id}-{i + 1}", i == 0 ? title : $"{title} note {i + 1}", bodies[i]));
        }
    }

    private static void AddQuery(List<CorpusQuery> queries, string id, string text, string topicId)
    {
        var relevant = Documents
            .Where(d => d.Id.StartsWith(topicId + "-", StringComparison.Ordinal))
            .Select(d => d.Id)
            .ToList();
        queries.Add(new CorpusQuery(id, text, relevant));
    }
}
