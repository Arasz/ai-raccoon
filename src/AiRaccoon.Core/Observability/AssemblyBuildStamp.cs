using System.Reflection;
using CommunityToolkit.Diagnostics;

namespace AiRaccoon.Core.Observability;

/// <summary>Reads <see cref="IBuildStamp" /> off a built assembly's SDK-generated attributes (see <see cref="BuildStamp.Parse" />).</summary>
public sealed class AssemblyBuildStamp : IBuildStamp
{
    private const string CommitTimestampMetadataKey = "CommitTimestamp";

    private readonly BuildStamp _stamp;

    public AssemblyBuildStamp(Assembly assembly)
    {
        Guard.IsNotNull(assembly);

        var informationalVersion = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        var commitTimestampText = assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(a => a.Key == CommitTimestampMetadataKey)?.Value;

        _stamp = BuildStamp.Parse(informationalVersion, commitTimestampText);
    }

    public string Version => _stamp.Version;

    public string Commit => _stamp.Commit;

    public DateTimeOffset? CommitTimestamp => _stamp.CommitTimestamp;
}
