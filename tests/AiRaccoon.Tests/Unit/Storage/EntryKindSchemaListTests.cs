using AiRaccoon.Core.Memory;
using AiRaccoon.Infrastructure.Sqlite;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Unit.Storage;

/// <summary>The SQL spelling of the kind vocabulary is derived from <see cref="EntryKindWireNames" /> and keeps the text banks already hold.</summary>
[Trait(TestCategories.Category, TestCategories.Unit)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public sealed class EntryKindSchemaListTests
{
    [Fact]
    public void KindList_NamesEveryEntryKind()
    {
        var listed = MemorySchema.EntryKindList.Split(',').Select(word => word.Trim('\'')).ToHashSet(StringComparer.Ordinal);

        listed.ShouldBe(Enum.GetNames<EntryKind>().Select(name => name.ToLowerInvariant()), ignoreOrder: true);
    }

    [Fact]
    public void KindList_KeepsTheTextExistingBanksCarry()
    {
        MemorySchema.EntryKindList.ShouldBe("'none','memory','file'");
        MemorySchema.AddEntryTypeColumnSql.ShouldBe(
            "ALTER TABLE entries ADD COLUMN entry_type TEXT NOT NULL DEFAULT 'none' CHECK(entry_type IN ('none','memory','file'))");
    }
}
