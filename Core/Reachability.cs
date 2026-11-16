namespace ZX2ILRecomp.Core;

/// <summary>
/// Decides which decoded addresses deserve their own generated method.
///
/// The linear sweep is deliberately greedy: it decodes everything in RAM that
/// recursive descent did not reach, because games driven by jump tables keep
/// real code in blocks the descent cannot prove reachable.
///
/// The cost of that greed is measured, not guessed. On Arkanoid the sweep adds
/// 46 000 instructions but only 131 basic blocks, and the game executes the
/// exact same 4 335 630 instructions over 600 frames either way — so the sweep
/// contributed nothing but 3 MB of C# and seven seconds of compilation.
///
/// This is why reachability is computed as a proper fixed point over *real*
/// control flow:
///
/// * start from the roots — entry point, interrupt vectors, forced addresses;
/// * follow fall-through and jump/call targets of instructions that are already
///   known reachable;
/// * repeat until nothing new is found.
///
/// The subtle part is that a target only counts when the instruction holding it
/// is itself reachable. Visiting the targets of *every* decoded instruction —
/// the obvious implementation — makes almost everything reachable, because
/// swept garbage contains jumps pointing all over RAM, and the filter then
/// prunes nothing.
///
/// What the sweep is still good for: an address it decoded can be promoted by a
/// decoded jump from reachable code, and a genuinely computed target is covered
/// by the interpreter and reported through <c>dynamic_targets.log</c>, which
/// feeds the next lift.
/// </summary>
public static class Reachability
{
    /// <summary>
    /// Returns the addresses that should get a generated method, ascending.
    /// </summary>
    public static List<ushort> Compute(AnalysisResultZ80 model, IReadOnlyCollection<ushort> roots)
    {
        HashSet<ushort> reachable = new();
        Queue<ushort> pending = new();

        void Visit(ushort address)
        {
            if (model.Instructions.ContainsKey(address) && reachable.Add(address))
            {
                pending.Enqueue(address);
            }
        }

        foreach (ushort root in roots)
        {
            Visit(root);
        }

        while (pending.Count > 0)
        {
            ushort address = pending.Dequeue();

            if (!model.Instructions.TryGetValue(address, out InstructionZ80? inst))
            {
                continue;
            }

            if (inst.HasFallthrough)
            {
                Visit(inst.Fallthrough);
            }

            if (inst.HasTarget)
            {
                Visit(inst.Target);
            }
        }

        List<ushort> labels = new(reachable);
        labels.Sort();
        return labels;
    }

    /// <summary>How many decoded instructions were dropped, for the report.</summary>
    public static int CountDropped(AnalysisResultZ80 model, IReadOnlyCollection<ushort> emitted)
    {
        return Math.Max(0, model.Instructions.Count - emitted.Count);
    }
}
