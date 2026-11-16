namespace ZX2ILRecomp.Lifting;

/// <summary>
/// Emits the instruction executor: one <c>switch</c> per prefix page.
///
/// This is the fallback path, but it is also the reference implementation the
/// inlined label methods call into for anything irregular (block moves, DAA,
/// port I/O), so it has to be complete.
/// </summary>
internal static partial class RuntimeEmitter
{
    private static void EmitExecMethods(EmitWriter w)
    {
        EmitExecBase(w);
        EmitExecEd(w);
        EmitBlockOps(w);
        EmitExecPrefixed(w);
        EmitExecPrefixedCb(w);
    }

    private static void EmitExecBase(EmitWriter w)
    {
        w.Open("public static void ExecBase(byte op, ushort val)");
        w.Line("TStates += BaseT[op];");
        w.Line("byte n = (byte)val;");
        w.Open("switch (op)");
        w.Line("case 0x00: return;                                   // NOP");
        w.Line("case 0x01: C = n; B = (byte)(val >> 8); return;       // LD BC,nn");
        w.Line("case 0x11: E = n; D = (byte)(val >> 8); return;       // LD DE,nn");
        w.Line("case 0x21: L = n; H = (byte)(val >> 8); return;       // LD HL,nn");
        w.Line("case 0x31: SP = val; return;                          // LD SP,nn");
        w.Line("case 0x02: Memory.Write(BC, A); return;               // LD (BC),A");
        w.Line("case 0x12: Memory.Write(DE, A); return;               // LD (DE),A");
        w.Line("case 0x0A: A = Memory.Read(BC); return;               // LD A,(BC)");
        w.Line("case 0x1A: A = Memory.Read(DE); return;               // LD A,(DE)");
        w.Line("case 0x22: Memory.Write16(val, HL); return;           // LD (nn),HL");
        w.Line("case 0x2A: HL = Memory.Read16(val); return;           // LD HL,(nn)");
        w.Line("case 0x32: Memory.Write(val, A); return;              // LD (nn),A");
        w.Line("case 0x3A: A = Memory.Read(val); return;              // LD A,(nn)");
        w.Line("case 0x07: Rlca(); return;");
        w.Line("case 0x0F: Rrca(); return;");
        w.Line("case 0x17: Rla(); return;");
        w.Line("case 0x1F: Rra(); return;");
        w.Line("case 0x08: { byte ta = A; A = A2; A2 = ta; byte tf = F; F = F2; F2 = tf; return; }  // EX AF,AF'");
        w.Line("case 0xD9: { byte t; t = B; B = B2; B2 = t; t = C; C = C2; C2 = t; t = D; D = D2; D2 = t; t = E; E = E2; E2 = t; t = H; H = H2; H2 = t; t = L; L = L2; L2 = t; return; }  // EXX");
        w.Line("case 0xEB: { ushort t = DE; DE = HL; HL = t; return; }  // EX DE,HL");
        w.Line("case 0xE3: { ushort t = Memory.Read16(SP); Memory.Write16(SP, HL); HL = t; return; }  // EX (SP),HL");
        w.Line("case 0x27: Daa(); return;");
        w.Line("case 0x2F: A = (byte)(~A); F = (byte)((F & 0xC5) | 0x12); return;  // CPL");
        w.Line("case 0x37: F = (byte)((F & 0xC4) | 0x01); return;                 // SCF");
        w.Line("case 0x3F: { int c = F & 1; F = (byte)((F & 0xC4) | (c << 4) | (c ^ 1)); return; }  // CCF");
        w.Line("case 0xF3: IFF1 = false; IFF2 = false; return;        // DI");
        w.Line("case 0xFB: IFF1 = true; IFF2 = true; return;          // EI");
        w.Line("case 0xF9: SP = HL; return;                           // LD SP,HL");
        w.Line("case 0xD3: WritePort((ushort)((A << 8) | (byte)val), A); return;   // OUT (n),A");
        w.Line("case 0xDB: A = ReadPort((ushort)((A << 8) | (byte)val)); return;   // IN A,(n)");
        w.Close();
        w.Blank();
        w.Line("if ((op & 0xC7) == 0x04) { int r = (op >> 3) & 7; SetR8(r, Inc8(GetR8(r))); return; }   // INC r");
        w.Line("if ((op & 0xC7) == 0x05) { int r = (op >> 3) & 7; SetR8(r, Dec8(GetR8(r))); return; }   // DEC r");
        w.Line("if ((op & 0xC7) == 0x06) { SetR8((op >> 3) & 7, n); return; }                          // LD r,n");
        w.Line("if ((op & 0xCF) == 0x03) { int rp = (op >> 4) & 3; SetRP16Arith(rp, (ushort)(GetRP16Arith(rp) + 1)); return; }  // INC rr");
        w.Line("if ((op & 0xCF) == 0x0B) { int rp = (op >> 4) & 3; SetRP16Arith(rp, (ushort)(GetRP16Arith(rp) - 1)); return; }  // DEC rr");
        w.Line("if ((op & 0xCF) == 0x09) { Add16HL(GetRP16Arith((op >> 4) & 3)); return; }              // ADD HL,rr");
        w.Line("if ((op & 0xC7) == 0xC5) { Push16(GetRP16((op >> 4) & 3)); return; }                    // PUSH qq");
        w.Line("if ((op & 0xC7) == 0xC1) { SetRP16((op >> 4) & 3, Pop16()); return; }                    // POP qq");
        w.Line("if ((op & 0xC7) == 0xC6) { Alu((op >> 3) & 7, n); return; }                              // ALU n");
        w.Line("if (op >= 0x40 && op <= 0x7F && op != 0x76) { SetR8((op >> 3) & 7, GetR8(op & 7)); return; }  // LD r,r'");
        w.Line("if (op >= 0x80 && op <= 0xBF) { Alu((op >> 3) & 7, GetR8(op & 7)); return; }              // ALU r");
        w.Line("UnknownOpcode(op, LastPC);");
        w.Close();
        w.Blank();

        w.Open("public static void ExecCB(byte op)");
        w.Line("TStates += CbT[op];");
        w.Line("int reg = op & 7; int kind = op >> 3;");
        w.Line("if (kind < 8) { byte v = GetR8(reg); v = RotateCB(kind, v); SetR8(reg, v); return; }");
        w.Line("if (kind < 16) { byte v = GetR8(reg); BitValue(v, kind - 8); return; }");
        w.Line("if (kind < 24) { int bit = kind - 16; byte v = GetR8(reg); v = (byte)(v & ~(1 << bit)); SetR8(reg, v); return; }");
        w.Line("int setBit = kind - 24; byte sv = GetR8(reg); sv = (byte)(sv | (1 << setBit)); SetR8(reg, sv);");
        w.Close();
        w.Blank();
    }

