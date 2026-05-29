namespace BaseApi.Service.Features.Orchestration;

/// <summary>
/// Single domain exception for ALL four Phase 14 orchestration validation gates (D-01) —
/// NOT per-gate subclasses. The <see cref="Gate"/> discriminator plus the gate-specific
/// <see cref="Offending"/> payload distinguish the gates downstream.
///
/// <para>
/// <b>Mapping (D-02 / D-03):</b> claimed by <c>OrchestrationValidationExceptionHandler</c> →
/// HTTP 422 + RFC 7807. The handler drops <see cref="ErrorsExtension"/> into
/// <c>ProblemDetails.Extensions["errors"]</c> as the <c>{ gate, offending }</c> envelope.
/// The Phase 4 <c>CustomizeProblemDetails</c> customizer adds <c>correlationId</c> + <c>instance</c>
/// on every emission, so the handler MUST NOT set them.
/// </para>
///
/// <para>
/// <b>Information-disclosure guard (T-14-02):</b> the <see cref="Offending"/> payloads carry ONLY
/// entity Guids and flattened schema-validation messages — never stack traces or internal type names.
/// </para>
/// </summary>
public sealed class OrchestrationValidationException : Exception
{
    /// <summary>Gate discriminator: "cycle" | "missingStep" | "schemaEdge" | "payloadConfigSchema".</summary>
    public string Gate { get; }

    /// <summary>Gate-specific RFC 7807 problem title.</summary>
    public string Title { get; }

    /// <summary>Gate-specific structured offending payload (entity Guids / flattened messages only).</summary>
    public object Offending { get; }

    /// <summary>The D-03 envelope the handler writes into <c>Extensions["errors"]</c>.</summary>
    public object ErrorsExtension => new { gate = Gate, offending = Offending };

    private OrchestrationValidationException(string gate, string title, string detail, object offending)
        : base(detail)
    {
        Gate = gate;
        Title = title;
        Offending = offending;
    }

    /// <summary>Cycle gate (DFS) — the workflow step graph contains a cycle.</summary>
    public static OrchestrationValidationException Cycle(IReadOnlyList<Guid> stepChain)
        => new(
            "cycle",
            "Workflow contains a cycle",
            $"A cycle was detected in the workflow step graph: {string.Join(" -> ", stepChain)}.",
            new CycleOffending(stepChain));

    /// <summary>Missing-step gate (DFS) — a parent step references a child step id that does not exist.</summary>
    public static OrchestrationValidationException MissingStep(Guid parentStepId, Guid missingChildId)
        => new(
            "missingStep",
            "Workflow references a missing step",
            $"Step '{parentStepId}' references missing child step '{missingChildId}'.",
            new MissingStepOffending(parentStepId, missingChildId));

    /// <summary>Schema-edge gate — parent.Output schema id does not equal child.Input schema id.</summary>
    public static OrchestrationValidationException SchemaEdge(Guid parentStepId, Guid childStepId)
        => new(
            "schemaEdge",
            "Schema-edge mismatch between steps",
            $"Schema-edge mismatch on edge '{parentStepId}' -> '{childStepId}': parent output schema does not match child input schema.",
            new SchemaEdgeOffending(parentStepId, childStepId));

    /// <summary>Payload↔ConfigSchema gate — an assignment payload does not conform to its config schema.</summary>
    public static OrchestrationValidationException PayloadConfigSchema(Guid assignmentId, IReadOnlyList<string> errors)
        => new(
            "payloadConfigSchema",
            "Assignment payload does not conform to its config schema",
            $"Assignment '{assignmentId}' payload does not conform to its config schema.",
            new PayloadConfigSchemaOffending(assignmentId, errors));
}

/// <summary>Offending payload for the "cycle" gate — the chain of step ids forming the cycle.</summary>
public sealed record CycleOffending(IReadOnlyList<Guid> stepChain);

/// <summary>Offending payload for the "missingStep" gate.</summary>
public sealed record MissingStepOffending(Guid parentStepId, Guid missingChildId);

/// <summary>Offending payload for the "schemaEdge" gate.</summary>
public sealed record SchemaEdgeOffending(Guid parentStepId, Guid childStepId);

/// <summary>Offending payload for the "payloadConfigSchema" gate — assignment id + flattened messages.</summary>
public sealed record PayloadConfigSchemaOffending(Guid assignmentId, IReadOnlyList<string> errors);
