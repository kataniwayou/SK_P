using BaseApi.Service.Features.Orchestration;

namespace BaseApi.Service.Features.Orchestration.Validation;

/// <summary>
/// Cycle + missing-step validation gate (D-07/D-08/D-14) — owns BOTH the cycle gate
/// (L1-VALIDATE-03) and the missing-step gate (L1-VALIDATE-04 / D-08 defense-in-depth).
/// <para>
/// <b>Two-set iterative DFS:</b> traversal uses an explicit <see cref="Stack{T}"/> frame plus
/// TWO bookkeeping sets — <c>onStack</c> (nodes currently on the active DFS path) and
/// <c>fullyVisited</c> (nodes whose entire subtree has been completed). A back-edge to a node
/// in <c>onStack</c> is a cycle; an edge to a node in <c>fullyVisited</c> is a legal
/// shared/fan-in subgraph (the two-set discriminator that prevents diamond/fan-in DAG
/// false-positives — D-14 / RESEARCH Pitfall 2). A single-<c>visited</c> set would false-flag a
/// diamond (A→B, A→C, B→D, C→D) as a cycle.
/// </para>
/// <para>
/// <b>NO recursion</b> anywhere — a crafted deep or cyclic graph would otherwise exhaust the call
/// stack, and <see cref="StackOverflowException"/> is UNCATCHABLE by <c>IExceptionHandler</c>
/// (it terminates the process). The explicit-stack form bounds traversal to managed heap (T-14-04).
/// </para>
/// <para>
/// On a true cycle it reconstructs the offending step chain from the active <c>path</c> list and
/// throws <see cref="OrchestrationValidationException.Cycle"/> (the chain drives the D-03 offending
/// body). On a dangling <c>NextStepId</c> it throws
/// <see cref="OrchestrationValidationException.MissingStep"/> with <c>(parentStepId, missingChildId)</c>.
/// A step whose <c>NextStepIds</c> is null/empty is terminal and passes.
/// </para>
/// </summary>
internal sealed class CycleDetector
{
    /// <summary>
    /// Runs the two-set iterative DFS over <c>snapshot.Steps[*].NextStepIds</c>, seeded from every
    /// <c>Workflow.EntryStepIds[*]</c>. Throws on the first cycle or missing-step encountered.
    /// <para>
    /// <b>Scope contract (WR-03):</b> the DFS is seeded ONLY from entry steps, so an entry-UNREACHABLE
    /// orphan subgraph is intentionally NOT visited by this gate — an unreachable step can never execute
    /// and so cannot contribute a runtime cycle. The schema-edge and payload↔config-schema gates walk the
    /// full <c>Steps</c>/<c>Assignments</c> sets by contrast; that divergence is by design (see the scope
    /// contract on <see cref="WorkflowGraphSnapshot"/>). To extend this gate to orphan subgraphs, sweep
    /// <c>snapshot.Steps.Keys</c> not yet in <c>fullyVisited</c> after the entry-seeded loop below.
    /// </para>
    /// </summary>
    public void Validate(WorkflowGraphSnapshot snapshot)
    {
        // Completed subtrees, SHARED across all entry seeds so a cleared subtree is never re-walked.
        var fullyVisited = new HashSet<Guid>();

        foreach (var workflow in snapshot.Workflows.Values)
        {
            foreach (var entryId in workflow.EntryStepIds ?? Enumerable.Empty<Guid>())
            {
                if (fullyVisited.Contains(entryId))
                {
                    continue;
                }

                // An EntryStepId that does not resolve to a step is itself a missing step. There is
                // no parent step here, so Guid.Empty is the parent sentinel for an entry-seed miss.
                if (!snapshot.Steps.ContainsKey(entryId))
                {
                    throw OrchestrationValidationException.MissingStep(Guid.Empty, entryId);
                }

                RunDfs(snapshot, entryId, fullyVisited);
            }
        }
    }

    /// <summary>
    /// Explicit-stack DFS from <paramref name="entryId"/>. <c>onStack</c> tracks the active path for
    /// cycle detection; <c>path</c> tracks the same nodes in order for offending-chain reconstruction;
    /// <paramref name="fullyVisited"/> records completed subtrees (shared across seeds).
    /// </summary>
    private static void RunDfs(WorkflowGraphSnapshot snapshot, Guid entryId, HashSet<Guid> fullyVisited)
    {
        var stack = new Stack<(Guid Step, IEnumerator<Guid> Children)>();
        var onStack = new HashSet<Guid>();
        var path = new List<Guid>();

        Push(snapshot, entryId, stack, onStack, path);

        while (stack.Count > 0)
        {
            var (currentStep, children) = stack.Peek();

            if (children.MoveNext())
            {
                var child = children.Current;

                if (!snapshot.Steps.ContainsKey(child))
                {
                    // D-08 missing-step gate — child referenced via NextStepIds but absent from the graph.
                    throw OrchestrationValidationException.MissingStep(currentStep, child);
                }

                if (onStack.Contains(child))
                {
                    // Back-edge → cycle. Reconstruct as the path slice from the first occurrence of
                    // `child` to the end, then close the loop by appending `child` again.
                    var startIndex = path.IndexOf(child);
                    var cycleChain = new List<Guid>(path.GetRange(startIndex, path.Count - startIndex)) { child };
                    throw OrchestrationValidationException.Cycle(cycleChain);
                }

                if (!fullyVisited.Contains(child))
                {
                    // Unvisited node — descend.
                    Push(snapshot, child, stack, onStack, path);
                }

                // Else: child is already fullyVisited → legal shared/fan-in subgraph. Skip (the
                // two-set discriminator — this is what prevents the diamond false-positive, D-14).
            }
            else
            {
                // Subtree exhausted — pop the frame and mark the step complete.
                stack.Pop();
                onStack.Remove(currentStep);
                path.RemoveAt(path.Count - 1);
                fullyVisited.Add(currentStep);
            }
        }
    }

    /// <summary>Pushes a new DFS frame for <paramref name="step"/> and records it on the active path.</summary>
    private static void Push(
        WorkflowGraphSnapshot snapshot,
        Guid step,
        Stack<(Guid Step, IEnumerator<Guid> Children)> stack,
        HashSet<Guid> onStack,
        List<Guid> path)
    {
        var children = (snapshot.Steps[step].NextStepIds ?? new List<Guid>()).GetEnumerator();
        stack.Push((step, children));
        onStack.Add(step);
        path.Add(step);
    }
}
