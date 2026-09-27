using AiRaccoon.Infrastructure.Maintenance;
using AiRaccoon.Infrastructure.Options;
using AiRaccoon.Setup;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Unit.Maintenance;

/// <summary>The runner walks its job list in order, and the chunk-boundary repair renumbers a note from the positions
/// it holds, so the note chunk order repair must come first.</summary>
[Trait(TestCategories.Category, TestCategories.Unit)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public sealed class NoteChunkOrderRepairJobOrderTests : IDisposable
{
    private readonly string _dataRoot = TestData.CreateTempRoot();

    public void Dispose() => TestData.DeleteTempRoot(_dataRoot);

    [Fact]
    public void NoteChunkOrderRepair_IsRegisteredBeforeTheChunkBoundaryRepair()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.RegisterMemoryServices(new InfrastructureOptions { DataRoot = _dataRoot, Scope = InstallScope.User });
        using var provider = services.BuildServiceProvider();

        var names = provider.GetRequiredService<IReadOnlyList<IMaintenanceJob>>().Select(job => job.Name).ToList();

        names.ShouldContain(NoteChunkOrderRepairJob.JobName);
        names.IndexOf(NoteChunkOrderRepairJob.JobName).ShouldBeLessThan(names.IndexOf(ChunkBoundaryRepairJob.JobName));
    }
}