    private static void EmitExecEd(EmitWriter w)
    {
        w.Open("public static void ExecEDNormal(byte op, ushort val)");
        w.Line("TStates += EdT[op];");
        w.Line("if ((op & 0xC7) == 0x40) { int r = (op >> 3) & 7; byte v = ReadPort(BC); if (r != 6) SetR8(r, v); F = (byte)((F & 1) | (v == 0 ? 0x40 : 0) | (v & 0x80) | Parity[v]); return; }  // IN r,(C)");
        w.Line("if ((op & 0xC7) == 0x41) { int r = (op >> 3) & 7; WritePort(BC, (r == 6) ? (byte)0 : GetR8(r)); return; }  // OUT (C),r");
        w.Line("if ((op & 0xCF) == 0x42) { Sbc16HL(GetRP16Arith((op >> 4) & 3)); return; }   // SBC HL,rr");
        w.Line("if ((op & 0xCF) == 0x4A) { Adc16HL(GetRP16Arith((op >> 4) & 3)); return; }   // ADC HL,rr");
        w.Line("if ((op & 0xC7) == 0x43) { Memory.Write16(val, GetRP16Arith((op >> 4) & 3)); return; }  // LD (nn),rr");
        w.Line("if ((op & 0xC7) == 0x4B) { SetRP16Arith((op >> 4) & 3, Memory.Read16(val)); return; }  // LD rr,(nn)");
        w.Line("if ((op & 0xC7) == 0x44) { NegA(); return; }                                 // NEG");
        w.Line("if (op == 0x46 || op == 0x4E || op == 0x66 || op == 0x6E) { IM = 0; return; }");
        w.Line("if (op == 0x56 || op == 0x76) { IM = 1; return; }");
        w.Line("if (op == 0x5E || op == 0x7E) { IM = 2; return; }");
        w.Line("if (op == 0x47) { I = A; return; }");
        w.Line("if (op == 0x4F) { R = A; return; }");
        w.Line("if (op == 0x57) { A = I; SetSZ(A); F = (byte)((F & 1) | (IFF2 ? 4 : 0)); return; }");
        w.Line("if (op == 0x5F) { A = R; SetSZ(A); F = (byte)((F & 1) | (IFF2 ? 4 : 0)); return; }");
        w.Line("if (op == 0x67) { byte m = Memory.Read(HL); byte low = (byte)(m & 0x0F); m = (byte)((m >> 4) | (A << 4)); A = (byte)((A & 0xF0) | low); Memory.Write(HL, m); SetSZ(A); F = (byte)((F & 1) | Parity[A]); return; }  // RRD");
        w.Line("if (op == 0x6F) { byte m = Memory.Read(HL); byte high = (byte)(m >> 4); m = (byte)((m << 4) | (A & 0x0F)); A = (byte)((A & 0xF0) | high); Memory.Write(HL, m); SetSZ(A); F = (byte)((F & 1) | Parity[A]); return; }  // RLD");
        w.Line("if (op >= 0xA0 && op <= 0xBB) { Block(op); return; }");
        w.Close();
        w.Blank();
    }

