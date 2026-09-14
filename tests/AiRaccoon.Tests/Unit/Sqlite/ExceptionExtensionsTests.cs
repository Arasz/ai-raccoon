using AiRaccoon.Infrastructure.Sqlite;
using Microsoft.Data.Sqlite;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Unit.Sqlite;

/// <summary>
///     The one bank-busy classification: SQLITE_BUSY (5) / SQLITE_LOCKED (6) anywhere in the
///     exception chain — the WP12 write-lock convoy. Shared by the extraction pass, the tool
///     filter, the best-effort search-quality write, the access-rating bump, the VACUUM swallow
///     and the metrics-flush retry, so those six cannot drift apart.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Unit)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public sealed class ExceptionExtensionsTests
{
    [Fact]
    public void Busy_Code5_IsBankBusy() =>
        new SqliteException("database is locked", 5).IsBankBusy().ShouldBeTrue();

    [Fact]
    public void Locked_Code6_IsBankBusy() =>
        new SqliteException("database table is locked", 6).IsBankBusy().ShouldBeTrue();

    [Fact]
    public void BusyWrappedInAnotherException_IsBankBusy() =>
        new InvalidOperationException("open failed", new SqliteException("database is locked", 5))
            .IsBankBusy().ShouldBeTrue();

    [Fact]
    public void BusyNestedTwoLevelsDeep_IsBankBusy() =>
        new InvalidOperationException("outer",
                new ApplicationException("inner", new SqliteException("database is locked", 6)))
            .IsBankBusy().ShouldBeTrue();

    [Fact]
    public void NonBusySqliteError_IsNotBankBusy() =>
        new SqliteException("file is not a database", 26).IsBankBusy().ShouldBeFalse();

    [Fact]
    public void NonSqliteException_IsNotBankBusy() =>
        new InvalidOperationException("boom").IsBankBusy().ShouldBeFalse();

    /// <summary>Null is "nothing to classify", matching the helpers this replaces; the walk simply finds no candidate.</summary>
    [Fact]
    public void NullException_IsNotBankBusy() =>
        ((Exception)null!).IsBankBusy().ShouldBeFalse();
}
