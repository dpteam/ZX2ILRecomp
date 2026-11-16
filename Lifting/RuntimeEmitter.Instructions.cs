using System.Globalization;
using ZX2ILRecomp.Core;

namespace ZX2ILRecomp.Lifting;

internal static partial class RuntimeEmitter
{
    /// <summary>
    /// Emits the body of one <c>Lxxxx()</c> method: either inlined arithmetic
    /// plus a literal next-address return, or a hand-off to the interpreter.
    /// </summary>
    private static void EmitInstruction(EmitWriter w, InstructionZ80 inst)
    {
        if (inst.Control == OpControlZ80.Invalid || !inst.Supported)
        {
            EmitDynamic(w, inst.Address);
            return;
        }

        switch (inst.Control)
        {
            case OpControlZ80.Branch:
                EmitBranch(w, inst);
                return;

            case OpControlZ80.Jmp:
                EmitJmp(w, inst);
                return;

            case OpControlZ80.JmpInd:
                EmitJmpInd(w, inst);
                return;

            case OpControlZ80.Call:
                EmitCall(w, inst);
                return;

            case OpControlZ80.Ret:
                EmitRet(w, inst);
                return;

            case OpControlZ80.Rst:
                EmitRst(w, inst);
                return;

            case OpControlZ80.Halt:
                w.Line("TStates += 4;");
                w.Line("Halt();");

                if (inst.HasFallthrough)
                {
                    EmitGoto(w, inst.Fallthrough);
                }
                else
                {
                    w.Line("Trap(\"HALT without a fall-through address.\");");
                    w.Line("return 0;");
                }

                return;
        }

        // DD/FD forms are executed by the interpreter: correct, just not inlined.
        if (inst.Prefix != 0 && inst.Prefix != 0xCB && inst.Prefix != 0xED)
        {
            EmitDynamic(w, inst.Address);
            return;
        }

        EmitNormalExec(w, inst);

        if (inst.HasFallthrough)
        {
            EmitGoto(w, inst.Fallthrough);
        }
        else
        {
            w.Line("Trap(\"No fall-through address.\");");
            w.Line("return 0;");
        }
    }

    private static void EmitNormalExec(EmitWriter w, InstructionZ80 inst)
    {
        if (inst.Prefix == 0)
        {
            w.Line("ExecBase(0x" + inst.Opcode.ToString("X2", CultureInfo.InvariantCulture) + ", 0x" +
                   inst.Operand.ToString("X4", CultureInfo.InvariantCulture) + ");");
        }
        else if (inst.Prefix == 0xCB)
        {
            w.Line("ExecCB(0x" + inst.Opcode.ToString("X2", CultureInfo.InvariantCulture) + ");");
        }
        else if (inst.Prefix == 0xED)
        {
            w.Line("ExecEDNormal(0x" + inst.Opcode.ToString("X2", CultureInfo.InvariantCulture) + ", 0x" +
                   inst.Operand.ToString("X4", CultureInfo.InvariantCulture) + ");");
        }
    }

    private static void EmitDynamic(EmitWriter w, ushort address)
    {
        string hex = address.ToString("X4", CultureInfo.InvariantCulture);
        w.Line("DispatchTarget = 0x" + hex + ";");
        w.Line("if (!ExecDynamicOne(DispatchTarget))");
        w.Line("{");
        w.Line("    Trap(\"Dynamic execution failed at $" + hex + "\");");
        w.Line("    return 0;");
        w.Line("}");
        w.Line("return DispatchTarget;");
    }

    private static void EmitBranch(EmitWriter w, InstructionZ80 inst)
    {
        w.Line("TStates += " + CpuZ80.BaseT[inst.Opcode].ToString(CultureInfo.InvariantCulture) + ";");

        string target = "0x" + inst.Target.ToString("X4", CultureInfo.InvariantCulture);
        string fallthrough = "0x" + inst.Fallthrough.ToString("X4", CultureInfo.InvariantCulture);

        if (inst.IsDJNZ)
        {
            w.Line("B = (byte)(B - 1);");
            w.Line("return (ushort)(B != 0 ? " + target + " : " + fallthrough + ");");
            return;
        }

        w.Line("return (ushort)(Conditional(" + inst.Condition.ToString(CultureInfo.InvariantCulture) + ") ? " +
               target + " : " + fallthrough + ");");
    }

    private static void EmitJmp(EmitWriter w, InstructionZ80 inst)
    {
        w.Line("TStates += " + CpuZ80.BaseT[inst.Opcode].ToString(CultureInfo.InvariantCulture) + ";");

        if (inst.Condition < 0)
        {
            EmitGoto(w, inst.Target);
            return;
        }

        w.Line("return (ushort)(Conditional(" + inst.Condition.ToString(CultureInfo.InvariantCulture) + ") ? 0x" +
               inst.Target.ToString("X4", CultureInfo.InvariantCulture) + " : 0x" +
               inst.Fallthrough.ToString("X4", CultureInfo.InvariantCulture) + ");");
    }

    private static void EmitJmpInd(EmitWriter w, InstructionZ80 inst)
    {
        w.Line("TStates += " + CpuZ80.BaseT[inst.Opcode].ToString(CultureInfo.InvariantCulture) + ";");
        w.Line("NoteDynamicTarget(HL);");
        w.Line("return HL;");
    }

    private static void EmitCall(EmitWriter w, InstructionZ80 inst)
    {
        w.Line("TStates += " + CpuZ80.BaseT[inst.Opcode].ToString(CultureInfo.InvariantCulture) + ";");

        string push = "Push16(0x" + inst.Fallthrough.ToString("X4", CultureInfo.InvariantCulture) + ");";

        if (inst.Condition < 0)
        {
            w.Line(push);
            EmitGoto(w, inst.Target);
            return;
        }

        w.Line("if (Conditional(" + inst.Condition.ToString(CultureInfo.InvariantCulture) + "))");
        w.Line("{");
        w.Line("    " + push);
        w.Line("    return 0x" + inst.Target.ToString("X4", CultureInfo.InvariantCulture) + ";");
        w.Line("}");
        w.Line("return 0x" + inst.Fallthrough.ToString("X4", CultureInfo.InvariantCulture) + ";");
    }

    private static void EmitRet(EmitWriter w, InstructionZ80 inst)
    {
        w.Line("TStates += " + CpuZ80.BaseT[inst.Opcode].ToString(CultureInfo.InvariantCulture) + ";");

        if (inst.Condition < 0)
        {
            w.Line("return Pop16();");
            return;
        }

        w.Line("if (Conditional(" + inst.Condition.ToString(CultureInfo.InvariantCulture) + ")) return Pop16();");
        EmitGoto(w, inst.Fallthrough);
    }

    private static void EmitRst(EmitWriter w, InstructionZ80 inst)
    {
        w.Line("TStates += " + CpuZ80.BaseT[inst.Opcode].ToString(CultureInfo.InvariantCulture) + ";");
        w.Line("Push16(0x" + ((ushort)(inst.Address + 1)).ToString("X4", CultureInfo.InvariantCulture) + ");");
        EmitGoto(w, inst.Target);
    }

    private static void EmitGoto(EmitWriter w, ushort address)
    {
        w.Line("return 0x" + address.ToString("X4", CultureInfo.InvariantCulture) + ";");
    }
}