    private static void EmitBlockOps(EmitWriter w)
    {
        w.Open("public static void Block(byte op)");
        w.Open("switch (op)");
        w.Line("case 0xA0: Ldi(); break;");
        w.Line("case 0xB0: while (BC != 0) { Ldi(); TStates += 5; } break;");
        w.Line("case 0xA8: Ldd(); break;");
        w.Line("case 0xB8: while (BC != 0) { Ldd(); TStates += 5; } break;");
        w.Line("case 0xA1: Cpi(); break;");
        w.Line("case 0xB1: while (BC != 0) { Cpi(); TStates += 5; if ((F & 0x40) == 0) break; } break;");
        w.Line("case 0xA9: Cpd(); break;");
        w.Line("case 0xB9: while (BC != 0) { Cpd(); TStates += 5; if ((F & 0x40) == 0) break; } break;");
        w.Line("case 0xA2: Ini(); break;");
        w.Line("case 0xB2: while (B != 0) { Ini(); TStates += 5; } break;");
        w.Line("case 0xAA: Ind(); break;");
        w.Line("case 0xBA: while (B != 0) { Ind(); TStates += 5; } break;");
        w.Line("case 0xA3: Outi(); break;");
        w.Line("case 0xB3: while (B != 0) { Outi(); TStates += 5; } break;");
        w.Line("case 0xAB: Outd(); break;");
        w.Line("case 0xBB: while (B != 0) { Outd(); TStates += 5; } break;");
        w.Close();
        w.Close();
        w.Blank();

        w.Line("static void Ldi() { TStates += 16; byte v = Memory.Read(HL); Memory.Write(DE, v); HL = (ushort)(HL + 1); DE = (ushort)(DE + 1); BC = (ushort)(BC - 1); F = (byte)((F & 0xC1) | ((BC != 0) ? 0x04 : 0)); }");
        w.Line("static void Ldd() { TStates += 16; byte v = Memory.Read(HL); Memory.Write(DE, v); HL = (ushort)(HL - 1); DE = (ushort)(DE - 1); BC = (ushort)(BC - 1); F = (byte)((F & 0xC1) | ((BC != 0) ? 0x04 : 0)); }");
        w.Line("static void Cpi() { TStates += 16; byte v = Memory.Read(HL); int res = A - v; HL = (ushort)(HL + 1); BC = (ushort)(BC - 1); F = (byte)((F & 0x01) | ((byte)res == 0 ? 0x40 : 0) | ((byte)res & 0x80) | ((BC != 0) ? 0x04 : 0) | 0x02); }");
        w.Line("static void Cpd() { TStates += 16; byte v = Memory.Read(HL); int res = A - v; HL = (ushort)(HL - 1); BC = (ushort)(BC - 1); F = (byte)((F & 0x01) | ((byte)res == 0 ? 0x40 : 0) | ((byte)res & 0x80) | ((BC != 0) ? 0x04 : 0) | 0x02); }");
        w.Line("static void Ini() { TStates += 16; byte v = ReadPort(BC); Memory.Write(HL, v); HL = (ushort)(HL + 1); B--; SetSZ(B); F = (byte)((F & 1) | 0x02); }");
        w.Line("static void Ind() { TStates += 16; byte v = ReadPort(BC); Memory.Write(HL, v); HL = (ushort)(HL - 1); B--; SetSZ(B); F = (byte)((F & 1) | 0x02); }");
        w.Line("static void Outi() { TStates += 16; byte v = Memory.Read(HL); HL = (ushort)(HL + 1); B--; WritePort(BC, v); SetSZ(B); }");
        w.Line("static void Outd() { TStates += 16; byte v = Memory.Read(HL); HL = (ushort)(HL - 1); B--; WritePort(BC, v); SetSZ(B); }");
        w.Blank();
    }

