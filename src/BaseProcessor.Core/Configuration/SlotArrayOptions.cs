using Microsoft.Extensions.Configuration;

namespace BaseProcessor.Core.Configuration;

/// <summary>
/// SLOT-01 / D-04 (deferred from Phase-50 D-07): the slot-array allocation-index whole-HASH random-TTL
/// knobs, bound from the SAME "Processor" config section as <see cref="ProcessorLivenessOptions"/>
/// (NOT the deleted Keeper BackupOptions). Two INDEPENDENT seconds-int auto-properties with baked
/// defaults; the bound config keys are <c>SlotArrayTtlMin</c>/<c>SlotArrayTtlMax</c> (no <c>Seconds</c>
/// suffix), each mapped via a <see cref="ConfigurationKeyNameAttribute"/>.
/// <para>D-05: the [min,max] range is [300,600]s — the 300 floor equals
/// <see cref="ProcessorLivenessOptions.ExecutionDataTtlSeconds"/>'s default so the L2[messageId] marker
/// outlives the data keys it indexes; the 600 ceiling is 2x for expiry jitter (avoids a synchronized
/// expiry herd). D-06: one EXPIRE(random) is applied to the WHOLE L2[messageId] HASH on each slot
/// write (allocation + guid.empty retire both refresh it).</para>
/// </summary>
public sealed class SlotArrayOptions
{
    [ConfigurationKeyName("SlotArrayTtlMin")]
    public int SlotArrayTtlMinSeconds { get; set; } = 300;   // D-05 floor = ExecutionDataTtl default

    [ConfigurationKeyName("SlotArrayTtlMax")]
    public int SlotArrayTtlMaxSeconds { get; set; } = 600;   // D-05 ceiling = 2x for jitter
}
