using System.Globalization;
using AiRaccoon.Core.Projects;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Unit.Projects;

/// <summary>
///     The unregistered-project refusal must offer only remedies that can work — the register
///     command is quoted only for a guid spelling (as the canonical guid) — and must not carry raw
///     control characters from a caller-supplied id into stderr or an MCP refusal.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Unit)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public sealed class UnregisteredProjectExceptionTests
{
    private const string Canonical = "0b7c2b0e-6a8e-4f7e-9d1a-2f3c4d5e6f70";
    private const string BracedUpper = "{0B7C2B0E-6A8E-4F7E-9D1A-2F3C4D5E6F70}";

    [Fact]
    public void GuidSpelling_RegisterCommandCarriesTheCanonicalGuid()
    {
        var message = new UnregisteredProjectException(BracedUpper).Message;

        message.ShouldContain($"'ai-raccoon project id register {Canonical}'");
    }

    [Fact]
    public void NonGuid_MessageOmitsTheRegisterCommandAndNamesTheLookupAndRepairPaths()
    {
        var message = new UnregisteredProjectException("ghost; rm -rf x").Message;

        message.ShouldNotContain("project id register");
        message.ShouldContain("project_id_get");
        message.ShouldContain("repair project-ids");
    }

    [Theory]
    [InlineData("bad\u001bname")]
    [InlineData("bad\r\nname")]
    [InlineData("bad\u202ename")]
    [InlineData("bad\uE000name")]
    public void UnsafeRunes_AreEscapedInTheEchoedId(string hostile)
    {
        var message = new UnregisteredProjectException(hostile).Message;

        message.Any(IsUnsafe).ShouldBeFalse($"a raw unsafe rune reached the refusal: {message}");
    }

    /// <summary>A lone surrogate cannot ride through an InlineData row — xunit's case enumeration
    /// replaces it — so the value is built in the body to keep the row honest.</summary>
    [Fact]
    public void LoneSurrogate_IsEscapedInTheEchoedId()
    {
        var message = new UnregisteredProjectException("bad" + '\ud800' + "name").Message;

        message.Contains("\\uD800", StringComparison.Ordinal).ShouldBeTrue(message);
        message.Any(IsUnsafe).ShouldBeFalse($"a raw unsafe rune reached the refusal: {message}");
    }

    private static bool IsUnsafe(char ch) =>
        CharUnicodeInfo.GetUnicodeCategory(ch) is UnicodeCategory.Control or UnicodeCategory.Format
            or UnicodeCategory.Surrogate or UnicodeCategory.PrivateUse;
}