    private static void EmitExecPrefixed(EmitWriter w)
    {
        w.Line("public static byte GetIXH(bool isIX) { return (byte)((isIX ? IX : IY) >> 8); }");
        w.Line("public static byte GetIXL(bool isIX) { return (byte)((isIX ? IX : IY) & 0xFF); }");
        w.Line("public static void SetIXH(bool isIX, byte v) { if (isIX) IX = (ushort)((v << 8) | (IX & 0xFF)); else IY = (ushort)((v << 8) | (IY & 0xFF)); }");
        w.Line("public static void SetIXL(bool isIX, byte v) { if (isIX) IX = (ushort)((IX & 0xFF00) | v); else IY = (ushort)((IY & 0xFF00) | v); }");
        w.Blank();

        w.Open("public static byte GetR8IX(int r, bool isIX)");
        w.Open("switch (r)");
        w.Line("case 0: return B; case 1: return C; case 2: return D; case 3: return E;");
        w.Line("case 4: return isIX ? (byte)(IX >> 8) : (byte)(IY >> 8);");
        w.Line("case 5: return isIX ? (byte)(IX & 0xFF) : (byte)(IY & 0xFF);");
        w.Line("case 7: return A;");
        w.Line("default: return 0;");
        w.Close();
        w.Close();
        w.Blank();

        w.Open("public static void SetR8IX(int r, byte value, bool isIX)");
        w.Open("switch (r)");
        w.Line("case 0: B = value; break; case 1: C = value; break;");
        w.Line("case 2: D = value; break; case 3: E = value; break;");
        w.Line("case 4: if (isIX) IX = (ushort)((value << 8) | (IX & 0xFF)); else IY = (ushort)((value << 8) | (IY & 0xFF)); break;");
        w.Line("case 5: if (isIX) IX = (ushort)((IX & 0xFF00) | value); else IY = (ushort)((IY & 0xFF00) | value); break;");
        w.Line("case 7: A = value; break;");
        w.Close();
        w.Close();
        w.Blank();

        w.Open("public static void ExecPrefixed(int prefix, byte op, sbyte d, ushort val)");
        w.Line("TStates += PfT[op];");
        w.Line("bool isIX = prefix == 0xDD;");
        w.Line("byte n = (byte)val;");
        w.Blank();
        w.Open("switch (op)");
        w.Line("case 0x21: if (isIX) IX = val; else IY = val; return;");
        w.Line("case 0x22: Memory.Write16(val, isIX ? IX : IY); return;");
        w.Line("case 0x2A: { ushort v = Memory.Read16(val); if (isIX) IX = v; else IY = v; return; }");
        w.Line("case 0x23: if (isIX) IX = (ushort)(IX + 1); else IY = (ushort)(IY + 1); return;");
        w.Line("case 0x2B: if (isIX) IX = (ushort)(IX - 1); else IY = (ushort)(IY - 1); return;");
        w.Line("case 0x24: SetIXH(isIX, Inc8(GetIXH(isIX))); return;");
        w.Line("case 0x25: SetIXH(isIX, Dec8(GetIXH(isIX))); return;");
        w.Line("case 0x26: SetIXH(isIX, n); return;");
        w.Line("case 0x2C: SetIXL(isIX, Inc8(GetIXL(isIX))); return;");
        w.Line("case 0x2D: SetIXL(isIX, Dec8(GetIXL(isIX))); return;");
        w.Line("case 0x2E: SetIXL(isIX, n); return;");
        w.Line("case 0xE1: if (isIX) IX = Pop16(); else IY = Pop16(); return;");
        w.Line("case 0xE5: Push16(isIX ? IX : IY); return;");
        w.Line("case 0xE3: { ushort v = Memory.Read16(SP); Memory.Write16(SP, isIX ? IX : IY); if (isIX) IX = v; else IY = v; return; }");
        w.Line("case 0xF9: SP = isIX ? IX : IY; return;");
        w.Close();
        w.Blank();
        w.Line("if ((op & 0xCF) == 0x09) { int rp = (op >> 4) & 3; ushort v = (rp == 2) ? (isIX ? IX : IY) : GetRP16Arith(rp); Add16Index(isIX, v); return; }");
        w.Blank();
        w.Open("if (UsesIXYDisp[op] != 0)");
        w.Line("ushort address = (ushort)((isIX ? IX : IY) + d);");
        w.Line("if (op == 0x34) { Memory.Write(address, Inc8(Memory.Read(address))); return; }");
        w.Line("if (op == 0x35) { Memory.Write(address, Dec8(Memory.Read(address))); return; }");
        w.Line("if (op == 0x36) { Memory.Write(address, n); return; }");
        w.Line("if (op >= 0x70 && op <= 0x77 && op != 0x76) { Memory.Write(address, GetR8(op & 7)); return; }");
        w.Line("if ((op & 0xC0) == 0x40) { int dst = (op >> 3) & 7; if (dst != 6) SetR8(dst, Memory.Read(address)); return; }");
        w.Line("if ((op & 0xC0) == 0x80) { Alu((op >> 3) & 7, Memory.Read(address)); return; }");
        w.Close();
        w.Blank();
        w.Line("// Remaining DD/FD forms: the prefix is ignored for B/C/D/E/A/H/L,");
        w.Line("// and 4/5 mean IXH/IXL (IYH/IYL) instead of H/L.");
        w.Line("if (op == 0x76) { Halt(); return; }");
        w.Line("if ((op & 0xC7) == 0x04) { int r = (op >> 3) & 7; if (r != 6) { SetR8IX(r, Inc8(GetR8IX(r, isIX)), isIX); return; } }");
        w.Line("if ((op & 0xC7) == 0x05) { int r = (op >> 3) & 7; if (r != 6) { SetR8IX(r, Dec8(GetR8IX(r, isIX)), isIX); return; } }");
        w.Line("if ((op & 0xC7) == 0x06) { int r = (op >> 3) & 7; if (r != 6) { SetR8IX(r, n, isIX); return; } }");
        w.Open("if (op >= 0x40 && op <= 0xBF)");
        w.Line("int dst = (op >> 3) & 7; int src = op & 7;");
        w.Line("if ((op & 0x80) == 0) { SetR8IX(dst, GetR8IX(src, isIX), isIX); return; }");
        w.Line("Alu((op >> 3) & 7, GetR8IX(src, isIX));");
        w.Line("return;");
        w.Close();
        w.Line("UnknownOpcode(op, LastPC);");
        w.Close();
        w.Blank();
    }

