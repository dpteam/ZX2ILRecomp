using System.Globalization;
using ZX2ILRecomp.Core;

namespace ZX2ILRecomp.Lifting;

/// <summary>
/// Emits the generated game's <c>Runtime</c> class: registers, memory-resident
/// state, the interpreter fallback and the per-instruction dispatch loop.
/// </summary>
internal static partial class RuntimeEmitter
{
    public static void Emit(EmitWriter w, LifterContext ctx)
    {
        w.Open("static class Runtime");
        EmitTables(w, ctx);
        EmitFields(w, ctx);
        EmitInit(w, ctx);
        EmitRunLoop(w, ctx);
        EmitHelpers(w);
        EmitExecMethods(w);
        EmitDynamicExecutor(w);
        w.Close();
        w.Blank();
    }

    private static void EmitFields(EmitWriter w, LifterContext ctx)
    {
        w.Line("public static string GameName = \"" + EmitWriter.Escape(ctx.DisplayName) + "\";");
        w.Line("public static int Model = " + ctx.Snapshot.Model.ToString(CultureInfo.InvariantCulture) + ";");
        w.Blank();

        w.Line("// Main register file.");
        w.Line("public static byte A, F, B, C, D, E, H, L;");
        w.Line("// Alternate register file (EX AF,AF' / EXX).");
        w.Line("public static byte A2, F2, B2, C2, D2, E2, H2, L2;");
        w.Line("public static ushort IX, IY, SP, PC;");
        w.Line("public static byte I, R;");
        w.Line("public static bool IFF1, IFF2;");
        w.Line("public static int IM;");
        w.Line("public static bool Halted;");
        w.Blank();

        w.Line("// ULA / paging latches.");
        w.Line("public static volatile bool InterruptPending;");
        w.Line("public static long Frames;");
        w.Line("public static byte Border, Port7FFD, Port1FFD;");
        w.Line("public static bool PagingDisabled;");
        w.Line("public static volatile bool BeeperLevel;");
        w.Blank();

        w.Line("// Memory: ROM images plus the eight 16K RAM banks.");
        w.Line("public static byte[] Rom0 = new byte[16384];");
        w.Line("public static byte[] Rom1 = new byte[16384];");
        w.Line("public static byte[][] InitialRam = new byte[8][];");
        w.Line("public static byte[][] RamBanks = new byte[8][];");
        w.Blank();

        w.Line("// Keyboard matrix (one byte per half-row, 0 = pressed) and Kempston.");
        w.Line("public static byte[] KeyMatrix = new byte[8];");
        w.Line("public static volatile int LastKey = -1;");
        w.Line("public static volatile ushort LastKeyPort;");
        w.Line("public static volatile byte LastKeyRead;");
        w.Line("public static byte Kempston;");
        w.Line("public static byte[] Parity = new byte[256];");
        w.Blank();

        w.Line("// Execution counters and diagnostics.");
        w.Line("public static long InsCount;");
        w.Line("public static ushort LastPC;");
        w.Line("public static ushort DispatchTarget;");
        w.Line("public static string LastError;");
        w.Line("public static volatile string CpuState;");
        w.Line("public static volatile int TrapCount;");
        w.Line("public static string LastTrap;");
        w.Line("public static bool HeadlessMode;");
        w.Line("public static int FrameLimit;");
        w.Line("public static volatile bool ShowDebug;");
        w.Blank();

        w.Line("// Indirect targets the analysis could not see, collected for the next lift.");
        w.Line("public static object DynLock = new object();");
        w.Line("public static Dictionary<ushort, bool> SeenDynamic = new Dictionary<ushort, bool>();");
        w.Line("public static object FrameLock = new object();");
        w.Blank();

        w.Line("// Timing: 3.5 MHz, 69888 T-states per 50 Hz frame.");
        w.Line("public static long ClockStart;");
        w.Line("public static volatile bool ThrottleEnabled = true;");
        w.Line("public static long TStates;");
        w.Line("public static long NextIntT = " + CpuZ80.FrameTStates.ToString(CultureInfo.InvariantCulture) + ";");
        w.Line("public static double Speed = 1.0;");
        w.Blank();

        w.Line("[DllImport(\"winmm.dll\")] static extern int TimeBeginPeriod(int period);");
        w.Blank();

        w.Line("public static ushort BC { get { return (ushort)((B << 8) | C); } set { B = (byte)(value >> 8); C = (byte)value; } }");
        w.Line("public static ushort DE { get { return (ushort)((D << 8) | E); } set { D = (byte)(value >> 8); E = (byte)value; } }");
        w.Line("public static ushort HL { get { return (ushort)((H << 8) | L); } set { H = (byte)(value >> 8); L = (byte)value; } }");
        w.Line("public static ushort AF { get { return (ushort)((A << 8) | F); } set { A = (byte)(value >> 8); F = (byte)value; } }");
        w.Blank();
    }

