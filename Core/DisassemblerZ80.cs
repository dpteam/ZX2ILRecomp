namespace ZX2ILRecomp.Core;

/// <summary>
/// Recursive-descent Z80 analyser with a linear-sweep safety net.
///
/// Two passes, on purpose:
///
/// 1. <b>Recursive descent</b> from the entry point, the RST/IM 1 vectors and
///    any user-forced addresses. This is the only pass that understands
///    control flow: it follows calls, jumps and fall-throughs and stops at
///    <c>RET</c> / <c>JP</c> / <c>HALT</c>.
///
/// 2. <b>Linear sweep</b> over everything the descent did not reach, skipping
///    the bitmap (0x4000-0x5AFF) and anything already covered. Games driven by
///    jump tables — Elite, most arcade ports — keep most of their code in
///    blocks the descent cannot prove reachable. Sweeping them turns a
///    "half the game is interpreted" lift into a fully static one.
///
/// Anything the sweep finds is decoded but not trusted as a function entry;
/// the runtime still logs real indirect targets through <c>dynamic_targets.log</c>
/// so the next lift can promote them to first-class labels.
/// </summary>
public sealed class DisassemblerZ80
{
    private const int MaxInstructions = 200_000;

    /// <summary>First byte after the bitmap + attribute area on a 48K machine.</summary>
    private const int FirstFreeRam = 0x5B00;

    private readonly ZxSnapshot _snapshot;
    private readonly byte[] _flat;
    private readonly Queue<ushort> _queue = new();
    private readonly HashSet<ushort> _seen = new();
    private AnalysisResultZ80 _result = new();

    public DisassemblerZ80(ZxSnapshot snapshot)
    {
        _snapshot = snapshot;
        _flat = snapshot.GetFlat();
    }

    /// <summary>Addresses forced into the analysis from <c>dynamic_targets.txt</c>.</summary>
    public List<ushort> ForcedAddresses { get; } = new();

    /// <summary>Set to false to skip the linear sweep (faster, less complete).</summary>
    public bool LinearSweep { get; set; } = true;

    /// <summary>Flat 64K view the analyser decoded from; useful for reports.</summary>
    public byte[] Flat => _flat;

    public AnalysisResultZ80 Analyze()
    {
        _result = new AnalysisResultZ80();
        _queue.Clear();
        _seen.Clear();

        ushort entry = _snapshot.Entry;

        if (entry == 0)
        {
            entry = 0x8000;
        }

        _result.Entry = entry;
        Enqueue(entry);
        _result.Functions.Add(entry);

        if (_snapshot.HasRomCode)
        {
            // RST vectors and the IM 1 handler. Without these the very first
            // interrupt lands on an address with no generated method.
            Enqueue(0x0000);
            Enqueue(0x0008);
            Enqueue(0x0010);
            Enqueue(0x0018);
            Enqueue(0x0020);
            Enqueue(0x0028);
            Enqueue(0x0030);
            Enqueue(0x0038);
        }

        foreach (ushort forced in ForcedAddresses)
        {
            Enqueue(forced);

            if (!_result.Functions.Contains(forced))
            {
                _result.Functions.Add(forced);
            }

            if (!_result.DynamicTargets.Contains(forced))
            {
                _result.DynamicTargets.Add(forced);
            }
        }

        DrainQueue();

        if (LinearSweep)
        {
            Sweep();
            DrainQueue();
        }

        _result.Labels = new List<ushort>(_result.Instructions.Keys);
        _result.Labels.Sort();
        _result.Swept = LinearSweep;

        if (_result.Instructions.Count >= MaxInstructions)
        {
            Log.Warn("Instruction limit reached during disassembly (" + MaxInstructions + ").");
        }

        return _result;
    }

    private void DrainQueue()
    {
        while (_queue.Count > 0 && _result.Instructions.Count < MaxInstructions)
        {
            ushort address = _queue.Dequeue();

            if (_result.Instructions.ContainsKey(address))
            {
                continue;
            }

            InstructionZ80 inst = Decode(address);
            _result.Instructions.Add(address, inst);
            Record(inst);
        }
    }

    private void Record(InstructionZ80 inst)
    {
        if (inst.Control == OpControlZ80.Invalid && !_result.UnknownOpcodes.Contains(inst.Opcode))
        {
            _result.UnknownOpcodes.Add(inst.Opcode);
        }

        if (inst.Control == OpControlZ80.JmpInd && !_result.IndirectJumps.Contains(inst.Address))
        {
            _result.IndirectJumps.Add(inst.Address);
        }

        if (inst.Control is OpControlZ80.Call or OpControlZ80.Jmp && inst.HasTarget)
        {
            if (!_result.Functions.Contains(inst.Target))
            {
                _result.Functions.Add(inst.Target);
            }
        }

        if (inst.HasTarget)
        {
            Enqueue(inst.Target);
        }

        if (inst.HasFallthrough)
        {
            Enqueue(inst.Fallthrough);
        }
    }

