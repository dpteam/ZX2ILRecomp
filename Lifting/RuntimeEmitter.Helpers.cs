namespace ZX2ILRecomp.Lifting;

internal static partial class RuntimeEmitter
{
    private static void EmitHelpers(EmitWriter w)
    {
        EmitTrapHelpers(w);
        EmitDynamicTargetHelpers(w);
        EmitRegisterHelpers(w);
        EmitAluHelpers(w);
        EmitTimingHelpers(w);
        EmitPortHelpers(w);
    }

    private static void EmitTrapHelpers(EmitWriter w)
    {
        w.Open("public sealed class TrapException : Exception");
        w.Open("public TrapException(string message) : base(message)");
        w.Close();
        w.Close();
        w.Blank();

        w.Open("public static void SetTrap(string message)");
        w.Line("LastError = message;");
        w.Line("CpuState = \"Trap\";");
        w.Open("if (LastTrap != message)");
        w.Line("LastTrap = message;");
        w.Line("TrapCount++;");
        w.Line("Console.WriteLine(message);");
        w.Line("try { File.AppendAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, \"cpu_trap.log\"), DateTime.Now.ToString(\"s\") + \" \" + message + Environment.NewLine); } catch { }");
        w.Close();
        w.Line("throw new TrapException(message);");
        w.Close();
        w.Blank();

        w.Line("public static void Trap(string message) { SetTrap(message); }");
        w.Blank();

        w.Line("public static void UnknownOpcode(byte op, ushort address) { SetTrap(string.Format(\"Unknown opcode ${0:X2} at ${1:X4}\", op, address)); }");
        w.Blank();

        w.Open("public static void OnCpuException(Exception ex)");
        w.Line("bool isTrap = ex is TrapException;");
        w.Line("LastError = isTrap ? ex.Message : \"CPU exception: \" + ex;");
        w.Line("CpuState = \"Trap\";");
        w.Open("if (!isTrap)");
        w.Line("TrapCount++;");
        w.Line("Console.WriteLine(LastError);");
        w.Close();
        w.Close();
        w.Blank();
    }

    private static void EmitDynamicTargetHelpers(EmitWriter w)
    {
        w.Open("public static void ReportDynamicTarget(ushort address)");
        w.Open("lock (DynLock)");
        w.Open("if (!SeenDynamic.ContainsKey(address))");
        w.Line("SeenDynamic.Add(address, true);");
        w.Line("try { File.AppendAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, \"dynamic_targets.log\"), address.ToString(\"X4\") + Environment.NewLine); } catch { }");
        w.Close();
        w.Close();
        w.Close();
        w.Blank();

        w.Line("static ushort _lastLoggedTarget;");
        w.Line("static bool _haveLastLogged;");
        w.Blank();

        w.Open("public static void NoteDynamicTarget(ushort address)");
        w.Line("// ROM-space targets are vectors, not game code.");
        w.Line("if (address < 0x4000) return;");
        w.Line("// A target that is exactly one past the previous one is linear fallout,");
        w.Line("// not a jump: logging it would poison the next lift with false labels.");
        w.Line("if (_haveLastLogged && address == (ushort)(_lastLoggedTarget + 1)) return;");
        w.Open("lock (DynLock)");
        w.Line("if (SeenDynamic.ContainsKey(address)) return;");
        w.Line("SeenDynamic.Add(address, true);");
        w.Line("_lastLoggedTarget = address;");
        w.Line("_haveLastLogged = true;");
        w.Line("try { File.AppendAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, \"dynamic_targets.log\"), address.ToString(\"X4\") + Environment.NewLine); } catch { }");
        w.Close();
        w.Close();
        w.Blank();
    }

