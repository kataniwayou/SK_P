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
    /// The OTel-managed <c>logs-generic.otel-default</c> data stream is governed by the x-pack
    /// ECS index template whose <c>all_strings_to_keywords</c> dynamic template maps every string
    /// attribute DIRECTLY to <c>keyword</c> (with <c>ignore_above: 1024</c>) — NOT to <c>text</c>
    /// with a <c>fields.keyword</c> sub-field. A field-API probe confirms:
    /// <code>
    /// GET /logs-generic.otel-default/_mapping/field/attributes.CorrelationId
    /// → {"CorrelationId":{"type":"keyword","ignore_above":1024}}
    /// </code>
    /// So <c>term</c> queries must target <c>attributes.CorrelationId</c> directly. Querying
    /// against a non-existent <c>attributes.CorrelationId.keyword</c> sub-field returns zero
    /// hits and breaks every log-readback fact in the suite.
    /// </para>
    ///
    /// <para>
    /// Historical context: a prior IN-05 review fix (commit 9370e89) attempted to route through
    /// <c>.keyword</c> on the assumption that stock ES 8.x dynamic mapping creates a sub-field.
    /// That assumption holds for un-templated indices but NOT for the x-pack ECS-managed data
    /// stream this suite actually queries. The change broke 4 log-readback facts at Phase 11 UAT
    /// (LogLevelFilterTests, SchemasLogsE2ETests, LogExportTests ×2) and was reverted.
    /// </para>
    /// </summary>
    public const string CorrelationIdFieldPath = "attributes.CorrelationId";

    /// <summary>
    /// The OTLP field path for the per-step label attribute (<c>Step_*</c>), expressed as a dot-separated
    /// path suitable for ES <c>term</c> queries. Phase 66 / OBS-01 — grouping multi-hit <c>_search</c>
    /// results by <see cref="CorrelationIdFieldPath"/> + this path reconstructs per-run traces.
    ///
    /// <para>
    /// <b>DIRECT path — NO <c>.keyword</c> sub-field.</b> Same rationale pinned on
    /// <see cref="CorrelationIdFieldPath"/>: the OTel-managed <c>logs-generic.otel-default</c> data stream's
    /// x-pack ECS index template maps every string attribute DIRECTLY to <c>keyword</c> (no <c>fields.keyword</c>
    /// sub-field). Querying <c>attributes.StepLabel.keyword</c> returns ZERO hits — the same trap that broke
    /// 4 log-readback facts at Phase 11 UAT (commit 9370e89, reverted). Always query
    /// <c>attributes.StepLabel</c> directly.
    /// </para>
    /// </summary>
    public const string StepLabelFieldPath = "attributes.StepLabel";

    /// <summary>
    /// The OTLP field path for the per-execution-instance id attribute, expressed as a dot-separated path
    /// suitable for ES <c>term</c>/<c>exists</c> queries. The <c>ExecutionId</c> surfaces here via the
    /// SampleProcessor's nested <see cref="Messaging.Contracts.ExecutionLogScope.ExecutionId"/> BeginScope —
    /// the same MEL→OTLP scope bridge that carries <see cref="StepLabelFieldPath"/>/<see cref="CorrelationIdFieldPath"/>,
    /// so the <c>BeginScope</c> key name is preserved verbatim inside the OTLP-normalized <c>attributes</c> map.
    /// Grouping multi-hit <c>_search</c> results by <see cref="CorrelationIdFieldPath"/> + this path
    /// reconstructs per-(correlationId, executionId) instance traces (each spawned execution is its own run).
    ///
    /// <para>
    /// <b>DIRECT path — NO <c>.keyword</c> sub-field.</b> Same rationale pinned on
    /// <see cref="CorrelationIdFieldPath"/>/<see cref="StepLabelFieldPath"/>: the OTel-managed
    /// <c>logs-generic.otel-default</c> data stream's x-pack ECS index template maps every string attribute
    /// DIRECTLY to <c>keyword</c> (no <c>fields.keyword</c> sub-field), so <c>attributes.ExecutionId.keyword</c>
    /// returns ZERO hits. Always query <c>attributes.ExecutionId</c> directly. If a future Wave-0 probe shows
    /// a different live field, this is a single-const edit.
    /// </para>
    /// </summary>
    public const string ExecutionIdFieldPath = "attributes.ExecutionId";

    /// <summary>
    /// The OTLP field path for the per-step computed <c>Sum</c> attribute, expressed as a dot-separated path.
    /// Phase 66 / OBS-01 — read alongside <see cref="StepLabelFieldPath"/> when reconstructing per-run traces.
    ///
    /// <para>
    /// <b>DIRECT path — NO <c>.keyword</c> sub-field</b>, identical rationale to
    /// <see cref="CorrelationIdFieldPath"/> / <see cref="StepLabelFieldPath"/>: the ECS-managed
    /// <c>logs-generic.otel-default</c> data stream exposes no <c>.keyword</c> sub-field, so
    /// <c>attributes.Sum.keyword</c> returns zero hits. Query <c>attributes.Sum</c> directly.
    /// </para>
    /// </summary>
    public const string SumFieldPath = "attributes.Sum";

    /// <summary>
    /// The ES field used for the analyzer's observation-window <c>range</c> filter — Phase 66 / OBS-01
    /// (Plan 03 Task 1 Wave-0 probe, 2026-06-14). The fixture bounds its <c>Step_*</c> <c>_search</c> by
    /// <c>{ "range": { "&lt;this&gt;": { "gte": &lt;window_start&gt;, "lte": &lt;snapshot&gt; } } }</c>.
    ///
    /// <para>
    /// <b>Wave-0 probe (2026-06-14):</b> the data-stream mapping was probed against the live ES with
    /// <code>
    /// GET http://localhost:9200/logs-generic.otel-default/_mapping/field/@timestamp
    /// → {".ds-logs-generic.otel-default-2026.06.10-000001":{"mappings":{"@timestamp":
    ///      {"full_name":"@timestamp","mapping":{"@timestamp":{"type":"date","ignore_malformed":false}}}}}}
    /// </code>
    /// confirming <c>@timestamp</c> is present and a <c>date</c> type (the OTLP log-record observed
    /// timestamp the collector writes). This is the empirically-verified value baked here — NOT a guess.
    /// </para>
    ///
    /// <para>
    /// <b>Sum field type (A1) note:</b> the companion probe
    /// <c>GET .../_mapping/field/attributes.Sum</c> returned an EMPTY mapping (no <c>Step_*</c> sum docs
    /// had been indexed in the live data stream at probe time, so the dynamic field was not yet present).
    /// The ECS dynamic template maps numeric attributes to <c>long</c> (cf. <c>attributes.ContentLength</c>
    /// → <c>long</c>), so <c>attributes.Sum</c> will surface numeric once Step_* docs land; the fixture
    /// reads it DEFENSIVELY regardless (<c>TryGetInt32</c> then <c>GetString</c>+parse) since Sum is
    /// informational, never a completeness gate.
    /// </para>
    ///
    /// <para>
    /// If a future collector/ES upgrade changes the window timestamp field (e.g. switches to
    /// <c>observedTimestamp</c>), re-run the Wave-0 <c>_mapping/field</c> probe and update this single const.
    /// </para>
    /// </summary>
    public const string WindowTimestampFieldPath = "@timestamp";  // Wave-0-verified <date> (2026-06-14)

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