    private static void EmitExecPrefixedCb(EmitWriter w)
    {
        w.Open("public static void ExecPrefixedCB(int prefix, sbyte d, byte op)");
        w.Line("TStates += DdCbT[op];");
        w.Line("bool isIX = prefix == 0xDD || prefix == 0xDDCB;");
        w.Line("ushort address = (ushort)((isIX ? IX : IY) + d);");
        w.Line("int kind = op >> 3; int reg = op & 7;");
        w.Line("if (kind < 8) { byte v = Memory.Read(address); v = RotateCB(kind, v); Memory.Write(address, v); if (reg != 6) SetR8(reg, v); return; }");
        w.Line("if (kind < 16) { byte v = Memory.Read(address); BitValue(v, kind - 8); return; }");
        w.Line("if (kind < 24) { int bit = kind - 16; byte v = Memory.Read(address); v = (byte)(v & ~(1 << bit)); Memory.Write(address, v); if (reg != 6) SetR8(reg, v); return; }");
        w.Line("int setBit = kind - 24; byte sv = Memory.Read(address); sv = (byte)(sv | (1 << setBit)); Memory.Write(address, sv); if (reg != 6) SetR8(reg, sv);");
        w.Close();
        w.Blank();
    }

    private static void EmitDynamicExecutor(EmitWriter w)
    {
        w.Open("public static int GetLength(ushort pc)");
        w.Line("byte op = Memory.Read(pc);");
        w.Line("if (op == 0xCB) return 2;");
        w.Line("if (op == 0xED) { byte op2 = Memory.Read((ushort)(pc + 1)); return EDLen[op2]; }");
        w.Open("if (op == 0xDD || op == 0xFD)");
        w.Line("byte op2 = Memory.Read((ushort)(pc + 1));");
        w.Line("if (op2 == 0xCB) return 4;");
        w.Line("if (op2 == 0xDD || op2 == 0xFD || op2 == 0xED) return 1;");
        w.Line("if (UsesIXYDisp[op2] != 0) return (op2 == 0x36) ? 4 : 3;");
        w.Line("return BaseLen[op2] + 1;");
        w.Close();
        w.Line("return BaseLen[op];");
        w.Close();
        w.Blank();

        w.Open("public static bool ExecDynamicOne(ushort pc)");
        w.Line("byte op = Memory.Read(pc);");
        w.Line("if (op == 0xCB) { ExecCB(Memory.Read((ushort)(pc + 1))); DispatchTarget = (ushort)(pc + 2); return true; }");
        w.Open("if (op == 0xED)");
        w.Line("byte ed = Memory.Read((ushort)(pc + 1));");
        w.Line("int len = EDLen[ed];");
        w.Line("ushort nn = len == 4 ? Memory.Read16((ushort)(pc + 2)) : (ushort)0;");
        w.Line("if ((ed & 0xC7) == 0x45) { TStates += 14; DispatchTarget = Pop16(); IFF1 = IFF2; return true; }");
        w.Line("ExecEDNormal(ed, nn);");
        w.Line("DispatchTarget = (ushort)(pc + len);");
        w.Line("return true;");
        w.Close();
        w.Open("if (op == 0xDD || op == 0xFD)");
        w.Line("byte op2 = Memory.Read((ushort)(pc + 1));");
        w.Line("if (op2 == 0xDD || op2 == 0xFD || op2 == 0xED) { TStates += 4; DispatchTarget = (ushort)(pc + 1); return true; }");
        w.Line("if (op2 == 0xCB) { sbyte d = (sbyte)Memory.Read((ushort)(pc + 2)); byte cb = Memory.Read((ushort)(pc + 3)); ExecPrefixedCB(op, d, cb); DispatchTarget = (ushort)(pc + 4); return true; }");
        w.Line("if (op2 == 0xE9) { TStates += 8; ushort ixy = (op == 0xDD) ? IX : IY; NoteDynamicTarget(ixy); DispatchTarget = ixy; return true; }");
        w.Line("int len = GetLength(pc);");
        w.Line("sbyte d2 = 0; ushort nn2 = 0;");
        w.Line("if (UsesIXYDisp[op2] != 0) { d2 = (sbyte)Memory.Read((ushort)(pc + 2)); if (len == 4) nn2 = Memory.Read((ushort)(pc + 3)); }");
        w.Line("else if (len >= 3) { if (BaseLen[op2] == 3 || op2 == 0x21 || op2 == 0x22 || op2 == 0x2A || op2 == 0x2B) nn2 = Memory.Read16((ushort)(pc + 2)); else nn2 = Memory.Read((ushort)(pc + 2)); }");
        w.Line("ExecPrefixed(op, op2, d2, nn2);");
        w.Line("DispatchTarget = (ushort)(pc + len);");
        w.Line("return true;");
        w.Close();
        w.Blank();
        w.Line("int blen = BaseLen[op];");
        w.Line("ushort operand = blen >= 2 ? Memory.Read((ushort)(pc + 1)) : (ushort)0;");
        w.Line("if (blen == 3) operand |= (ushort)(Memory.Read((ushort)(pc + 2)) << 8);");
        w.Open("switch (op)");
        w.Line("case 0xC3: TStates += 10; DispatchTarget = operand; return true;");
        w.Line("case 0xC2: case 0xCA: case 0xD2: case 0xDA: case 0xE2: case 0xEA: case 0xF2: case 0xFA:");
        w.Line("    TStates += 10; DispatchTarget = Conditional((op >> 3) & 7) ? operand : (ushort)(pc + 3); return true;");
        w.Line("case 0x18: TStates += 12; DispatchTarget = (ushort)(pc + 2 + (sbyte)operand); return true;");
        w.Line("case 0x10: TStates += 13; B = (byte)(B - 1); DispatchTarget = B != 0 ? (ushort)(pc + 2 + (sbyte)operand) : (ushort)(pc + 2); return true;");
        w.Line("case 0x20: case 0x28: case 0x30: case 0x38:");
        w.Line("    TStates += 12; DispatchTarget = Conditional((op >> 3) & 3) ? (ushort)(pc + 2 + (sbyte)operand) : (ushort)(pc + 2); return true;");
        w.Line("case 0xCD: TStates += 17; Push16((ushort)(pc + 3)); DispatchTarget = operand; return true;");
        w.Line("case 0xC4: case 0xCC: case 0xD4: case 0xDC: case 0xE4: case 0xEC: case 0xF4: case 0xFC:");
        w.Line("    TStates += 17; if (Conditional((op >> 3) & 7)) { Push16((ushort)(pc + 3)); DispatchTarget = operand; } else DispatchTarget = (ushort)(pc + 3); return true;");
        w.Line("case 0xC9: TStates += 10; { ushort t = Pop16(); NoteDynamicTarget(t); DispatchTarget = t; return true; }");
        w.Line("case 0xC0: case 0xC8: case 0xD0: case 0xD8: case 0xE0: case 0xE8: case 0xF0: case 0xF8:");
        w.Line("    TStates += 11; DispatchTarget = Conditional((op >> 3) & 7) ? Pop16() : (ushort)(pc + 1); return true;");
        w.Line("case 0xC7: case 0xCF: case 0xD7: case 0xDF: case 0xE7: case 0xEF: case 0xF7: case 0xFF:");
        w.Line("    TStates += 11; Push16((ushort)(pc + 1)); DispatchTarget = (ushort)(op & 0x38); return true;");
        w.Line("case 0xE9: TStates += 4; NoteDynamicTarget(HL); DispatchTarget = HL; return true;");
        w.Line("case 0x76: TStates += 4; Halt(); DispatchTarget = (ushort)(pc + 1); return true;");
        w.Close();
        w.Line("ExecBase(op, operand);");
        w.Line("DispatchTarget = (ushort)(pc + blen);");
        w.Line("return true;");
        w.Close();
        w.Blank();
    }
}
