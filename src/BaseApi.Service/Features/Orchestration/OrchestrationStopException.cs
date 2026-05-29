namespace BaseApi.Service.Features.Orchestration;

/// <summary>
/// Stop-side 422 seam (D-04 / ORCH-STOP-03/04). Rather than introduce a second exception type +
/// a second <c>IExceptionHandler</c> registration, the Stop existence gate reuses the existing
/// <see cref="OrchestrationValidationException"/> (already mapped to HTTP 422 + RFC 7807 by
/// <c>OrchestrationValidationExceptionHandler</c>, with <c>correlationId</c> + <c>instance</c>
/// added by the Phase 4 customizer). This file hosts the Stop-specific factory + offending payload
/// as a <c>partial</c> extension so the Stop 422 path lives in its own file (matching the plan's
/// <c>files_modified</c> contract) while sharing the one validation-exception handler.
/// <para>
/// The factory carries the FULL set of missing workflow ids so the 422 detail + offending payload
/// list every workflow that had no L2 root key. The all-exist gate that THROWS this lives in
/// <c>OrchestrationService.StopAsync</c> (Plan 04) — never in the always-tolerant cleanup routine
/// (<c>RedisL2Cleanup</c>), which is shared with Start's pre-clean and must stay tolerant (Pitfall D).
/// </para>
/// </summary>
public sealed partial class OrchestrationValidationException
{
    /// <summary>
    /// Stop existence gate (D-04 / ORCH-STOP-03/04) — one or more requested workflow ids have no
    /// L2 root key in Redis. <paramref name="missing"/> is the FULL missing-id set; the 422 detail
    /// joins it with <c>", "</c> (mirrors <c>OrchestrationService</c>'s existence-check detail shape)
    /// and the <see cref="MissingRootsOffending"/> payload carries the structured list. The
    /// <see cref="Gate"/> discriminator is <c>"stopMissingRoots"</c>.
    /// </summary>
    public static OrchestrationValidationException MissingRoots(IReadOnlyList<Guid> missing)
        => new(
            "stopMissingRoots",
            "Workflow root keys not found in L2",
            $"No L2 projection exists for workflow id(s): {string.Join(", ", missing)}.",
            new MissingRootsOffending(missing));
}

/// <summary>Offending payload for the "stopMissingRoots" gate — the full set of missing workflow ids (D-04).</summary>
public sealed record MissingRootsOffending(IReadOnlyList<Guid> missing);
