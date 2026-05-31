using System.Reflection;
using Orchestrator.Consumers;
using Orchestrator.Hydration;
using Orchestrator.L1;
using Orchestrator.Scheduling;
using Xunit;

namespace BaseApi.Tests.Orchestrator;

/// <summary>
/// ORCH-SCALE-01 — the automatable half of the no-global-lock review (T-23-14). Reflection over the
/// four lifecycle-gating production types asserts NONE carries a STATIC lock primitive
/// (<see cref="SemaphoreSlim"/>, <see cref="Mutex"/>, or a bare static <c>lock</c>-<c>object</c>):
/// the per-workflow concurrency stripe must be an INSTANCE
/// <c>ConcurrentDictionary&lt;Guid, SemaphoreSlim&gt;</c> (per-instance state), never a static/global
/// singleton lock or process-uniqueness gate.
/// <para>
/// The NON-reflectable half — "no process-uniqueness assumption" (a 2nd replica N×-dispatches as the
/// accepted/deferred behavior, rather than crashing) — remains a documented MANUAL design review per
/// 23-VALIDATION.md "Manual-Only Verifications", confirmed at the Plan 05 blocking checkpoint.
/// </para>
/// </summary>
public sealed class NoGlobalLockTests
{
    public static IEnumerable<object[]> LifecycleTypes()
    {
        yield return [typeof(WorkflowL1Store)];
        yield return [typeof(WorkflowScheduler)];
        yield return [typeof(StartOrchestrationConsumer)];
        yield return [typeof(StopOrchestrationConsumer)];
        yield return [typeof(HydrationBackgroundService)];
    }

    [Theory]
    [MemberData(nameof(LifecycleTypes))]
    public void NoStaticLockFieldsGateLifecycle(Type type)
    {
        var staticFields = type.GetFields(BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);

        foreach (var field in staticFields)
        {
            Assert.NotEqual(typeof(SemaphoreSlim), field.FieldType);
            Assert.NotEqual(typeof(Mutex), field.FieldType);

            // A bare `static readonly object _lock = new();` lock-gate is forbidden too. (A `const string`
            // or a TimeSpan backoff constant is fine — only a plain System.Object lock target is banned.)
            Assert.False(
                field.FieldType == typeof(object),
                $"{type.Name}.{field.Name} is a static System.Object — a global lock target is forbidden (ORCH-SCALE-01).");
        }
    }

    [Fact]
    public void L1Store_StripeIsInstanceState_NotStatic()
    {
        // The per-workflow stripe (and the entries map) must be INSTANCE fields — per-replica state,
        // never a static/global singleton lock (ORCH-SCALE-01).
        var instanceFields = typeof(WorkflowL1Store)
            .GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);

        Assert.Contains(instanceFields, f =>
            f.FieldType.IsGenericType &&
            f.FieldType.GetGenericTypeDefinition() == typeof(System.Collections.Concurrent.ConcurrentDictionary<,>) &&
            f.FieldType.GetGenericArguments() is [_, var v] && v == typeof(SemaphoreSlim));

        // And no static fields at all on the store (no static stripe, no static lock).
        var staticFields = typeof(WorkflowL1Store)
            .GetFields(BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
        Assert.Empty(staticFields);
    }
}
