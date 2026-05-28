namespace BaseApi.Tests.Observability.Helpers;

/// <summary>
/// Phase 11 D-06 + RESEARCH Open Q1 (resolved Wave 0, 2026-05-28) — verified constants for
/// the Elasticsearch index name + OTLP field path shape produced by the running collector
/// (0.152.0) + ES (8.15.5) + <c>mapping.mode: none</c> exporter config.
///
/// <para>
/// <b>Why these constants live in a file:</b> RESEARCH Pitfall 2 documents a known ambiguity
/// between the spec-predicted index name (<c>logs-generic-default</c>) and sk2_1's
/// live-observed name (<c>logs-generic.otel-default</c>). The Wave 0 probe (Plan 11-06 Task 0)
/// empirically resolves the ambiguity for the sk_p stack; the verified value is baked in
/// here so test code consumes the truth rather than the prediction.
/// </para>
///
/// <para>
/// <b>Wave 0 finding (2026-05-28):</b> Despite the collector config carrying
/// <c>mapping.mode: none</c>, elasticsearchexporter@v0.152.0 emits the deprecation warning
/// "mapping::mode config option is deprecated and ignored" (RESEARCH Pitfall 2 anticipated
/// this for v0.122.0+) and silently falls back to its current default behavior — which
/// produces the <c>logs-generic.otel-default</c> data stream with OTLP-normalized field
/// shape (lowercase top-level keys + nested <c>attributes</c> map with capital-A scope keys
/// from the .NET MEL bridge, e.g., <c>attributes.CorrelationId</c>). This is the same shape
/// sk2_1 observed live despite identical <c>mapping.mode: none</c> wiring.
/// </para>
///
/// <para>
/// If a future collector or ES upgrade changes the index name (e.g., v0.180.0 makes a new
/// default), re-run the Wave 0 probe and update these constants — the change should be
/// a single file edit + test re-run.
/// </para>
/// </summary>
public static class EsIndexNames
{
    /// <summary>
    /// The Elasticsearch data-stream alias the test polling helpers query.
    /// Verified value from Plan 11-06 Task 0 Wave 0 probe.
    /// Test code uses this in URL paths: <c>$"{LogsDataStream}/_search"</c>.
    /// </summary>
    public const string LogsDataStream = "logs-generic.otel-default";

    /// <summary>
    /// The OTLP field path for the correlation-id attribute, expressed as a dot-separated
    /// path suitable for ES `term` queries (e.g., <c>{"term":{"&lt;path&gt;":"value"}}</c>).
    /// Verified shape from Plan 11-06 Task 0 Wave 0 probe — the .NET MEL bridge preserves
    /// the <c>BeginScope</c> key name verbatim, so <c>CorrelationId</c> stays capital-C
    /// inside the OTLP-normalized lowercase <c>attributes</c> map.
    ///
    /// <para>
    /// IN-05 review fix: query against the <c>.keyword</c> sub-field instead of the raw
    /// analyzed text field. ES 8.x default dynamic mapping creates a <c>text</c> field
    /// with a <c>fields.keyword</c> sub-field for string attributes; <c>term</c> queries
    /// against the raw analyzed text only matched because 32-hex GUIDs accidentally
    /// survive the <c>standard</c> analyzer (no token splits on hex-only input). If a
    /// future correlation-id format adds dashes or other non-alphanumeric chars, the
    /// raw-field <c>term</c> query would silently miss; routing through
    /// <c>.keyword</c> makes the query robust to any correlation-id shape.
    /// </para>
    /// </summary>
    public const string CorrelationIdFieldPath = "attributes.CorrelationId.keyword";

    /// <summary>
    /// The OTLP field path prefix for resource attributes (service.name, service.version)
    /// expressed as a dot-separated path. Test code reads
    /// <c>hit._source.&lt;ResourceFieldPath&gt;[.service\.name]</c> — under the live
    /// otel-mode shape, this is lowercase <c>resource.attributes</c> with dotted keys.
    /// Verified shape from Plan 11-06 Task 0 Wave 0 probe.
    /// </summary>
    public const string ResourceAttributesFieldPath = "resource.attributes";

    /// <summary>
    /// Indicates whether the live ES index uses <c>mapping.mode: none</c> raw OTLP shape (capital
    /// top-level keys, flat attributes) or <c>mapping.mode: otel</c> normalized shape (lowercase
    /// top-level keys, dotted attribute paths). Verified value from Plan 11-06 Task 0 Wave 0 probe.
    /// </summary>
    public const string FieldShape = "otel";
}