    private static void EmitInit(EmitWriter w, LifterContext ctx)
    {
        ZxSnapshot s = ctx.Snapshot;

        w.Open("public static void Init()");
        w.Line("InitMemory();");
        w.Line("InitParity();");
        w.Line("A = 0x" + s.A.ToString("X2", CultureInfo.InvariantCulture) + "; F = 0x" + s.F.ToString("X2", CultureInfo.InvariantCulture) + ";");
        w.Line("B = 0x" + s.B.ToString("X2", CultureInfo.InvariantCulture) + "; C = 0x" + s.C.ToString("X2", CultureInfo.InvariantCulture) + ";");
        w.Line("D = 0x" + s.D.ToString("X2", CultureInfo.InvariantCulture) + "; E = 0x" + s.E.ToString("X2", CultureInfo.InvariantCulture) + ";");
        w.Line("H = 0x" + s.H.ToString("X2", CultureInfo.InvariantCulture) + "; L = 0x" + s.L.ToString("X2", CultureInfo.InvariantCulture) + ";");
        w.Line("A2 = 0x" + s.A2.ToString("X2", CultureInfo.InvariantCulture) + "; F2 = 0x" + s.F2.ToString("X2", CultureInfo.InvariantCulture) + ";");
        w.Line("B2 = 0x" + s.B2.ToString("X2", CultureInfo.InvariantCulture) + "; C2 = 0x" + s.C2.ToString("X2", CultureInfo.InvariantCulture) + ";");
        w.Line("D2 = 0x" + s.D2.ToString("X2", CultureInfo.InvariantCulture) + "; E2 = 0x" + s.E2.ToString("X2", CultureInfo.InvariantCulture) + ";");
        w.Line("H2 = 0x" + s.H2.ToString("X2", CultureInfo.InvariantCulture) + "; L2 = 0x" + s.L2.ToString("X2", CultureInfo.InvariantCulture) + ";");
        w.Line("IX = 0x" + s.IX.ToString("X4", CultureInfo.InvariantCulture) + "; IY = 0x" + s.IY.ToString("X4", CultureInfo.InvariantCulture) + ";");
        w.Line("SP = 0x" + s.SP.ToString("X4", CultureInfo.InvariantCulture) + "; PC = 0x" + s.PC.ToString("X4", CultureInfo.InvariantCulture) + ";");
        w.Line("I = 0x" + s.I.ToString("X2", CultureInfo.InvariantCulture) + "; R = 0x" + s.R.ToString("X2", CultureInfo.InvariantCulture) + ";");
        w.Line("IFF1 = " + Bool(s.IFF1) + "; IFF2 = " + Bool(s.IFF2) + ";");
        w.Line("IM = " + s.IM.ToString(CultureInfo.InvariantCulture) + ";");
        w.Blank();
        w.Line("// A snapshot taken inside a DI'd routine would never receive a frame");
        w.Line("// interrupt again, so interrupts are re-armed on entry.");
        w.Line("if (!IFF1) { IFF1 = true; IFF2 = true; }");
        w.Line("if (IM == 0) IM = 1;");
        w.Blank();
        w.Line("Border = 0x" + s.Border.ToString("X2", CultureInfo.InvariantCulture) + ";");
        w.Line("Port7FFD = 0x" + s.Port7FFD.ToString("X2", CultureInfo.InvariantCulture) + ";");
        w.Line("Port1FFD = 0x" + s.Port1FFD.ToString("X2", CultureInfo.InvariantCulture) + ";");
        w.Line("DispatchTarget = PC;");
        w.Line("CpuState = \"Init\";");
        w.Line("Frames = 0;");
        w.Line("for (int i = 0; i < 8; i++) KeyMatrix[i] = 0xFF;");
        w.Line("Memory.Reset();");
        w.Line("Ula.Reset();");
        w.Line("Audio.TryStart();");
        w.Line("try { TimeBeginPeriod(1); } catch { }");
        w.Line("ClockStart = System.Diagnostics.Stopwatch.GetTimestamp();");
        w.Line("NextIntT = " + CpuZ80.FrameTStates.ToString(CultureInfo.InvariantCulture) + ";");
        w.Close();
        w.Blank();

        w.Open("public static void InitParity()");
        w.Open("for (int i = 0; i < 256; i++)");
        w.Line("int bits = 0;");
        w.Line("for (int b = 0; b < 8; b++) if ((i & (1 << b)) != 0) bits++;");
        w.Line("Parity[i] = (byte)((bits % 2 == 0) ? 4 : 0);");
        w.Close();
        w.Close();
        w.Blank();

        w.Open("public static void CpuThread()");
        w.Open("try");
        w.Line("CpuState = \"Starting\";");
        w.Line("Reset();");
        w.Line("CpuState = \"Running\";");
        w.Line("Run();");
        w.Line("CpuState = LastError != null ? \"Trap\" : \"Exited\";");
        w.Close();
        w.Open("catch (Exception ex)");
        w.Line("CpuState = \"Exception\";");
        w.Line("OnCpuException(ex);");
        w.Close();
        w.Close();
        w.Blank();

        w.Open("public static void Reset()");
        w.Line("LastError = null; LastTrap = null; InsCount = 0; LastPC = 0; TrapCount = 0;");
        w.Line("DispatchTarget = PC; CpuState = \"Reset\";");
        w.Line("TStates = 0; NextIntT = " + CpuZ80.FrameTStates.ToString(CultureInfo.InvariantCulture) + ";");
        w.Line("ClockStart = System.Diagnostics.Stopwatch.GetTimestamp();");
        w.Line("if (SeenDynamic != null) SeenDynamic.Clear();");
        w.Line("Memory.Reset();");
        w.Close();
        w.Blank();
    }

