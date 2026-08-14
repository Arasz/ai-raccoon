using Xunit;

namespace AiRaccoon.Tests.TestHelpers;

/// <summary>
///     Serialises the test classes that mutate the process-global BWS_ACCESS_TOKEN. Two of them
///     set and restore it around a body, so running them concurrently lets one class's value leak
///     into another's fake-bws call and fail it with "invalid access token".
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class BwsAccessTokenCollection
{
    public const string Name = "bws-access-token";
}
