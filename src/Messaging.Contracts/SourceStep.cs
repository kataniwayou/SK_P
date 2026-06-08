namespace Messaging.Contracts;

/// <summary>D-07: the SINGLE shared source-step sentinel predicate. Every consumer branches
/// "skip read / skip end-delete" off THIS helper — never an ad-hoc <c>== Guid.Empty</c> (Anti-Pattern).
/// Replaces the deleted MessageIdentity as the leaf's one canonical helper.</summary>
public static class SourceStep
{
    public static bool IsSource(Guid entryId) => entryId == Guid.Empty;
}
