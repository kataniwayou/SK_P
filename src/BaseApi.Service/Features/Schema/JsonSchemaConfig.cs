using Json.Schema;

namespace BaseApi.Service.Features.Schema;

/// <summary>
/// The SINGLE source of SSRF truth (D-05) for all JsonSchema.Net evaluation in the app.
/// Consumed by both the Phase 8 Schema validators (<see cref="SchemaCreateDtoValidator"/> /
/// <see cref="SchemaUpdateDtoValidator"/>) and the Phase 14 <c>PayloadConfigSchemaValidator</c>
/// (Plan 14-04).
///
/// <para>
/// <b>Static ctor (VALID-08 + VALID-09 / D-06 / Pitfall 3):</b> runs exactly once on first member
/// access. It sets <c>Dialect.Default = Dialect.Draft202012</c> (the library default is V1, not
/// 2020-12) and <c>SchemaRegistry.Global.Fetch = (_,_) =&gt; null</c> as defense-in-depth against
/// SSRF via external <c>$ref</c> tokens. Any consumer that evaluates a schema MUST reference a
/// member of this type (e.g. <see cref="DefaultOptions"/>) so the cctor fires BEFORE evaluation —
/// if no member is ever touched, the cctor never runs and the SSRF lockdown would silently regress.
/// </para>
/// </summary>
public static class JsonSchemaConfig
{
    static JsonSchemaConfig()
    {
        // VALID-08 — set the meta-schema dialect explicitly (library default is V1, not 2020-12).
        Dialect.Default = Dialect.Draft202012;
        // VALID-09 — defense-in-depth: explicit no-op even though library default is already (_,_) => null.
        SchemaRegistry.Global.Fetch = (_, _) => null;
    }

    /// <summary>
    /// Shared evaluation options (<c>OutputFormat.List</c>). Referencing this property is the trigger
    /// that fires the SSRF-locking static ctor (D-06 / Pitfall 3).
    /// </summary>
    public static EvaluationOptions DefaultOptions { get; } = new() { OutputFormat = OutputFormat.List };
}