    private static void EmitRegisterHelpers(EmitWriter w)
    {
        w.Open("public static bool Conditional(int cc)");
        w.Open("switch (cc)");
        w.Line("case 0: return (F & 0x40) == 0;   // NZ");
        w.Line("case 1: return (F & 0x40) != 0;   // Z");
        w.Line("case 2: return (F & 0x01) == 0;   // NC");
        w.Line("case 3: return (F & 0x01) != 0;   // C");
        w.Line("case 4: return (F & 0x04) == 0;   // PO");
        w.Line("case 5: return (F & 0x04) != 0;   // PE");
        w.Line("case 6: return (F & 0x80) == 0;   // P");
        w.Line("case 7: return (F & 0x80) != 0;   // M");
        w.Line("default: return true;");
        w.Close();
        w.Close();
        w.Blank();

        w.Open("public static void Push16(ushort value)");
        w.Line("SP = (ushort)(SP - 1); Memory.Write(SP, (byte)(value >> 8));");
        w.Line("SP = (ushort)(SP - 1); Memory.Write(SP, (byte)(value & 0xFF));");
        w.Close();
        w.Blank();

        w.Open("public static ushort Pop16()");
        w.Line("byte low = Memory.Read(SP); SP = (ushort)(SP + 1);");
        w.Line("byte high = Memory.Read(SP); SP = (ushort)(SP + 1);");
        w.Line("return (ushort)(low | (high << 8));");
        w.Close();
        w.Blank();

        w.Open("public static byte GetR8(int r)");
        w.Open("switch (r)");
        w.Line("case 0: return B; case 1: return C; case 2: return D; case 3: return E;");
        w.Line("case 4: return H; case 5: return L; case 6: return Memory.Read(HL); case 7: return A;");
        w.Line("default: return 0;");
        w.Close();
        w.Close();
        w.Blank();

        w.Open("public static void SetR8(int r, byte value)");
        w.Open("switch (r)");
        w.Line("case 0: B = value; break; case 1: C = value; break;");
        w.Line("case 2: D = value; break; case 3: E = value; break;");
        w.Line("case 4: H = value; break; case 5: L = value; break;");
        w.Line("case 6: Memory.Write(HL, value); break; case 7: A = value; break;");
        w.Close();
        w.Close();
        w.Blank();

        w.Line("public static ushort GetRP16(int rp)");
        w.Line("{");
        w.Line("    switch (rp) { case 0: return BC; case 1: return DE; case 2: return HL; case 3: return AF; default: return 0; }");
        w.Line("}");
        w.Blank();

        w.Line("public static void SetRP16(int rp, ushort value)");
        w.Line("{");
        w.Line("    switch (rp) { case 0: BC = value; break; case 1: DE = value; break; case 2: HL = value; break; case 3: AF = value; break; }");
        w.Line("}");
        w.Blank();

        w.Line("public static ushort GetRP16Arith(int rp)");
        w.Line("{");
        w.Line("    switch (rp) { case 0: return BC; case 1: return DE; case 2: return HL; case 3: return SP; default: return 0; }");
        w.Line("}");
        w.Blank();

        w.Line("public static void SetRP16Arith(int rp, ushort value)");
        w.Line("{");
        w.Line("    switch (rp) { case 0: BC = value; break; case 1: DE = value; break; case 2: HL = value; break; case 3: SP = value; break; }");
        w.Line("}");
        w.Blank();
    }

