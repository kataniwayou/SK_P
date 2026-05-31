namespace Messaging.Contracts;

/// <summary>
/// The terminal outcome a processor reports for a single step execution.
/// Int values mirror <c>StepEntryCondition.Previous*</c> (0-3) so an incoming
/// outcome can be matched against a next-step's <c>EntryCondition</c> by int equality.
/// </summary>
/// <remarks>
/// Plain <c>enum : int</c> — NO <c>JsonStringEnumConverter</c> is registered anywhere,
/// so this serializes as its underlying int on both the bus envelope and any projection.
/// The unconditional (4) and disabled (5) entry conditions are deliberately NOT on this
/// enum: a processor can only ever report one of the four real outcomes. Those two extra
/// conditions live as orchestrator-side int constants in the advancement helper.
/// </remarks>
public enum StepOutcome
{
    Processing = 0, // == StepEntryCondition.PreviousProcessing
    Completed = 1, // == StepEntryCondition.PreviousCompleted
    Failed = 2, // == StepEntryCondition.PreviousFailed
    Cancelled = 3, // == StepEntryCondition.PreviousCancelled
}
