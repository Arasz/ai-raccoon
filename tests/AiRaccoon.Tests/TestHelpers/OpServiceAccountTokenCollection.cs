using Xunit;

namespace AiRaccoon.Tests.TestHelpers;

/// <summary>
///     Serialises the test classes that set or clear the process-global OP_SERVICE_ACCOUNT_TOKEN, so
///     one class's value never leaks into another class's fake-op child process.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class OpServiceAccountTokenCollection
{
    public const string Name = "op-service-account-token";
}