    private static void EmitAluHelpers(EmitWriter w)
    {
        w.Line("public static void SetSZ(byte value) { F = (byte)((F & 0x29) | (value == 0 ? 0x40 : 0) | (value & 0x80)); }");
        w.Blank();

        w.Open("public static byte Inc8(byte value)");
        w.Line("int result = value + 1;");
        w.Line("bool half = (value & 0x0F) == 0x0F;");
        w.Line("bool overflow = value == 0x7F;");
        w.Line("F = (byte)((F & 0x01) | ((byte)result & 0x80) | ((byte)result == 0 ? 0x40 : 0) | (half ? 0x10 : 0) | (overflow ? 0x04 : 0));");
        w.Line("return (byte)result;");
        w.Close();
        w.Blank();

        w.Open("public static byte Dec8(byte value)");
        w.Line("int result = value - 1;");
        w.Line("bool half = (value & 0x0F) == 0;");
        w.Line("bool overflow = value == 0x80;");
        w.Line("F = (byte)((F & 0x01) | ((byte)result & 0x80) | ((byte)result == 0 ? 0x40 : 0) | (half ? 0x10 : 0) | (overflow ? 0x04 : 0) | 0x02);");
        w.Line("return (byte)result;");
        w.Close();
        w.Blank();

        w.Open("public static void AddA(byte value)");
        w.Line("int a = A; int result = a + value;");
        w.Line("bool carry = result > 0xFF;");
        w.Line("bool half = ((a & 0x0F) + (value & 0x0F)) > 0x0F;");
        w.Line("bool overflow = ((~(a ^ value) & (a ^ (byte)result)) & 0x80) != 0;");
        w.Line("A = (byte)result;");
        w.Line("F = (byte)((A & 0x80) | (A == 0 ? 0x40 : 0) | (half ? 0x10 : 0) | (overflow ? 0x04 : 0) | (carry ? 0x01 : 0));");
        w.Close();
        w.Blank();

        w.Open("public static void AdcA(byte value)");
        w.Line("int a = A; int carryIn = F & 1; int result = a + value + carryIn;");
        w.Line("bool carry = result > 0xFF;");
        w.Line("bool half = ((a & 0x0F) + (value & 0x0F) + carryIn) > 0x0F;");
        w.Line("bool overflow = ((~(a ^ value) & (a ^ (byte)result)) & 0x80) != 0;");
        w.Line("A = (byte)result;");
        w.Line("F = (byte)((A & 0x80) | (A == 0 ? 0x40 : 0) | (half ? 0x10 : 0) | (overflow ? 0x04 : 0) | (carry ? 0x01 : 0));");
        w.Close();
        w.Blank();

        w.Open("public static void SubA(byte value)");
        w.Line("int a = A; int result = a - value;");
        w.Line("bool carry = a < value;");
        w.Line("bool half = (a & 0x0F) < (value & 0x0F);");
        w.Line("bool overflow = ((a ^ value) & (a ^ (byte)result) & 0x80) != 0;");
        w.Line("F = (byte)(((byte)result & 0x80) | ((byte)result == 0 ? 0x40 : 0) | (half ? 0x10 : 0) | (overflow ? 0x04 : 0) | 0x02 | (carry ? 0x01 : 0));");
        w.Line("A = (byte)result;");
        w.Close();
        w.Blank();

        w.Open("public static void SbcA(byte value)");
        w.Line("int a = A; int carryIn = F & 1; int result = a - value - carryIn;");
        w.Line("bool carry = result < 0;");
        w.Line("bool half = ((a & 0x0F) - (value & 0x0F) - carryIn) < 0;");
        w.Line("bool overflow = ((a ^ value) & (a ^ (byte)result) & 0x80) != 0;");
        w.Line("F = (byte)(((byte)result & 0x80) | ((byte)result == 0 ? 0x40 : 0) | (half ? 0x10 : 0) | (overflow ? 0x04 : 0) | 0x02 | (carry ? 0x01 : 0));");
        w.Line("A = (byte)result;");
        w.Close();
        w.Blank();

        w.Line("public static void AndA(byte value) { A = (byte)(A & value); F = (byte)((A == 0 ? 0x40 : 0) | (A & 0x80) | Parity[A] | 0x10); }");
        w.Line("public static void XorA(byte value) { A = (byte)(A ^ value); F = (byte)((A == 0 ? 0x40 : 0) | (A & 0x80) | Parity[A]); }");
        w.Line("public static void OrA(byte value) { A = (byte)(A | value); F = (byte)((A == 0 ? 0x40 : 0) | (A & 0x80) | Parity[A]); }");
        w.Blank();

        w.Open("public static void CpA(byte value)");
        w.Line("int a = A; int result = a - value;");
        w.Line("bool carry = a < value;");
        w.Line("bool half = (a & 0x0F) < (value & 0x0F);");
        w.Line("bool overflow = ((a ^ value) & (a ^ (byte)result) & 0x80) != 0;");
        w.Line("F = (byte)(((byte)result & 0x80) | ((byte)result == 0 ? 0x40 : 0) | (half ? 0x10 : 0) | (overflow ? 0x04 : 0) | 0x02 | (carry ? 0x01 : 0));");
        w.Close();
        w.Blank();

        w.Open("public static void Alu(int op, byte value)");
        w.Open("switch (op)");
        w.Line("case 0: AddA(value); break;");
        w.Line("case 1: AdcA(value); break;");
        w.Line("case 2: SubA(value); break;");
        w.Line("case 3: SbcA(value); break;");
        w.Line("case 4: AndA(value); break;");
        w.Line("case 5: XorA(value); break;");
        w.Line("case 6: OrA(value); break;");
        w.Line("case 7: CpA(value); break;");
        w.Close();
        w.Close();
        w.Blank();

        w.Open("public static void Add16HL(ushort value)");
        w.Line("int hl = HL; int result = hl + value;");
        w.Line("bool half = ((hl & 0x0FFF) + (value & 0x0FFF)) > 0x0FFF;");
        w.Line("bool carry = result > 0xFFFF;");
        w.Line("HL = (ushort)result;");
        w.Line("F = (byte)((F & 0xC4) | (half ? 0x10 : 0) | (carry ? 0x01 : 0));");
        w.Close();
        w.Blank();

        w.Open("public static void Adc16HL(ushort value)");
        w.Line("int hl = HL; int carryIn = F & 1; int result = hl + value + carryIn;");
        w.Line("bool half = ((hl & 0x0FFF) + (value & 0x0FFF) + carryIn) > 0x0FFF;");
        w.Line("bool carry = result > 0xFFFF;");
        w.Line("bool overflow = ((~(hl ^ value) & (hl ^ result)) & 0x8000) != 0;");
        w.Line("HL = (ushort)result;");
        w.Line("F = (byte)(((result & 0x8000) != 0 ? 0x80 : 0) | ((ushort)result == 0 ? 0x40 : 0) | (half ? 0x10 : 0) | (overflow ? 0x04 : 0) | (carry ? 0x01 : 0));");
        w.Close();
        w.Blank();

        w.Open("public static void Sbc16HL(ushort value)");
        w.Line("int hl = HL; int carryIn = F & 1; int result = hl - value - carryIn;");
        w.Line("bool carry = result < 0;");
        w.Line("bool half = ((hl & 0x0FFF) - (value & 0x0FFF) - carryIn) < 0;");
        w.Line("bool overflow = ((hl ^ value) & (hl ^ result) & 0x8000) != 0;");
        w.Line("HL = (ushort)result;");
        w.Line("F = (byte)(((result & 0x8000) != 0 ? 0x80 : 0) | ((ushort)result == 0 ? 0x40 : 0) | (half ? 0x10 : 0) | (overflow ? 0x04 : 0) | 0x02 | (carry ? 0x01 : 0));");
        w.Close();
        w.Blank();

        w.Open("public static void Add16Index(bool isIX, ushort value)");
        w.Line("int reg = isIX ? IX : IY; int result = reg + value;");
        w.Line("bool half = ((reg & 0x0FFF) + (value & 0x0FFF)) > 0x0FFF;");
        w.Line("bool carry = result > 0xFFFF;");
        w.Line("if (isIX) IX = (ushort)result; else IY = (ushort)result;");
        w.Line("F = (byte)((F & 0xC4) | (half ? 0x10 : 0) | (carry ? 0x01 : 0));");
        w.Close();
        w.Blank();

        w.Line("public static void Rlca() { int c = (A >> 7) & 1; A = (byte)((A << 1) | c); F = (byte)((F & 0xC4) | c); }");
        w.Line("public static void Rrca() { int c = A & 1; A = (byte)((A >> 1) | (c << 7)); F = (byte)((F & 0xC4) | c); }");
        w.Line("public static void Rla() { int oldCarry = F & 1; int c = (A >> 7) & 1; A = (byte)((A << 1) | oldCarry); F = (byte)((F & 0xC4) | c); }");
        w.Line("public static void Rra() { int oldCarry = F & 1; int c = A & 1; A = (byte)((A >> 1) | (oldCarry << 7)); F = (byte)((F & 0xC4) | c); }");
        w.Blank();

        w.Open("public static byte RotateCB(int kind, byte value)");
        w.Line("int carry = 0;");
        w.Open("switch (kind)");
        w.Line("case 0: carry = (value >> 7) & 1; value = (byte)((value << 1) | carry); break;   // RLC");
        w.Line("case 1: carry = value & 1; value = (byte)((value >> 1) | (carry << 7)); break;   // RRC");
        w.Line("case 2: carry = (value >> 7) & 1; value = (byte)((value << 1) | (F & 1)); break; // RL");
        w.Line("case 3: carry = value & 1; value = (byte)((value >> 1) | ((F & 1) << 7)); break; // RR");
        w.Line("case 4: carry = (value >> 7) & 1; value = (byte)(value << 1); break;             // SLA");
        w.Line("case 5: carry = value & 1; value = (byte)((value >> 1) | (value & 0x80)); break; // SRA");
        w.Line("case 6: carry = (value >> 7) & 1; value = (byte)((value << 1) | 1); break;       // SLL");
        w.Line("case 7: carry = value & 1; value = (byte)(value >> 1); break;                    // SRL");
        w.Close();
        w.Line("F = (byte)((value == 0 ? 0x40 : 0) | (value & 0x80) | Parity[value] | carry);");
        w.Line("return value;");
        w.Close();
        w.Blank();

        w.Open("public static void BitValue(byte value, int bit)");
        w.Line("bool set = (value & (1 << bit)) != 0;");
        w.Line("F = (byte)((F & 0x01) | 0x10 | (!set ? 0x40 : 0) | ((set && bit == 7) ? 0x80 : 0));");
        w.Close();
        w.Blank();

        w.Open("public static void Daa()");
        w.Line("int correction = 0; int carry = F & 1;");
        w.Line("if ((F & 0x10) != 0 || (A & 0x0F) > 9) correction |= 0x06;");
        w.Line("if (carry != 0 || A > 0x99) { correction |= 0x60; carry = 1; }");
        w.Line("if ((F & 0x02) != 0) A = (byte)(A - correction); else A = (byte)(A + correction);");
        w.Line("F = (byte)((F & 0x02) | (carry != 0 ? 1 : 0) | (A == 0 ? 0x40 : 0) | (A & 0x80) | Parity[A] | (((F & 0x10) != 0) ? 0x10 : 0));");
        w.Close();
        w.Blank();

        w.Open("public static void NegA()");
        w.Line("byte old = A; int result = -old;");
        w.Line("bool overflow = old == 0x80;");
        w.Line("bool half = (old & 0x0F) != 0;");
        w.Line("bool carry = old != 0;");
        w.Line("A = (byte)result;");
        w.Line("F = (byte)((A & 0x80) | (A == 0 ? 0x40 : 0) | (half ? 0x10 : 0) | (overflow ? 0x04 : 0) | 0x02 | (carry ? 0x01 : 0));");
        w.Close();
        w.Blank();
    }