    private void Sweep()
    {
        bool[] covered = new bool[65536];

        foreach (KeyValuePair<ushort, InstructionZ80> pair in _result.Instructions)
        {
            int start = pair.Key;
            int length = pair.Value.Length;

            for (int i = 0; i < length && start + i < 65536; i++)
            {
                covered[start + i] = true;
            }
        }

        for (int start = FirstFreeRam; start < 0x10000; start++)
        {
            if (covered[start])
            {
                continue;
            }

            // Runs of identical filler are data or padding, not code. Decoding
            // them as thousands of NOPs (or 0xFF "RST 38") would bloat the
            // generated file without adding a single reachable label.
            if (_flat[start] is 0x00 or 0xFF)
            {
                int run = 1;

                while (start + run < 0x10000 && _flat[start + run] == _flat[start])
                {
                    run++;
                }

                if (run >= 16)
                {
                    start += run - 1;
                    continue;
                }
            }

            ushort address = (ushort)start;
            InstructionZ80 inst = Decode(address);

            if (!_result.Instructions.ContainsKey(address))
            {
                _result.Instructions.Add(address, inst);

                if (inst.Control == OpControlZ80.Invalid && !_result.UnknownOpcodes.Contains(inst.Opcode))
                {
                    _result.UnknownOpcodes.Add(inst.Opcode);
                }
            }

            for (int i = 0; i < inst.Length && address + i < 65536; i++)
            {
                covered[address + i] = true;
            }

            if (inst.HasTarget)
            {
                Enqueue(inst.Target);
            }

            if (inst.HasFallthrough)
            {
                Enqueue(inst.Fallthrough);
            }
        }
    }

    private void Enqueue(ushort address)
    {
        if (!_seen.Add(address))
        {
            return;
        }

        _queue.Enqueue(address);
    }

    private byte Read8(ushort address) => _flat[address];

    /// <summary>Decodes one instruction at <paramref name="address"/>.</summary>
    public InstructionZ80 Decode(ushort address)
    {
        InstructionZ80 inst = new()
        {
            Address = address
        };

        byte b0 = Read8(address);
        inst.Opcode = b0;

        if (b0 == 0xCB)
        {
            inst.Prefix = 0xCB;
            inst.Length = 2;
            inst.Opcode = Read8((ushort)(address + 1));
            inst.Control = OpControlZ80.Normal;
            inst.HasFallthrough = true;
            inst.Fallthrough = (ushort)(address + 2);
            inst.Supported = true;
        }
        else if (b0 == 0xED)
        {
            inst.Prefix = 0xED;
            byte op2 = Read8((ushort)(address + 1));
            inst.Opcode = op2;
            inst.Length = CpuZ80.EDLen[op2];

            if (inst.Length == 4)
            {
                inst.Operand = (ushort)(Read8((ushort)(address + 2)) | (Read8((ushort)(address + 3)) << 8));
            }

            // RETN/RETI end a subroutine; every other ED form falls through.
            if ((op2 & 0xC7) == 0x45)
            {
                inst.Control = OpControlZ80.Ret;
                inst.HasFallthrough = false;
            }
            else
            {
                inst.Control = OpControlZ80.Normal;
                inst.HasFallthrough = true;
                inst.Fallthrough = (ushort)(address + inst.Length);
            }

            inst.Supported = true;
        }
        else if (b0 == 0xDD || b0 == 0xFD)
        {
            inst.Prefix = b0;
            byte op2 = Read8((ushort)(address + 1));
            inst.Opcode = op2;

            if (op2 == 0xCB)
            {
                inst.Prefix = b0 == 0xDD ? 0xDDCB : 0xFDCB;
                inst.Length = 4;
                inst.Displacement = (sbyte)Read8((ushort)(address + 2));
                inst.Opcode = Read8((ushort)(address + 3));
                inst.Control = OpControlZ80.Normal;
                inst.HasFallthrough = true;
                inst.Fallthrough = (ushort)(address + 4);

                // Handled by the interpreter: correct but not inlined.
                inst.Supported = false;
            }
            else
            {
                inst.Length = PrefixedLength(op2);
                ParsePrefixedOperand(inst, address);

                if (op2 == 0xE9)
                {
                    inst.Control = OpControlZ80.JmpInd;
                    inst.HasFallthrough = false;
                }
                else
                {
                    inst.Control = OpControlZ80.Normal;
                    inst.HasFallthrough = true;
                    inst.Fallthrough = (ushort)(address + inst.Length);
                }

                inst.Supported = false;
            }
        }
        else
        {
            inst.Length = CpuZ80.BaseLen[b0];

            if (inst.Length >= 2)
            {
                inst.Operand = Read8((ushort)(address + 1));
            }

            if (inst.Length == 3)
            {
                inst.Operand |= (ushort)(Read8((ushort)(address + 2)) << 8);
            }

            SetBaseControl(inst, address);
            inst.Supported = true;
        }

        inst.Text = Z80Text.Format(inst, _flat);
        return inst;
    }

