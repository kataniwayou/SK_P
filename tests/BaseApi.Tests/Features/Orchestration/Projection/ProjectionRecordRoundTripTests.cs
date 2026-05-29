using System;
using System.Collections.Generic;
using System.Text.Json;
using BaseApi.Service.Features.Orchestration.Projection;
using BaseApi.Service.Features.Step;
using Xunit;

namespace BaseApi.Tests.Features.Orchestration.Projection;

/// <summary>
/// Phase 15 L2-PROJECT-03/04/05/06 — verifies the four projection record DTOs serialize
/// under DEFAULT System.Text.Json options (no converters configured) to the exact locked
/// camelCase field shapes. The <c>[property: JsonPropertyName]</c> targeting is load-bearing
/// (RESEARCH Pitfall 1: a bare attribute binds to the positional ctor parameter and STJ
/// ignores it). Using default options proves the pins hold regardless of caller options.
/// <para>
/// Key locked shapes: <c>entryCondition</c> serializes as an int (NO
/// <c>JsonStringEnumConverter</c>, L2-PROJECT-04); <c>cron</c>:null falls out; empty
/// <c>nextStepIds</c> renders <c>[]</c>; processor field is <c>inputDefinition</c>
/// (NOT <c>definitionIn</c>); records round-trip back to equal values.
/// </para>
/// </summary>
[Trait("Phase", "15")]
public sealed class ProjectionRecordRoundTripTests
{
    // DEFAULT options — no camelCase policy, no enum converter. The [JsonPropertyName]
    // pins must hold on their own.
    private static readonly JsonSerializerOptions Default = new();

    private static readonly Guid Job = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly Guid Proc = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
    private static readonly Guid Step = Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc");

    private static LivenessProjection Liveness() =>
        new(new DateTime(2026, 5, 29, 0, 0, 0, DateTimeKind.Utc), 60, "ok");

    [Fact]
    public void WorkflowRoot_Serializes_Exact_CamelCase_Keys()
    {
        var root = new WorkflowRootProjection(
            EntryStepIds: new List<Guid> { Step },
            Cron: "* * * * *",
            JobId: Job,
            Liveness: Liveness(),
            CorrelationId: "corr-1");

        var json = JsonSerializer.Serialize(root, Default);

        Assert.Contains("\"entryStepIds\":", json);
        Assert.Contains("\"cron\":", json);
        Assert.Contains("\"jobId\":", json);
        Assert.Contains("\"liveness\":", json);
        Assert.Contains("\"correlationId\":", json);
        // Liveness nested camelCase keys.
        Assert.Contains("\"timestamp\":", json);
        Assert.Contains("\"interval\":", json);
        Assert.Contains("\"status\":", json);
    }

    [Fact]
    public void StepProjection_Serializes_EntryCondition_As_Int_Not_String()
    {
        var step = new StepProjection(
            EntryCondition: StepEntryCondition.Always,
            ProcessorId: Proc,
            Payload: "{}",
            NextStepIds: new List<Guid>());

        var json = JsonSerializer.Serialize(step, Default);

        Assert.Contains("\"entryCondition\":4", json);
        Assert.DoesNotContain("Always", json);
    }

    [Fact]
    public void WorkflowRoot_Serializes_Null_Cron_As_Null()
    {
        var root = new WorkflowRootProjection(
            EntryStepIds: new List<Guid> { Step },
            Cron: null,
            JobId: Job,
            Liveness: Liveness(),
            CorrelationId: "corr-1");

        var json = JsonSerializer.Serialize(root, Default);

        Assert.Contains("\"cron\":null", json);
    }

    [Fact]
    public void StepProjection_Serializes_Empty_NextStepIds_As_Empty_Array()
    {
        var step = new StepProjection(
            EntryCondition: StepEntryCondition.PreviousCompleted,
            ProcessorId: Proc,
            Payload: "{}",
            NextStepIds: new List<Guid>());

        var json = JsonSerializer.Serialize(step, Default);

        Assert.Contains("\"nextStepIds\":[]", json);
    }

    [Fact]
    public void ProcessorProjection_Serializes_Null_InputDefinition_With_Exact_Field_Name()
    {
        var proc = new ProcessorProjection(
            InputDefinition: null,
            OutputDefinition: "{\"type\":\"object\"}",
            Liveness: Liveness());

        var json = JsonSerializer.Serialize(proc, Default);

        Assert.Contains("\"inputDefinition\":null", json);
        Assert.Contains("\"outputDefinition\":", json);
        Assert.DoesNotContain("definitionIn", json);
    }

    [Fact]
    public void WorkflowRoot_RoundTrips_By_Value()
    {
        var root = new WorkflowRootProjection(
            EntryStepIds: new List<Guid> { Step },
            Cron: "* * * * *",
            JobId: Job,
            Liveness: Liveness(),
            CorrelationId: "corr-1");

        var rt = JsonSerializer.Deserialize<WorkflowRootProjection>(
            JsonSerializer.Serialize(root, Default), Default);

        // Records compare component-wise; the List<Guid> reference differs but contents match
        // via record equality only if same reference — so assert on scalar pins + sequence.
        Assert.NotNull(rt);
        Assert.Equal(root.Cron, rt!.Cron);
        Assert.Equal(root.JobId, rt.JobId);
        Assert.Equal(root.CorrelationId, rt.CorrelationId);
        Assert.Equal(root.Liveness, rt.Liveness);
        Assert.Equal(root.EntryStepIds, rt.EntryStepIds);
    }

    [Fact]
    public void StepProjection_RoundTrips_By_Value()
    {
        var step = new StepProjection(
            EntryCondition: StepEntryCondition.PreviousFailed,
            ProcessorId: Proc,
            Payload: "{\"a\":1}",
            NextStepIds: new List<Guid> { Step, Proc });

        var rt = JsonSerializer.Deserialize<StepProjection>(
            JsonSerializer.Serialize(step, Default), Default);

        Assert.NotNull(rt);
        Assert.Equal(step.EntryCondition, rt!.EntryCondition);
        Assert.Equal(step.ProcessorId, rt.ProcessorId);
        Assert.Equal(step.Payload, rt.Payload);
        Assert.Equal(step.NextStepIds, rt.NextStepIds);
    }

    [Fact]
    public void ProcessorProjection_RoundTrips_By_Value()
    {
        var proc = new ProcessorProjection(
            InputDefinition: "{\"type\":\"string\"}",
            OutputDefinition: null,
            Liveness: Liveness());

        var rt = JsonSerializer.Deserialize<ProcessorProjection>(
            JsonSerializer.Serialize(proc, Default), Default);

        Assert.NotNull(rt);
        Assert.Equal(proc.InputDefinition, rt!.InputDefinition);
        Assert.Equal(proc.OutputDefinition, rt.OutputDefinition);
        Assert.Equal(proc.Liveness, rt.Liveness);
    }
}