    private static void EmitTimingHelpers(EmitWriter w)
    {
        w.Open("public static void Throttle()");
        w.Line("if (!ThrottleEnabled || Speed <= 0.0) return;");
        w.Line("long freq = System.Diagnostics.Stopwatch.Frequency;");
        w.Line("long now = System.Diagnostics.Stopwatch.GetTimestamp();");
        w.Line("long target = (long)(3500000.0 * Speed);");
        w.Line("long ahead = TStates - (now - ClockStart) * target / freq;");
        w.Line("if (ahead < -target) { ClockStart = System.Diagnostics.Stopwatch.GetTimestamp(); return; }");
        w.Line("while (ahead > target / 100) { Thread.Sleep(1); now = System.Diagnostics.Stopwatch.GetTimestamp(); ahead = TStates - (now - ClockStart) * target / freq; }");
        w.Line("while (ahead > 0) { now = System.Diagnostics.Stopwatch.GetTimestamp(); ahead = TStates - (now - ClockStart) * target / freq; }");
        w.Close();
        w.Blank();

        w.Open("public static bool CheckInterrupt(ushort pc)");
        w.Line("if (TStates >= NextIntT)");
        w.Line("{");
        w.Line("    NextIntT += 69888;");
        w.Line("    InterruptPending = true;");
        w.Line("    Frames++;");
        w.Line("}");
        w.Blank();
        w.Line("// A threshold, not a mask: InsCount advances by a whole block at a");
        w.Line("// time, so (InsCount & 4095) == 0 would usually never be true.");
        w.Line("if (InsCount >= _nextThrottleCheck)");
        w.Line("{");
        w.Line("    _nextThrottleCheck = InsCount + 4096;");
        w.Line("    Throttle();");
        w.Line("}");
        w.Blank();
        w.Line("if (!InterruptPending || !IFF1) return false;");
        w.Line("InterruptPending = false;");
        w.Line("Halted = false;");
        w.Line("Push16(pc);");
        w.Line("IFF1 = false; IFF2 = false;");
        w.Line("if (IM == 2) { ushort vector = (ushort)((I << 8) | 0xFF); DispatchTarget = Memory.Read16(vector); }");
        w.Line("else DispatchTarget = 0x0038;");
        w.Line("return true;");
        w.Close();
        w.Blank();

        w.Open("public static void Halt()");
        w.Line("// HALT with interrupts disabled can never be woken: no IM0/1/2");
        w.Line("// interrupt is accepted, and an NMI needs hardware this does not have.");
        w.Line("// Reporting it is far more useful than spinning forever.");
        w.Line("if (!IFF1) { Trap(\"HALT with interrupts disabled at $\" + LastPC.ToString(\"X4\")); return; }");
        w.Blank();
        w.Line("Halted = true;");
        w.Blank();
        w.Open("if (HeadlessMode)");
        w.Line("// No wall clock in headless mode: jump straight to the next frame");
        w.Line("// boundary. That keeps CI deterministic and fast, and it is what a");
        w.Line("// test wants - the frame count, not real-time pacing.");
        w.Line("TStates = NextIntT;");
        w.Line("InterruptPending = true;");
        w.Close();
        w.Open("else");
        w.Line("long freq = System.Diagnostics.Stopwatch.Frequency;");
        w.Open("while (Halted && !InterruptPending)");
        w.Line("long now = System.Diagnostics.Stopwatch.GetTimestamp();");
        w.Line("long wall = (now - ClockStart) * (long)(3500000.0 * Speed) / freq;");
        w.Line("if (wall > TStates) TStates = wall;");
        w.Line("// The frame boundary is consumed by CheckInterrupt, which is the one");
        w.Line("// place that counts frames; bumping it here too would double the rate.");
        w.Line("if (TStates >= NextIntT) InterruptPending = true;");
        w.Line("else Thread.Sleep(1);");
        w.Close();
        w.Close();
        w.Blank();
        w.Line("Halted = false;");
        w.Close();
        w.Blank();
    }

