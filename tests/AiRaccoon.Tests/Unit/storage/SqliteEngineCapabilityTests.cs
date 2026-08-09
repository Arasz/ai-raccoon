using Microsoft.Data.Sqlite;
using Shouldly;
using SQLitePCL;
using Xunit;

namespace AiRaccoon.Tests.Unit.storage;

/// <summary>
///     Pins the SQLite engine surface (docs/work/archive/2026-08-06-sqlite3mc-feature-surface.md): the app
///     depends on the 3.53.0 WAL-reset fix and on SQLite3MC's temp-in-memory and encryption properties.
///     Floor assertions (>= 3.53.4, 2.4.x) let patch bumps through but fail on a major/minor regression.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Unit)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public class SqliteEngineCapabilityTests
{
    private static SqliteConnection OpenMemory()
    {
        var conn = new SqliteConnection("Data Source=:memory:");
        conn.Open();
        return conn;
    }

    private static void Exec(SqliteConnection conn, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    [Fact]
    public void EngineVersion_IsAtLeastWalResetFixFloor()
    {
        var libVersion = Version.Parse(raw.sqlite3_libversion().utf8_to_string());

        libVersion.ShouldBeGreaterThanOrEqualTo(new Version(3, 53, 4),
            "the 3.53.0 WAL-reset corruption fix is a hard dependency (SyncService checkpoint + rekey journal switch)");
    }

    [Fact]
    public void Engine_ReportsSqlite3mc24x()
    {
        using var conn = OpenMemory();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT sqlite3mc_version()";

        cmd.ExecuteScalar().ShouldBeOfType<string>().ShouldStartWith("SQLite3 Multiple Ciphers 2.4");
    }

    [Fact]
    public void CompileOptions_TempStorageIsMemory()
    {
        using var conn = OpenMemory();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "PRAGMA compile_options";
        using var reader = cmd.ExecuteReader();
        var options = new List<string>();
        while (reader.Read())
        {
            options.Add(reader.GetString(0));
        }

        options.ShouldContain("TEMP_STORE=2",
            "encrypted banks must never spill plaintext temp b-trees to disk (SQLite3MC docs recommendation)");
    }

    [Fact]
    public void CompileOptions_IncludeSecureDeleteFts5AndPercentile()
    {
        using var conn = OpenMemory();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "PRAGMA compile_options";
        using var reader = cmd.ExecuteReader();
        var options = new List<string>();
        while (reader.Read())
        {
            options.Add(reader.GetString(0));
        }

        options.ShouldContain("SECURE_DELETE");
        options.ShouldContain("ENABLE_FTS5");
        options.ShouldContain("ENABLE_PERCENTILE");
    }

    [Fact]
    public void JsonbFunctions_AreAvailable()
    {
        using var conn = OpenMemory();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT jsonb('{\"a\":1}') IS NOT NULL";
        cmd.ExecuteScalar().ShouldBe(1L);

        cmd.CommandText = "SELECT count(*) FROM jsonb_each('{\"a\":1,\"b\":2}')";
        cmd.ExecuteScalar().ShouldBe(2L);
    }

    [Fact]
    public void PercentileFunctions_SimpleFormWorks()
    {
        using var conn = OpenMemory();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT median(x) FROM (SELECT 1 x UNION ALL SELECT 2 UNION ALL SELECT 3)";
        cmd.ExecuteScalar().ShouldBe(2L);

        cmd.CommandText = "SELECT percentile_cont(x, 0.5) FROM (SELECT 1 x UNION ALL SELECT 2 UNION ALL SELECT 3)";
        cmd.ExecuteScalar().ShouldBe(2L);
    }

    [Fact]
    public void AlterColumn_SetNotNull_Enforces()
    {
        using var conn = OpenMemory();
        Exec(conn, "CREATE TABLE nn(x INTEGER)");
        Exec(conn, "INSERT INTO nn VALUES (1)");
        Exec(conn, "ALTER TABLE nn ALTER COLUMN x SET NOT NULL");

        Should.Throw<SqliteException>(() => Exec(conn, "INSERT INTO nn VALUES (NULL)"));
        Exec(conn, "INSERT INTO nn VALUES (2)");
    }

    [Fact]
    public void AlterColumn_DropNotNull_AllowsNull()
    {
        using var conn = OpenMemory();
        Exec(conn, "CREATE TABLE dn(x INTEGER NOT NULL)");
        Exec(conn, "ALTER TABLE dn ALTER COLUMN x DROP NOT NULL");

        Exec(conn, "INSERT INTO dn VALUES (NULL)");
    }

    [Fact]
    public void AlterTable_AddConstraintCheck_Enforces()
    {
        using var conn = OpenMemory();
        Exec(conn, "CREATE TABLE cc(x INTEGER)");
        Exec(conn, "ALTER TABLE cc ADD CONSTRAINT cc_pos CHECK (x > 0)");

        Should.Throw<SqliteException>(() => Exec(conn, "INSERT INTO cc VALUES (-1)"));
        Exec(conn, "INSERT INTO cc VALUES (1)");
    }

    [Fact]
    public void AlterTable_DropConstraint_Removes()
    {
        using var conn = OpenMemory();
        Exec(conn, "CREATE TABLE dc(x INTEGER CONSTRAINT dc_pos CHECK (x > 0))");
        Exec(conn, "ALTER TABLE dc DROP CONSTRAINT dc_pos");

        Exec(conn, "INSERT INTO dc VALUES (-1)");
    }

    [Fact]
    public void StrictTables_RejectWrongType()
    {
        using var conn = OpenMemory();
        Exec(conn, "CREATE TABLE strict_t(x INTEGER) STRICT");

        Should.Throw<SqliteException>(() => Exec(conn, "INSERT INTO strict_t VALUES ('nope')"));
        Exec(conn, "INSERT INTO strict_t VALUES (1)");
    }

    [Fact]
    public void Vec0_KnnQuery_WorksWithLoadedExtension()
    {
        using var conn = OpenMemory();
        conn.EnableExtensions();
        conn.LoadVector();
        Exec(conn, "CREATE VIRTUAL TABLE v USING vec0(embedding float[2])");
        Exec(conn, "INSERT INTO v(rowid, embedding) VALUES (1, '[1,0]'), (2, '[0,1]')");

        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT rowid, distance FROM v WHERE embedding MATCH '[1,0]' AND k = 2 ORDER BY distance";
        using var reader = cmd.ExecuteReader();
        reader.Read().ShouldBeTrue();
        reader.GetInt64(0).ShouldBe(1L);
        reader.GetDouble(1).ShouldBe(0d, 0.0001);
    }
}
