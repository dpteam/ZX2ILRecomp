using System.Globalization;
using ZX2ILRecomp.Core;

namespace ZX2ILRecomp.Lifting;

/// <summary>
/// Emits the 64K address decoder.
///
/// Reads and writes are pure functions of the paging latches, exactly as on the
/// real machine: no bank pointer is cached, because a game can flip 0x7FFD in
/// the middle of a routine that is executing from the bank it is replacing.
/// </summary>
internal static class MemoryEmitter
{
    public static void Emit(EmitWriter w, LifterContext ctx)
    {
        w.Open("static class Memory");

        w.Open("public static void Reset()");
        w.Open("for (int i = 0; i < 8; i++)");
        w.Line("if (Runtime.InitialRam[i] != null) Runtime.RamBanks[i] = (byte[])Runtime.InitialRam[i].Clone();");
        w.Line("else Runtime.RamBanks[i] = new byte[16384];");
        w.Close();
        w.Close();
        w.Blank();

        w.Open("public static byte Read(ushort address)");
        w.Line("if (address < 0x4000)");
        w.Line("{");
        w.Line("    if (Runtime.Model >= 128 && (Runtime.Port7FFD & 0x10) != 0) return Runtime.Rom1[address];");
        w.Line("    return Runtime.Rom0[address];");
        w.Line("}");
        w.Line("if (address < 0x8000) return Runtime.RamBanks[5][address - 0x4000];");
        w.Line("if (address < 0xC000) return Runtime.RamBanks[2][address - 0x8000];");
        w.Line("int bank = (Runtime.Model >= 128) ? (Runtime.Port7FFD & 7) : 0;");
        w.Line("return Runtime.RamBanks[bank][address - 0xC000];");
        w.Close();
        w.Blank();

        w.Open("public static void Write(ushort address, byte value)");
        w.Line("if (address < 0x4000) return;   // ROM is not writable");
        w.Line("if (address < 0x8000) { Runtime.RamBanks[5][address - 0x4000] = value; return; }");
        w.Line("if (address < 0xC000) { Runtime.RamBanks[2][address - 0x8000] = value; return; }");
        w.Line("int bank = (Runtime.Model >= 128) ? (Runtime.Port7FFD & 7) : 0;");
        w.Line("Runtime.RamBanks[bank][address - 0xC000] = value;");
        w.Close();
        w.Blank();

        w.Open("public static ushort Read16(ushort address)");
        w.Line("return (ushort)(Read(address) | (Read((ushort)(address + 1)) << 8));");
        w.Close();
        w.Blank();

        w.Open("public static void Write16(ushort address, ushort value)");
        w.Line("Write(address, (byte)(value & 0xFF));");
        w.Line("Write((ushort)(address + 1), (byte)(value >> 8));");
        w.Close();
        w.Blank();

        w.Open("public static byte ReadRaw(int bank, int offset)");
        w.Line("return Runtime.RamBanks[bank & 7][offset & 0x3FFF];");
        w.Close();
        w.Blank();

        w.Close();
        w.Blank();
    }
}

/// <summary>Emits the AY-3-8912 register file and selector latch.</summary>
internal static class AyEmitter
{
    public static void Emit(EmitWriter w, LifterContext ctx)
    {
        w.Open("static class Ayu");
        w.Line("public static byte Sel;");
        w.Line("public static byte[] Regs = new byte[16];");
        w.Line("public const int ClockHz = 1773400;");
        w.Blank();
        w.Line("// AY envelope state; only the tone channels are mixed, but games read");
        w.Line("// the registers back, so writes must be stored verbatim.");
        w.Line("public static int EnvCounter;");
        w.Line("public static int EnvStep;");
        w.Blank();
        w.Open("public static void Reset()");
        w.Line("Sel = 0;");
        w.Line("for (int i = 0; i < Regs.Length; i++) Regs[i] = 0;");
        w.Line("EnvCounter = 0; EnvStep = 0;");
        w.Close();
        w.Close();
        w.Blank();
    }
}
