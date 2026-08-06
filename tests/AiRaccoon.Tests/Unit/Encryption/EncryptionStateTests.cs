using AiRaccoon.Core.Encryption;
using AiRaccoon.Infrastructure.Sqlite.Encryption;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Unit.Encryption;

[Trait(TestCategories.Category, TestCategories.Unit)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public sealed class EncryptionStateTests : IDisposable
{
    private readonly string _dataRoot = TestData.CreateTempRoot("ai-raccoon-tests");

    public void Dispose() => Directory.Delete(_dataRoot, true);

    private string BankPath() => Path.Combine(_dataRoot, "memory.db");

    private string SidecarPath() => EncryptionState.PathFor(BankPath());

    [Fact]
    public void PathFor_AppendsSourceSuffix() => EncryptionState.PathFor("/data/memory.db").ShouldBe("/data/memory.db.source");

    [Fact]
    public void Read_WhenSidecarAbsent_ReturnsNull()
    {
        var sidecar = new EncryptionState(BankPath());

        sidecar.Read().ShouldBeNull();
    }

    [Fact]
    public void WriteThenRead_RoundTripsBitwardenConfig()
    {
        var sidecar = new EncryptionState(BankPath());

        sidecar.Write(new EncryptionData("bitwarden", "project-1", "secret-1"));
        var read = sidecar.Read();

        read.ShouldNotBeNull();
        read.Source.ShouldBe("bitwarden");
        read.ProjectId.ShouldBe("project-1");
        read.SecretId.ShouldBe("secret-1");
    }

    [Fact]
    public void WriteThenRead_RoundTripsEnvConfigWithNullIds()
    {
        var sidecar = new EncryptionState(BankPath());

        sidecar.Write(new EncryptionData("env", null, null));
        var read = sidecar.Read();

        read.ShouldNotBeNull();
        read.Source.ShouldBe("env");
        read.ProjectId.ShouldBeNull();
        read.SecretId.ShouldBeNull();
    }

    [Fact]
    public void Write_OverwritesExistingSidecar()
    {
        var sidecar = new EncryptionState(BankPath());

        sidecar.Write(new EncryptionData("env", null, null));
        sidecar.Write(new EncryptionData("bitwarden", "p2", "s2"));
        var read = sidecar.Read();

        read.ShouldNotBeNull();
        read.Source.ShouldBe("bitwarden");
        read.ProjectId.ShouldBe("p2");
        read.SecretId.ShouldBe("s2");
    }

    [Fact]
    public void Write_IsAtomic_LeavesNoTempFilesBehind()
    {
        var sidecar = new EncryptionState(BankPath());

        sidecar.Write(new EncryptionData("bitwarden", "p1", "s1"));

        Directory.GetFiles(_dataRoot).ShouldHaveSingleItem();
        File.ReadAllText(SidecarPath()).ShouldContain("\"source\":\"bitwarden\"");
    }

    [Fact]
    public void Write_SetsUnixMode0600()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var sidecar = new EncryptionState(BankPath());

        sidecar.Write(new EncryptionData("env", null, null));

        File.GetUnixFileMode(SidecarPath()).ShouldBe(UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }

    [Fact]
    public void Delete_RemovesTheSidecar()
    {
        var sidecar = new EncryptionState(BankPath());
        sidecar.Write(new EncryptionData("bitwarden", "p1", "s1"));

        sidecar.Delete();

        File.Exists(SidecarPath()).ShouldBeFalse();
        sidecar.Read().ShouldBeNull();
    }

    [Fact]
    public void Delete_WhenSidecarAbsent_DoesNotThrow()
    {
        var sidecar = new EncryptionState(BankPath());

        sidecar.Delete();
    }

    [Fact]
    public void Read_CorruptJson_ThrowsNamingThePath()
    {
        File.WriteAllText(SidecarPath(), "{not json");
        var sidecar = new EncryptionState(BankPath());

        var ex = Should.Throw<EncryptionSourceException>(() => sidecar.Read());

        ex.Message.ShouldContain(SidecarPath());
    }

    [Fact]
    public void Read_EmptyFile_Throws()
    {
        File.WriteAllText(SidecarPath(), string.Empty);
        var sidecar = new EncryptionState(BankPath());

        Should.Throw<EncryptionSourceException>(() => sidecar.Read());
    }

    [Fact]
    public void Read_UnknownSource_ThrowsNamingThePath()
    {
        File.WriteAllText(SidecarPath(), """{"source":"ssh-key"}""");
        var sidecar = new EncryptionState(BankPath());

        var ex = Should.Throw<EncryptionSourceException>(() => sidecar.Read());

        ex.Message.ShouldContain(SidecarPath());
        ex.Message.ShouldContain("ssh-key");
    }

    [Fact]
    public void Read_IgnoresUnknownExtraFields()
    {
        File.WriteAllText(SidecarPath(), """{"source":"env","extra":1}""");
        var sidecar = new EncryptionState(BankPath());

        var read = sidecar.Read();

        read.ShouldNotBeNull();
        read.Source.ShouldBe("env");
    }

    [Fact]
    public void Write_InvalidSource_Throws()
    {
        var sidecar = new EncryptionState(BankPath());

        Should.Throw<ArgumentException>(() => sidecar.Write(new EncryptionData("ssh-key", null, null)));
    }
}
