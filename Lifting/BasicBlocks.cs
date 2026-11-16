using ZX2ILRecomp.Core;

namespace ZX2ILRecomp.Lifting;

/// <summary>
/// Groups emitted instructions into basic blocks.
///
/// One method per instruction is the obvious design and it is what the first
/// version did. Measured on a 48K game it produced 37 000 methods and 7 MB of
/// C#, and the C# compiler became the bottleneck: 0.3 s to analyse, 2 minutes to
/// compile. The methods are also pure overhead at run time, because a straight
/// run of twenty instructions became twenty dispatch round-trips.
///
/// A basic block is a maximal run of instructions entered only at its first
/// instruction and left only at its last. Emitting one method per block fixes
/// both problems at once: fewer methods to compile, fewer calls to execute.
///
/// A block starts when an address is:
/// * the entry point,
/// * the target of any jump, call or RST,
/// * or not the fall-through of the previous instruction.
///
/// Everything else continues the current block.
/// </summary>
public static class BasicBlocks
{
    /// <summary>One generated method: a run of consecutive instructions.</summary>
    public sealed record Block(ushort Start, IReadOnlyList<ushort> Addresses);

    /// <summary>
    /// Partitions <paramref name="labels"/> (ascending) into blocks.
    /// </summary>
    public static List<Block> Build(AnalysisResultZ80 model, IReadOnlyList<ushort> labels, ushort entry)
    {
        // Anything control can jump to must be able to start a block, otherwise
        // the dispatch table would have no method to call for it.
        HashSet<ushort> leaders = new() { entry };

        foreach (InstructionZ80 inst in model.Instructions.Values)
        {
            if (inst.HasTarget)
            {
                leaders.Add(inst.Target);
            }
        }

        List<Block> blocks = new();
        List<ushort> current = new();
        ushort currentStart = 0;
        ushort previousAddress = 0;
        bool havePrevious = false;

        foreach (ushort address in labels)
        {
            bool startNew;

            if (current.Count == 0)
            {
                startNew = true;
            }
            else if (leaders.Contains(address))
            {
                // Somebody can enter here directly; it needs its own method.
                startNew = true;
            }
            else if (!havePrevious)
            {
                startNew = true;
            }
            else if (!model.Instructions.TryGetValue(previousAddress, out InstructionZ80? previous))
            {
                startNew = true;
            }
            else
            {
                // The previous instruction must actually fall through to this
                // one, must not transfer control away, and must not be one that
                // can return to the next address.
                startNew = !previous.HasFallthrough || previous.Fallthrough != address;

                // CALL and RST push a return address. If the next instruction
                // were interior to this block, that return would land in the
                // middle of it with no dispatch entry — so the block ends here.
                if (previous.Control is OpControlZ80.Call or OpControlZ80.Rst)
                {
                    startNew = true;
                }

                // An unsupported instruction hands off to the interpreter, which
                // returns a computed address; nothing can follow it inline.
                if (!previous.Supported)
                {
                    startNew = true;
                }
            }

            if (startNew)
            {
                if (current.Count > 0)
                {
                    blocks.Add(new Block(currentStart, current.ToArray()));
                }

                current = new List<ushort>();
                currentStart = address;
            }

            current.Add(address);
            previousAddress = address;
            havePrevious = true;
        }

        if (current.Count > 0)
        {
            blocks.Add(new Block(currentStart, current.ToArray()));
        }

        return blocks;
    }

    /// <summary>Maps every emitted address to the block that contains it.</summary>
    public static Dictionary<ushort, ushort> MapStarts(IReadOnlyList<Block> blocks)
    {
        Dictionary<ushort, ushort> starts = new();

        foreach (Block block in blocks)
        {
            foreach (ushort address in block.Addresses)
            {
                starts[address] = block.Start;
            }
        }

        return starts;
    }
}