    private int PrefixedLength(byte op)
    {
        if (op == 0xCB)
        {
            return 4;
        }

        // A doubled prefix is consumed one byte at a time, like real silicon.
        if (op is 0xDD or 0xFD or 0xED)
        {
            return 1;
        }

        if (CpuZ80.UsesIXYDisp[op] != 0)
        {
            return op == 0x36 ? 4 : 3;
        }

        return CpuZ80.BaseLen[op] + 1;
    }

    private void ParsePrefixedOperand(InstructionZ80 inst, ushort address)
    {
        byte op2 = inst.Opcode;

        if (CpuZ80.UsesIXYDisp[op2] != 0)
        {
            inst.Displacement = (sbyte)Read8((ushort)(address + 2));

            if (inst.Length == 4)
            {
                inst.Operand = Read8((ushort)(address + 3));
            }

            return;
        }

        if (inst.Length >= 3)
        {
            if (CpuZ80.BaseLen[op2] == 3 || op2 is 0x21 or 0x22 or 0x2A or 0x2B)
            {
                inst.Operand = (ushort)(Read8((ushort)(address + 2)) | (Read8((ushort)(address + 3)) << 8));
            }
            else
            {
                inst.Operand = Read8((ushort)(address + 2));
            }
        }
    }

    private void SetBaseControl(InstructionZ80 inst, ushort address)
    {
        byte op = inst.Opcode;
        ushort operand = inst.Operand;
        int length = inst.Length;

        inst.Control = OpControlZ80.Normal;
        inst.HasFallthrough = true;
        inst.Fallthrough = (ushort)(address + length);

        switch (op)
        {
            case 0xC3:
                inst.Control = OpControlZ80.Jmp;
                inst.Target = operand;
                inst.HasTarget = true;
                inst.HasFallthrough = false;
                break;

            case 0xC2:
            case 0xCA:
            case 0xD2:
            case 0xDA:
            case 0xE2:
            case 0xEA:
            case 0xF2:
            case 0xFA:
                inst.Control = OpControlZ80.Jmp;
                inst.Condition = (op >> 3) & 7;
                inst.Target = operand;
                inst.HasTarget = true;
                inst.HasFallthrough = true;
                inst.Fallthrough = (ushort)(address + 3);
                break;

            case 0x18:
                inst.Control = OpControlZ80.Branch;
                inst.Target = (ushort)(address + 2 + (sbyte)operand);
                inst.HasTarget = true;
                inst.HasFallthrough = true;
                inst.Fallthrough = (ushort)(address + 2);
                break;

            case 0x10:
                inst.Control = OpControlZ80.Branch;
                inst.IsDJNZ = true;
                inst.Target = (ushort)(address + 2 + (sbyte)operand);
                inst.HasTarget = true;
                inst.HasFallthrough = true;
                inst.Fallthrough = (ushort)(address + 2);
                break;

            case 0x20:
            case 0x28:
            case 0x30:
            case 0x38:
                inst.Control = OpControlZ80.Branch;
                inst.Condition = (op >> 3) & 3;
                inst.Target = (ushort)(address + 2 + (sbyte)operand);
                inst.HasTarget = true;
                inst.HasFallthrough = true;
                inst.Fallthrough = (ushort)(address + 2);
                break;

            case 0xCD:
                inst.Control = OpControlZ80.Call;
                inst.Target = operand;
                inst.HasTarget = true;
                inst.HasFallthrough = true;
                inst.Fallthrough = (ushort)(address + 3);
                break;

            case 0xC4:
            case 0xCC:
            case 0xD4:
            case 0xDC:
            case 0xE4:
            case 0xEC:
            case 0xF4:
            case 0xFC:
                inst.Control = OpControlZ80.Call;
                inst.Condition = (op >> 3) & 7;
                inst.Target = operand;
                inst.HasTarget = true;
                inst.HasFallthrough = true;
                inst.Fallthrough = (ushort)(address + 3);
                break;

            case 0xC9:
                inst.Control = OpControlZ80.Ret;
                inst.HasFallthrough = false;
                break;

            case 0xC0:
            case 0xC8:
            case 0xD0:
            case 0xD8:
            case 0xE0:
            case 0xE8:
            case 0xF0:
            case 0xF8:
                inst.Control = OpControlZ80.Ret;
                inst.Condition = (op >> 3) & 7;
                inst.HasFallthrough = true;
                inst.Fallthrough = (ushort)(address + 1);
                break;

            case 0xC7:
            case 0xCF:
            case 0xD7:
            case 0xDF:
            case 0xE7:
            case 0xEF:
            case 0xF7:
            case 0xFF:
                inst.Control = OpControlZ80.Rst;
                inst.Target = (ushort)(op & 0x38);
                inst.HasTarget = true;
                inst.HasFallthrough = false;
                break;

            case 0xE9:
                inst.Control = OpControlZ80.JmpInd;
                inst.HasFallthrough = false;
                break;

            case 0x76:
                inst.Control = OpControlZ80.Halt;
                inst.HasFallthrough = true;
                inst.Fallthrough = (ushort)(address + 1);
                break;
        }
    }
}
