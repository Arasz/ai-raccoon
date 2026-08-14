using AiRaccoon.Core.Sync;
using AiRaccoon.Infrastructure.Options;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Unit.Sync;

/// <summary>
///     A misspelled sync.provider row used to resolve silently to S3 (architect finding A-F8), so a
///     typo could push a bank to the wrong backend, or — when the typo'd provider's credential rows
///     were absent — make sync a silent no-op. Absent still means the documented S3 default; only a
///     value that was written and not recognised is an error.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Unit)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public sealed class SyncProviderParserTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Parse_WithNoProviderRow_KeepsTheDocumentedS3Default(string? absent) =>
        SyncProviderParser.Parse(absent).ShouldBe(SyncProvider.S3);

    [Theory]
    [InlineData("s3", SyncProvider.S3)]
    [InlineData("S3", SyncProvider.S3)]
    [InlineData("azure", SyncProvider.Azure)]
    [InlineData("AZURE", SyncProvider.Azure)]
    [InlineData(" azure ", SyncProvider.Azure)]
    public void Parse_WithARecognisedValue_ResolvesIt(string value, SyncProvider expected) =>
        SyncProviderParser.Parse(value).ShouldBe(expected);

    [Theory]
    [InlineData("azrue")]      // the typo this finding is about
    [InlineData("aws")]        // plausible-but-wrong synonym
    [InlineData("gcs")]        // a backend that does not exist here
    [InlineData("s3;azure")]
    public void Parse_WithAnUnrecognisedValue_ThrowsInsteadOfSilentlyChoosingS3(string written)
    {
        var ex = Should.Throw<SyncProviderUnknownException>(() => SyncProviderParser.Parse(written));

        ex.Message.ShouldContain(written, Case.Insensitive,
            "the operator must see the value they actually wrote");
        ex.Message.ShouldContain("s3");
        ex.Message.ShouldContain("azure");
    }

    /// <summary>
    ///     The failure that motivated this: a typo'd provider with Azure rows present resolved to S3,
    ///     whose rows were absent, so IsConfigured went false and sync became a silent no-op.
    /// </summary>
    [Fact]
    public void Parse_TypoWithTheOtherProvidersRowsPresent_DoesNotDegradeToASilentNoOp() =>
        Should.Throw<SyncProviderUnknownException>(() => SyncProviderParser.Parse("azrue"));
}
