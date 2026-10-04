using System.Net;
using AiRaccoon.Core.Projects;
using AiRaccoon.Setup.Cli.Commands;
using AiRaccoon.Tests.TestHelpers;
using AiRaccoon.Tests.Unit.Setup;
using NSubstitute;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Unit.Setup.Cli;

/// <summary>
///     `project id register | get | check`: each directory outcome maps to exactly the exit code,
///     stdout line and stderr text the lookup-before-mint contract hands ai-badger.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Unit)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public sealed class ProjectIdCommandsTests
{
    private const string Guid1 = "0b7c2b0e-6a8e-4f7e-9d1a-2f3c4d5e6f70";
    private const string Guid2 = "1c8d3c1f-7b9f-4a8f-8e2b-3a4d5e6f7081";

    private readonly IProjectDirectory _directory = Substitute.For<IProjectDirectory>();

    public static TheoryData<string[]> BlankArguments() => new()
    {
        new[] { "project", "id", "register", "" },
        new[] { "project", "id", "register", "   " },
        new[] { "project", "id", "register", Guid1, "--name", "" },
        new[] { "project", "id", "get", "--name", "" },
        new[] { "project", "id", "check", "" }
    };

    public static TheoryData<string[]> EveryVerb() => new()
    {
        new[] { "project", "id", "register", Guid1 },
        new[] { "project", "id", "get", "--name", "acme" },
        new[] { "project", "id", "check", Guid1 }
    };

    [Theory]
    [MemberData(nameof(BlankArguments))]
    public async Task Blank_Exits10WithoutTouchingTheDirectory(string[] args)
    {
        var (exit, stdout, stderr) = await Run(args);

        exit.ShouldBe(ErrorCode.Usage.InvalidValue);
        stdout.ShouldBeEmpty();
        stderr.ShouldStartWith("ai-raccoon: ");
        _directory.ReceivedCalls().ShouldBeEmpty();
    }

    [Fact]
    public async Task Register_New_PrintsRegistered()
    {
        _directory.RegisterAsync(Guid1, "acme", Arg.Any<CancellationToken>())
            .Returns(new ProjectRegistration(Guid1, ProjectRegistrationOutcome.Registered));

        var (exit, stdout, stderr) = await Run(["project", "id", "register", Guid1, "--name", "acme"]);

        exit.ShouldBe(0);
        stdout.ShouldBe($"registered {Guid1}{Environment.NewLine}");
        stderr.ShouldBeEmpty();
    }

    /// <summary>An alias prints the winner the server resolved it to, not the id that was typed.</summary>
    [Fact]
    public async Task Register_Existing_PrintsAlreadyRegisteredWithTheResolvedId()
    {
        _directory.RegisterAsync(Guid2, null, Arg.Any<CancellationToken>())
            .Returns(new ProjectRegistration(Guid1, ProjectRegistrationOutcome.AlreadyRegistered));

        var (exit, stdout, stderr) = await Run(["project", "id", "register", Guid2]);

        exit.ShouldBe(0);
        stdout.ShouldBe($"already registered {Guid1}{Environment.NewLine}");
        stderr.ShouldBeEmpty();
    }

    [Fact]
    public async Task Register_NotAGuid_Exits10StdoutEmpty()
    {
        _directory.RegisterAsync("my-repo", null, Arg.Any<CancellationToken>())
            .Returns(new ProjectRegistration("my-repo", ProjectRegistrationOutcome.NotAGuid));

        var (exit, stdout, stderr) = await Run(["project", "id", "register", "my-repo"]);

        exit.ShouldBe(ErrorCode.Usage.InvalidValue);
        stdout.ShouldBeEmpty();
        stderr.ShouldContain("not a guid");
    }

    /// <summary>The outcome lines echo a server-resolved id through ProjectIdText.</summary>
    [Fact]
    public async Task Register_HostileResolvedId_IsEscapedOnStdout()
    {
        _directory.RegisterAsync("my-repo", null, Arg.Any<CancellationToken>())
            .Returns(new ProjectRegistration("bad\rID", ProjectRegistrationOutcome.Registered));

        var (exit, stdout, stderr) = await Run(["project", "id", "register", "my-repo"]);

        exit.ShouldBe(0);
        stdout.ShouldContain("\\u000D");
        stdout.ShouldNotContain("\rID");
        stderr.ShouldBeEmpty();
    }

    [Fact]
    public async Task Register_HostileNotAGuid_IsEscapedOnStderr()
    {
        _directory.RegisterAsync("ghost\u001b", null, Arg.Any<CancellationToken>())
            .Returns(new ProjectRegistration("ghost\u001b", ProjectRegistrationOutcome.NotAGuid));

        var (exit, _, stderr) = await Run(["project", "id", "register", "ghost\u001b"]);

        exit.ShouldBe(ErrorCode.Usage.InvalidValue);
        stderr.ShouldContain("\\u001B");
        stderr.ShouldNotContain("\u001b");
    }

    [Fact]
    public async Task Register_Retired_Exits18StdoutEmpty()
    {
        _directory.RegisterAsync(Guid1, null, Arg.Any<CancellationToken>())
            .Returns(new ProjectRegistration(Guid1, ProjectRegistrationOutcome.Retired));

        var (exit, stdout, stderr) = await Run(["project", "id", "register", Guid1]);

        exit.ShouldBe(ErrorCode.Usage.ProjectUnknown);
        stdout.ShouldBeEmpty();
        stderr.ShouldContain("is retired");
    }

    /// <summary>Raw-text ids are printed exactly as stored, with no prefix, so a script can capture the line.</summary>
    [Theory]
    [InlineData(Guid1)]
    [InlineData("ai-badger")]
    public async Task Get_One_PrintsBareId(string stored)
    {
        _directory.FindByNameAsync("acme", Arg.Any<CancellationToken>()).Returns([stored]);

        var (exit, stdout, stderr) = await Run(["project", "id", "get", "--name", "acme"]);

        exit.ShouldBe(0);
        stdout.ShouldBe($"{stored}{Environment.NewLine}");
        stderr.ShouldBeEmpty();
    }

    [Fact]
    public async Task Get_None_Exits18()
    {
        _directory.FindByNameAsync("acme", Arg.Any<CancellationToken>()).Returns([]);

        var (exit, stdout, stderr) = await Run(["project", "id", "get", "--name", "acme"]);

        exit.ShouldBe(ErrorCode.Usage.ProjectUnknown);
        stdout.ShouldBeEmpty();
        stderr.ShouldContain("acme");
    }

    /// <summary>A hostile server answer is echoed through ProjectIdText: raw control characters never reach stderr.</summary>
    [Fact]
    public async Task Get_HostileNameAndIds_AreEscaped()
    {
        _directory.FindByNameAsync("ac\rme", Arg.Any<CancellationToken>()).Returns(["bad\u001bID", "good"]);

        var (exit, stdout, stderr) = await Run(["project", "id", "get", "--name", "ac\rme"]);

        exit.ShouldBe(ErrorCode.Usage.ProjectAmbiguous);
        stdout.ShouldBeEmpty();
        stderr.ShouldContain("\\u000D");
        stderr.ShouldContain("\\u001B");
        stderr.ShouldNotContain("\r");
        stderr.ShouldNotContain("\u001b");
    }

    [Fact]
    public async Task Get_HostileMissingName_IsEscaped()
    {
        _directory.FindByNameAsync("ac\u001bme", Arg.Any<CancellationToken>()).Returns([]);

        var (exit, stdout, stderr) = await Run(["project", "id", "get", "--name", "ac\u001bme"]);

        exit.ShouldBe(ErrorCode.Usage.ProjectUnknown);
        stdout.ShouldBeEmpty();
        stderr.ShouldContain("\\u001B");
        stderr.ShouldNotContain("\u001b");
    }

    [Fact]
    public async Task Get_Several_Exits19IdsOnStderrOnly()
    {
        _directory.FindByNameAsync("acme", Arg.Any<CancellationToken>()).Returns([Guid1, Guid2]);

        var (exit, stdout, stderr) = await Run(["project", "id", "get", "--name", "acme"]);

        exit.ShouldBe(ErrorCode.Usage.ProjectAmbiguous);
        stdout.ShouldBeEmpty();
        var lines = stderr.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        lines.Length.ShouldBe(3);
        lines[0].ShouldStartWith("ai-raccoon: ");
        lines[1..].ShouldBe([Guid1, Guid2]);
    }

    [Fact]
    public async Task Check_Known_Exits0()
    {
        _directory.CheckAsync("ai-badger", Arg.Any<CancellationToken>())
            .Returns(new ProjectIdCheck("ai-badger", ProjectIdStatus.Known));

        var (exit, stdout, stderr) = await Run(["project", "id", "check", "ai-badger"]);

        exit.ShouldBe(0);
        stdout.ShouldBe($"known ai-badger{Environment.NewLine}");
        stderr.ShouldBeEmpty();
    }

    [Fact]
    public async Task Check_Unknown_Exits18PrintsUnknown()
    {
        _directory.CheckAsync(Guid1, Arg.Any<CancellationToken>())
            .Returns(new ProjectIdCheck(Guid1, ProjectIdStatus.Unknown));

        var (exit, stdout, stderr) = await Run(["project", "id", "check", Guid1]);

        exit.ShouldBe(ErrorCode.Usage.ProjectUnknown);
        stdout.ShouldBe($"unknown {Guid1}{Environment.NewLine}");
        stderr.ShouldBeEmpty();
    }

    [Fact]
    public async Task Check_HostileUnknownId_IsEscapedOnStdout()
    {
        _directory.CheckAsync("bad\u001b", Arg.Any<CancellationToken>())
            .Returns(new ProjectIdCheck("bad\u001b", ProjectIdStatus.Unknown));

        var (exit, stdout, _) = await Run(["project", "id", "check", "bad\u001b"]);

        exit.ShouldBe(ErrorCode.Usage.ProjectUnknown);
        stdout.ShouldContain("\\u001B");
        stdout.ShouldNotContain("\u001b");
    }

    [Fact]
    public async Task Check_Retired_Exits18PrintsRetired()
    {
        _directory.CheckAsync(Guid1, Arg.Any<CancellationToken>())
            .Returns(new ProjectIdCheck(Guid1, ProjectIdStatus.Retired));

        var (exit, stdout, stderr) = await Run(["project", "id", "check", Guid1]);

        exit.ShouldBe(ErrorCode.Usage.ProjectUnknown);
        stdout.ShouldBe($"retired {Guid1}{Environment.NewLine}");
        stderr.ShouldBeEmpty();
    }

    /// <summary>A server older than `/projects` answers 404; that is the shared endpoint-missing code, not "unknown".</summary>
    [Fact]
    public async Task ServerWithoutProjects_Exits57()
    {
        _directory.CheckAsync(Guid1, Arg.Any<CancellationToken>())
            .Returns<ProjectIdCheck>(_ => throw new HttpRequestException("Not Found", null, HttpStatusCode.NotFound));

        var (exit, stdout, _) = await Run(["project", "id", "check", Guid1]);

        exit.ShouldBe(ErrorCode.Server.EndpointMissing);
        stdout.ShouldBeEmpty();
    }

    [Theory]
    [MemberData(nameof(EveryVerb))]
    public async Task NoVerb_ReadsStdin(string[] args)
    {
        _directory.RegisterAsync(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(new ProjectRegistration(Guid1, ProjectRegistrationOutcome.Registered));
        _directory.FindByNameAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns([Guid1]);
        _directory.CheckAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new ProjectIdCheck(Guid1, ProjectIdStatus.Known));

        var (exit, _, stderr) = await Run(args, new ThrowingReader());

        exit.ShouldBe(0, stderr);
    }

    private Task<(int Exit, string Out, string Err)> Run(string[] args, TextReader? stdin = null) =>
        CliRun.RunAsync(args, TestData.CreateConfigCommands(new FakeConfigStore(), projectIds: new ProjectIdCommands(_directory)), stdin);

    private sealed class ThrowingReader : TextReader
    {
        public override int Peek() => throw new InvalidOperationException("a project id verb read stdin");

        public override int Read() => throw new InvalidOperationException("a project id verb read stdin");

        public override string ReadLine() => throw new InvalidOperationException("a project id verb read stdin");

        public override Task<string?> ReadLineAsync() => throw new InvalidOperationException("a project id verb read stdin");

        public override ValueTask<string?> ReadLineAsync(CancellationToken cancellationToken) =>
            throw new InvalidOperationException("a project id verb read stdin");

        public override string ReadToEnd() => throw new InvalidOperationException("a project id verb read stdin");

        public override Task<string> ReadToEndAsync() => throw new InvalidOperationException("a project id verb read stdin");
    }
}
