using System.Reflection;
using System.Runtime.CompilerServices;
using AiRaccoon.Core.Observability;
using AiRaccoon.Setup.Serve;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Unit.Observability;

/// <summary>
///     WP13's own gate: a fifth background service may not arrive dark. Derived from the assembly
///     graph rather than a list of the four we know about, so a new hosted service joins the guard
///     by existing (the shape of LoggerMessageEventIdTests' walk).
/// </summary>
[Trait(TestCategories.Category, TestCategories.Unit)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public sealed class BackgroundInstrumentationCoverageTests
{
    [Fact]
    public void EveryHostedService_TakesTheOperationTelemetryPort()
    {
        var dark = HostedServices()
            .Where(t => !t.GetConstructors()
                .Any(c => c.GetParameters().Any(p => p.ParameterType == typeof(IOperationTelemetry))))
            .Select(t => t.FullName ?? t.Name)
            .ToList();

        dark.ShouldBeEmpty(
            $"Uninstrumented background service(s): {string.Join(", ", dark)}. Take IOperationTelemetry "
            + "and open a scope per pass, or the pass is invisible to the collector.");
    }

    /// <summary>
    ///     H7's own gate: taking the port is not enough — SweepHostedService took IOperationTelemetry
    ///     from day one and this file's other test stayed green throughout, while the reaper never
    ///     called NoteWork() and so never emitted a span even on a pass that deleted rows. This scans
    ///     each hosted service's actual pass logic (including inside its async state machine, where
    ///     the IL really lives) for a call to IOperationScope.NoteWork — a service that never calls it
    ///     anywhere can never be worth a span, no matter what it does.
    /// </summary>
    [Fact]
    public void EveryHostedService_InvokesNoteWorkSomewhereInItsPassLogic()
    {
        var dark = HostedServices()
            .Where(t => !InvokesNoteWork(t))
            .Select(t => t.FullName ?? t.Name)
            .ToList();

        dark.ShouldBeEmpty(
            $"Hosted service(s) that never call NoteWork(): {string.Join(", ", dark)}. Taking "
            + "IOperationTelemetry is not enough — without a NoteWork() call somewhere in the pass, "
            + "no pass this service runs can ever produce a span, however much work it did.");
    }

    /// <summary>The guard's reach, not a second copy of the list: a walk that found nothing would
    /// pass the check above forever.</summary>
    [Fact]
    public void TheGuard_ReachesBothProductAssembliesThatHostServices()
    {
        var found = HostedServices();

        found.ShouldNotBeEmpty();
        found.ShouldContain(typeof(IdleWatchdog)); // the server project
        found.ShouldContain(t => t.Assembly.GetName().Name == "AiRaccoon.Infrastructure");
    }

    private static IReadOnlyList<Type> HostedServices() =>
    [
        .. ProductAssemblies.All()
            .SelectMany(a => a.GetTypes())
            .Where(t => t is { IsAbstract: false, IsInterface: false } && typeof(IHostedService).IsAssignableFrom(t))
    ];

    private static readonly MethodInfo NoteWorkMethod = typeof(IOperationScope).GetMethod(nameof(IOperationScope.NoteWork))
        ?? throw new InvalidOperationException("IOperationScope.NoteWork not found by reflection.");

    private static bool InvokesNoteWork(Type hostedServiceType) =>
        MethodsToScan(hostedServiceType).Any(m => CallsNoteWork(m));

    /// <summary>Every method declared directly on the type, plus (for an async method) the
    /// compiler-generated state machine's MoveNext — that is where an `await`-laced pass's real
    /// IL body lives, not the stub the compiler leaves on the async method itself.</summary>
    private static IEnumerable<MethodBase> MethodsToScan(Type type)
    {
        const BindingFlags all = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance
            | BindingFlags.Static | BindingFlags.DeclaredOnly;

        foreach (var method in type.GetMethods(all))
        {
            yield return method;

            var stateMachineAttribute = method.GetCustomAttribute<AsyncStateMachineAttribute>();
            if (stateMachineAttribute?.StateMachineType is { } stateMachineType)
            {
                var moveNext = stateMachineType.GetMethod("MoveNext",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly);
                if (moveNext is not null)
                {
                    yield return moveNext;
                }
            }
        }
    }

    /// <summary>Scans a method body's IL for a callvirt against IOperationScope.NoteWork.
    /// Structural, not behavioral: it proves the call site exists, not that it is reached.</summary>
    private static bool CallsNoteWork(MethodBase method)
    {
        var body = method.GetMethodBody();
        var il = body?.GetILAsByteArray();
        if (il is null)
        {
            return false;
        }

        var module = method.Module;
        var typeArgs = method.DeclaringType?.IsGenericType == true
            ? method.DeclaringType.GetGenericArguments()
            : null;
        var methodArgs = method is MethodInfo { IsGenericMethod: true } generic
            ? generic.GetGenericArguments()
            : null;

        for (var i = 0; i < il.Length - 4; i++)
        {
            // callvirt = 0x6F, a 1-byte opcode followed by a 4-byte metadata token.
            if (il[i] != 0x6F)
            {
                continue;
            }

            var token = BitConverter.ToInt32(il, i + 1);
            try
            {
                if (module.ResolveMethod(token, typeArgs, methodArgs) == NoteWorkMethod)
                {
                    return true;
                }
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or NotSupportedException)
            {
                // Not every token resolves cleanly outside its exact generic context; a token
                // this scan cannot resolve is not evidence either way, so it is skipped.
            }
        }

        return false;
    }
}
