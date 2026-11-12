using System.Reflection;
using System.Runtime.CompilerServices;
using AiRaccoon.Access;
using AiRaccoon.Core.EventPump;
using AiRaccoon.Infrastructure.Assets;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Unit.Layering;

/// <summary>
///     Fails when a production assembly is built without the runtime-async compiler switch, so that
///     the switch in Directory.Build.targets cannot be lost without a red test.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Unit)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public sealed class RuntimeAsyncTests
{
    public static TheoryData<string, MethodInfo> AsyncMethods => new()
    {
        { "AiRaccoon", typeof(ForgettingPolicyService).GetMethod(nameof(ForgettingPolicyService.GetSweepThresholdAsync))! },
        { "AiRaccoon.Core", typeof(EventPump<>).GetMethod(nameof(EventPump<object>.WaitForItemAsync))! },
        { "AiRaccoon.Infrastructure", typeof(AssetDownloader).GetMethod(nameof(AssetDownloader.GetAsync))! }
    };

    [Theory]
    [MemberData(nameof(AsyncMethods))]
    public void AsyncProductionMethod_RunsAsRuntimeAsync(string assembly, MethodInfo method)
    {
        method.Module.Assembly.GetName().Name.ShouldBe(assembly);
        method.GetMethodImplementationFlags().HasFlag(MethodImplAttributes.Async)
            .ShouldBeTrue($"{assembly}: {method.Name} must carry the runtime-async flag (ADR-0145)");
        method.GetCustomAttribute<AsyncStateMachineAttribute>()
            .ShouldBeNull($"{assembly}: {method.Name} must not compile to a state machine (ADR-0145)");
    }
}
