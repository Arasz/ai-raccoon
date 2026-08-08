using AiRaccoon.Core.Chunking;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Unit.Chunking;

/// <summary>
///     Heading-path extraction: the chunk's heading-path context,
///     e.g. "ADR-0011: Frontend Chassis Stack > Decision", or "" when it has none.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Unit)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public sealed class HeadingPathParserTests
{
    [Fact]
    public void NestedHeadings_BuildsPathDownToSection()
    {
        const string markdown = """
                                # Title

                                intro

                                ## Section

                                ### Sub

                                text
                                """;

        HeadingPathParser.Parse(markdown).ShouldBe("Title > Section");
    }

    [Fact]
    public void LastHeadingInChunk_DeterminesPath()
    {
        const string markdown = """
                                # ADR-0011: Chassis

                                ## Context

                                context text

                                ## Decision

                                decision text
                                """;

        HeadingPathParser.Parse(markdown).ShouldBe("ADR-0011 > Decision");
    }

    [Fact]
    public void TitleSegment_BeforeColon_IsTheIdentifier()
    {
        const string markdown = """
                                # ADR-0011: Frontend chassis stack — component library, router mode

                                ## Decision

                                ### 1. Component library

                                text
                                """;

        HeadingPathParser.Parse(markdown).ShouldBe("ADR-0011 > Decision");
    }

    [Fact]
    public void TitleSegment_BeforeEmDash_IsTheIdentifier()
    {
        const string markdown = """
                                # ADR-0004 — UUID version 7 for generated identifiers

                                ## Decision
                                """;

        HeadingPathParser.Parse(markdown).ShouldBe("ADR-0004 > Decision");
    }

    [Fact]
    public void SubSectionHeading_DoesNotExtendThePath()
    {
        const string markdown = """
                                # Title

                                ## Decision

                                ### 1. Component library

                                text
                                """;

        HeadingPathParser.Parse(markdown).ShouldBe("Title > Decision");
    }

    [Fact]
    public void TitleWithoutSeparator_KeptWhole()
    {
        const string markdown = """
                                # Frontend architecture

                                ## 3. The gluestack → shadcn/ui pivot
                                """;

        HeadingPathParser.Parse(markdown).ShouldBe("Frontend architecture > 3. The gluestack → shadcn/ui pivot");
    }

    [Fact]
    public void SiblingHeading_ReplacesPreviousAtSameLevel()
    {
        const string markdown = """
                                # Title

                                ## Context

                                ## Decision
                                """;

        HeadingPathParser.Parse(markdown).ShouldBe("Title > Decision");
    }

    [Fact]
    public void CodeFence_HeadingLookalikesIgnored()
    {
        const string markdown = """
                                # Title

                                ```
                                # not a heading
                                ## also not
                                ### still not
                                ```

                                ## Real Section

                                text
                                """;

        HeadingPathParser.Parse(markdown).ShouldBe("Title > Real Section");
    }

    [Fact]
    public void TildeCodeFence_HeadingLookalikesIgnored()
    {
        const string markdown = """
                                # Title

                                ~~~
                                # not a heading
                                ~~~

                                ## Real Section
                                """;

        HeadingPathParser.Parse(markdown).ShouldBe("Title > Real Section");
    }

    [Fact]
    public void ProvenanceHeader_Ignored()
    {
        // Ingested chunks carry a '## Source: <structured path>' header; it is metadata,
        // not a content heading, and must not appear in (or break) the heading path.
        const string markdown = """
                                [docs:adr] docs:adr:0011-frontend-chassis-stack.md#decision

                                ## Source: docs:adr:0011-frontend-chassis-stack.md#decision

                                # ADR-0011: Frontend chassis stack

                                ## Decision

                                text
                                """;

        HeadingPathParser.Parse(markdown).ShouldBe("ADR-0011 > Decision");
    }

    [Fact]
    public void NoHeadings_ReturnsEmpty()
    {
        const string markdown = """
                                plain text
                                no hashes here
                                """;

        HeadingPathParser.Parse(markdown).ShouldBeEmpty();
    }

    [Fact]
    public void BareHash_NotAHeading_ReturnsEmpty()
    {
        HeadingPathParser.Parse("#\n\ntext").ShouldBeEmpty();
    }

    [Fact]
    public void HashWithoutSpace_NotAHeading()
    {
        HeadingPathParser.Parse("#Title\n\ntext").ShouldBeEmpty();
    }

    [Fact]
    public void HeadingBeyondSixLevels_Ignored()
    {
        const string markdown = """
                                # Title

                                ####### seven levels
                                """;

        HeadingPathParser.Parse(markdown).ShouldBe("Title");
    }

    [Fact]
    public void EmptyInput_ReturnsEmpty()
    {
        HeadingPathParser.Parse("").ShouldBeEmpty();
    }

    [Fact]
    public void UnclosedFence_HeadingLookalikesIgnoredToEnd()
    {
        const string markdown = """
                                # Title

                                ```
                                # swallowed
                                """;

        HeadingPathParser.Parse(markdown).ShouldBe("Title");
    }
}