    private static void EmitPortHelpers(EmitWriter w)
    {
        // NoInlining is load-bearing: games poll the keyboard in a tight loop,
        // and without a call barrier the JIT may hoist the KeyMatrix read out of
        // the loop, freezing input.
        w.Line("[System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]");
        w.Open("public static byte ReadPort(ushort port)");
        w.Line("Thread.MemoryBarrier();");
        w.Open("if ((port & 1) == 0)");
        w.Line("int high = port >> 8; int result = 0xFF;");
        w.Line("for (int i = 0; i < 8; i++) { if ((high & (1 << i)) == 0) { Thread.MemoryBarrier(); result &= KeyMatrix[i]; } }");
        w.Line("LastKeyPort = port;");
        w.Line("LastKeyRead = (byte)(result & 0xBF);");
        w.Line("return (byte)(result & 0xBF);");
        w.Close();
        w.Line("if (port == 0x7FFD || (port & 0x8002) == 0) return Port7FFD;");
        w.Line("if (port == 0x1FFD) return Port1FFD;");
        w.Line("if (port == 0xFFFD) return Ayu.Sel;");
        w.Line("if (port == 0xBFFD) return Ayu.Regs[Ayu.Sel];");
        w.Line("if ((port & 0x1F) == 0x1F) return Kempston;");
        w.Line("return 0xFF;");
        w.Close();
        w.Blank();

        w.Line("[System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]");
        w.Open("public static void WritePort(ushort port, byte value)");
        w.Line("Thread.MemoryBarrier();");
        w.Line("if ((port & 1) == 0) { Border = (byte)(value & 7); BeeperLevel = (value & 0x10) != 0; return; }");
        w.Line("if (port == 0x1FFD) { Port1FFD = value; return; }");
        w.Line("if (port == 0x7FFD || (port & 0x8002) == 0)");
        w.Line("{");
        w.Line("    if (!PagingDisabled) Port7FFD = value;");
        w.Line("    if ((value & 0x20) != 0) PagingDisabled = true;");
        w.Line("    return;");
        w.Line("}");
        w.Line("if (port == 0xFFFD) { Ayu.Sel = (byte)(value & 15); return; }");
        w.Line("if (port == 0xBFFD) { Ayu.Regs[Ayu.Sel] = value; return; }");
        w.Line("if ((port & 0x1F) == 0x1F) { Kempston = value; return; }");
        w.Close();
        w.Blank();
    }
}