    private static string Bool(bool value) => value ? "true" : "false";

    private static void EmitRunLoop(EmitWriter w, LifterContext ctx)
    {
        w.Open("public static void Run()");
        w.Open("try");
        w.Line("CpuState = \"Running\";");
        w.Line("ushort pc = 0x" + ctx.Entry.ToString("X4", CultureInfo.InvariantCulture) + ";");
        w.Blank();

        if (!ctx.HasEntry)
        {
            w.Line("Trap(\"No valid entry point in the snapshot.\");");
            w.Line("return;");
            w.Close();
            w.Close();
            w.Blank();
            return;
        }

        w.Open("while (true)");
        w.Line("if (FrameLimit > 0 && Frames >= FrameLimit) { CpuState = \"Done\"; return; }");
        w.Line("LastPC = pc;");
        w.Line("InsCount++;");
        w.Line("if (CheckInterrupt(pc)) pc = DispatchTarget;");
        w.Line("else pc = StepStatic(pc);");
        w.Close();
        w.Close();
        w.Open("catch (Exception ex)");
        w.Line("OnCpuException(ex);");
        w.Close();
        w.Close();
        w.Blank();

        // Group labels per 256-byte page so the dispatch switch stays small.
        SortedDictionary<int, List<ushort>> pages = new();

        foreach (ushort address in ctx.Labels)
        {
            int page = address >> 8;

            if (!pages.TryGetValue(page, out List<ushort>? list))
            {
                list = new List<ushort>();
                pages[page] = list;
            }

            list.Add(address);
        }

        w.Open("static ushort StepStatic(ushort pc)");
        w.Open("switch (pc >> 8)");

        foreach (int page in pages.Keys)
        {
            w.Line("case 0x" + page.ToString("X2", CultureInfo.InvariantCulture) + ": return StepPage" + page.ToString("X2", CultureInfo.InvariantCulture) + "(pc);");
        }

        w.Line("default: return StepDynamic(pc);");
        w.Close();
        w.Close();
        w.Blank();

        w.Open("static ushort StepDynamic(ushort pc)");
        w.Line("DispatchTarget = pc;");
        w.Line("if (!ExecDynamicOne(DispatchTarget))");
        w.Line("{");
        w.Line("    Trap(\"Dynamic execution failed at $\" + pc.ToString(\"X4\"));");
        w.Line("    return 0;");
        w.Line("}");
        w.Line("return DispatchTarget;");
        w.Close();
        w.Blank();

        foreach (KeyValuePair<int, List<ushort>> page in pages)
        {
            string suffix = page.Key.ToString("X2", CultureInfo.InvariantCulture);
            w.Open("static ushort StepPage" + suffix + "(ushort pc)");
            w.Open("switch (pc)");

            foreach (ushort address in page.Value)
            {
                w.Line("case 0x" + address.ToString("X4", CultureInfo.InvariantCulture) + ": return L" +
                       address.ToString("X4", CultureInfo.InvariantCulture) + "();");
            }

            w.Line("default: return StepDynamic(pc);");
            w.Close();
            w.Close();
            w.Blank();
        }

        foreach (ushort address in ctx.Labels)
        {
            w.Open("static ushort L" + address.ToString("X4", CultureInfo.InvariantCulture) + "()");

            if (ctx.Model.Instructions.TryGetValue(address, out InstructionZ80? inst))
            {
                w.Line("// " + inst.Text);
                EmitInstruction(w, inst);
            }
            else
            {
                w.Line("Trap(\"Label without a decoded instruction.\");");
                w.Line("return 0;");
            }

            w.Close();
            w.Blank();
        }
    }
}
