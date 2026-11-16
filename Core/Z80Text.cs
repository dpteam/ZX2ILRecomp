using System.Globalization;

namespace ZX2ILRecomp.Core;

/// <summary>
/// Turns a decoded instruction into Zilog-style mnemonic text.
///
/// The lifter puts this above every generated method, so the emitted C# reads
/// like annotated assembly instead of a wall of <c>ExecBase(0x3C, 0x0000)</c>.
/// It is deliberately not a full reassembler: only the forms the decoder
/// produces are covered, and unknown bytes come out as <c>DEFB</c>.
/// </summary>
public static class Z80Text
{
    private static readonly string[] R8 = { "B", "C", "D", "E", "H", "L", "(HL)", "A" };
    private static readonly string[] Rp = { "BC", "DE", "HL", "SP" };
    private static readonly string[] Rp2 = { "BC", "DE", "HL", "AF" };
    private static readonly string[] Cc = { "NZ", "Z", "NC", "C", "PO", "PE", "P", "M" };
    private static readonly string[] Alu = { "ADD A,", "ADC A,", "SUB ", "SBC A,", "AND ", "XOR ", "OR ", "CP " };

    /// <summary>Formats one instruction. <paramref name="flat"/> supplies displacement bytes.</summary>
    public static string Format(InstructionZ80 inst, byte[] flat)
    {
        int pc = inst.Address;

        return inst.Prefix switch
        {
            0xCB => FormatCb(inst),
            0xED => FormatEd(inst),
            0xDD or 0xFD => FormatIndex(inst, inst.Prefix == 0xDD ? "IX" : "IY"),
            0xDDCB => FormatIndexCb(inst, "IX"),
            0xFDCB => FormatIndexCb(inst, "IY"),
            _ => FormatBase(inst, flat)
        };
    }

    private static string Hex8(int value) => "0x" + value.ToString("X2", CultureInfo.InvariantCulture);

    private static string Hex16(int value) => "0x" + value.ToString("X4", CultureInfo.InvariantCulture);

    private static string SignedHex8(int value)
    {
        return value >= 0 ? "+" + Hex8(value) : "-" + Hex8(-value);
    }

    private static string FormatBase(InstructionZ80 inst, byte[] flat)
    {
        byte op = inst.Opcode;
        int operand = inst.Operand;
        int pc = inst.Address;
        int next = pc + inst.Length;

        switch (op)
        {
            case 0x00: return "NOP";
            case 0x07: return "RLCA";
            case 0x0F: return "RRCA";
            case 0x17: return "RLA";
            case 0x1F: return "RRA";
            case 0x27: return "DAA";
            case 0x2F: return "CPL";
            case 0x37: return "SCF";
            case 0x3F: return "CCF";
            case 0x76: return "HALT";
            case 0xC9: return "RET";
            case 0xE9: return "JP (HL)";
            case 0xD9: return "EXX";
            case 0x08: return "EX AF,AF'";
            case 0xEB: return "EX DE,HL";
            case 0xE3: return "EX (SP),HL";
            case 0xF3: return "DI";
            case 0xFB: return "EI";
            case 0xF9: return "LD SP,HL";
            case 0x01: return "LD BC," + Hex16(operand);
            case 0x11: return "LD DE," + Hex16(operand);
            case 0x21: return "LD HL," + Hex16(operand);
            case 0x31: return "LD SP," + Hex16(operand);
            case 0x22: return "LD (" + Hex16(operand) + "),HL";
            case 0x2A: return "LD HL,(" + Hex16(operand) + ")";
            case 0x32: return "LD (" + Hex16(operand) + "),A";
            case 0x3A: return "LD A,(" + Hex16(operand) + ")";
            case 0x02: return "LD (BC),A";
            case 0x12: return "LD (DE),A";
            case 0x0A: return "LD A,(BC)";
            case 0x1A: return "LD A,(DE)";
            case 0xD3: return "OUT (" + Hex8(operand) + "),A";
            case 0xDB: return "IN A,(" + Hex8(operand) + ")";
            case 0x10: return "DJNZ " + Hex16(next + (sbyte)operand);
            case 0x18: return "JR " + Hex16(next + (sbyte)operand);
            case 0xC3: return "JP " + Hex16(operand);
            case 0xCD: return "CALL " + Hex16(operand);
        }

        if ((op & 0xC7) == 0x04)
        {
            return "INC " + R8[(op >> 3) & 7];
        }

        if ((op & 0xC7) == 0x05)
        {
            return "DEC " + R8[(op >> 3) & 7];
        }

        if ((op & 0xC7) == 0x06)
        {
            return "LD " + R8[(op >> 3) & 7] + "," + Hex8(operand);
        }

        if ((op & 0xCF) == 0x03)
        {
            return "INC " + Rp[(op >> 4) & 3];
        }

        if ((op & 0xCF) == 0x0B)
        {
            return "DEC " + Rp[(op >> 4) & 3];
        }

        if ((op & 0xCF) == 0x09)
        {
            return "ADD HL," + Rp[(op >> 4) & 3];
        }

        if ((op & 0xC7) == 0xC5)
        {
            return "PUSH " + Rp2[(op >> 4) & 3];
        }

        if ((op & 0xC7) == 0xC1)
        {
            return "POP " + Rp2[(op >> 4) & 3];
        }

        if ((op & 0xC7) == 0xC6)
        {
            return Alu[(op >> 3) & 7] + Hex8(operand);
        }

        if (op is >= 0x40 and <= 0x7F)
        {
            return "LD " + R8[(op >> 3) & 7] + "," + R8[op & 7];
        }

        if (op is >= 0x80 and <= 0xBF)
        {
            return Alu[(op >> 3) & 7] + R8[op & 7];
        }

        if ((op & 0xE7) == 0xC2)
        {
            return "JP " + Cc[(op >> 3) & 7] + "," + Hex16(operand);
        }

        if ((op & 0xE7) == 0xC4)
        {
            return "CALL " + Cc[(op >> 3) & 7] + "," + Hex16(operand);
        }

        if ((op & 0xE7) == 0xC0)
        {
            return "RET " + Cc[(op >> 3) & 7];
        }

        if ((op & 0xE7) == 0xC7)
        {
            return "RST " + Hex8(op & 0x38);
        }

        if ((op & 0xE7) == 0x20)
        {
            return "JR " + Cc[(op >> 3) & 3] + "," + Hex16(next + (sbyte)operand);
        }

        return "DEFB " + Hex8(op);
    }

    private static string FormatCb(InstructionZ80 inst)
    {
        byte op = inst.Opcode;
        int kind = op >> 3;
        int reg = op & 7;

        string target = R8[reg];

        if (kind < 8)
        {
            string[] names = { "RLC", "RRC", "RL", "RR", "SLA", "SRA", "SLL", "SRL" };
            return names[kind] + " " + target;
        }

        if (kind < 16)
        {
            return "BIT " + (kind - 8) + "," + target;
        }

        if (kind < 24)
        {
            return "RES " + (kind - 16) + "," + target;
        }

        return "SET " + (kind - 24) + "," + target;
    }

    private static string FormatEd(InstructionZ80 inst)
    {
        byte op = inst.Opcode;

        if (op is >= 0x40 and <= 0x7F)
        {
            int group = (op >> 3) & 7;
            int reg = op & 7;

            switch (group)
            {
                case 0: return "IN " + (reg == 6 ? "F" : R8[reg]) + ",(C)";
                case 1: return "OUT (C)," + (reg == 6 ? "0" : R8[reg]);
                case 2: return "SBC HL," + Rp[(op >> 4) & 3];
                case 3: return "ADC HL," + Rp[(op >> 4) & 3];
                case 4: return "LD (" + Hex16(inst.Operand) + ")," + Rp[(op >> 4) & 3];
                case 5: return "LD " + Rp[(op >> 4) & 3] + ",(" + Hex16(inst.Operand) + ")";
                case 6: return "IM " + ((op >> 3) & 3);
                case 7:
                    return op switch
                    {
                        0x47 => "LD I,A",
                        0x4F => "LD R,A",
                        0x57 => "LD A,I",
                        0x5F => "LD A,R",
                        0x67 => "RRD",
                        0x6F => "RLD",
                        _ => "ED " + Hex8(op)
                    };
            }
        }

        return op switch
        {
            0x44 or 0x4C or 0x54 or 0x5C or 0x64 or 0x6C or 0x74 or 0x7C => "NEG",
            0x45 or 0x4D or 0x55 or 0x5D or 0x65 or 0x6D or 0x75 or 0x7D => "RETN",
            0x4A or 0x5A or 0x6A or 0x7A => "IN F,(C)",
            0xA0 => "LDI",
            0xA1 => "CPI",
            0xA2 => "INI",
            0xA3 => "OUTI",
            0xA8 => "LDD",
            0xA9 => "CPD",
            0xAA => "IND",
            0xAB => "OUTD",
            0xB0 => "LDIR",
            0xB1 => "CPIR",
            0xB2 => "INIR",
            0xB3 => "OTIR",
            0xB8 => "LDDR",
            0xB9 => "CPDR",
            0xBA => "INDR",
            0xBB => "OTDR",
            _ => "ED " + Hex8(op)
        };
    }

    private static string FormatIndex(InstructionZ80 inst, string index)
    {
        byte op = inst.Opcode;
        int displacement = inst.Displacement;
        int operand = inst.Operand;

        string indexed = "(" + index + SignedHex8(displacement) + ")";

        if (CpuZ80.UsesIXYDisp[op] != 0)
        {
            if (op == 0x34)
            {
                return "INC " + indexed;
            }

            if (op == 0x35)
            {
                return "DEC " + indexed;
            }

            if (op == 0x36)
            {
                return "LD " + indexed + "," + Hex8(operand);
            }

            if (op is >= 0x70 and <= 0x77)
            {
                return "LD " + indexed + "," + R8[op & 7];
            }

            if (op is >= 0x40 and <= 0x7F)
            {
                return "LD " + R8[(op >> 3) & 7] + "," + indexed;
            }

            if (op is >= 0x80 and <= 0xBF)
            {
                return Alu[(op >> 3) & 7] + indexed;
            }
        }

        return op switch
        {
            0x21 => "LD " + index + "," + Hex16(operand),
            0x22 => "LD (" + Hex16(operand) + ")," + index,
            0x2A => "LD " + index + ",(" + Hex16(operand) + ")",
            0x23 => "INC " + index,
            0x2B => "DEC " + index,
            0x24 => "INC " + index + "H",
            0x25 => "DEC " + index + "H",
            0x26 => "LD " + index + "H," + Hex8(operand),
            0x2C => "INC " + index + "L",
            0x2D => "DEC " + index + "L",
            0x2E => "LD " + index + "L," + Hex8(operand),
            0xE1 => "POP " + index,
            0xE5 => "PUSH " + index,
            0xE3 => "EX (SP)," + index,
            0xE9 => "JP (" + index + ")",
            0xF9 => "LD SP," + index,
            0x09 => "ADD " + index + ",BC",
            0x19 => "ADD " + index + ",DE",
            0x29 => "ADD " + index + "," + index,
            0x39 => "ADD " + index + ",SP",
            0x76 => "HALT",
            _ => "DD " + Hex8(op)
        };
    }

    private static string FormatIndexCb(InstructionZ80 inst, string index)
    {
        byte op = inst.Opcode;
        int kind = op >> 3;
        int reg = op & 7;

        string indexed = "(" + index + SignedHex8(inst.Displacement) + ")";

        if (kind < 8)
        {
            string[] names = { "RLC", "RRC", "RL", "RR", "SLA", "SRA", "SLL", "SRL" };
            return names[kind] + " " + indexed + (reg != 6 ? "," + R8[reg] : string.Empty);
        }

        if (kind < 16)
        {
            return "BIT " + (kind - 8) + "," + indexed;
        }

        if (kind < 24)
        {
            return "RES " + (kind - 16) + "," + indexed + (reg != 6 ? "," + R8[reg] : string.Empty);
        }

        return "SET " + (kind - 24) + "," + indexed + (reg != 6 ? "," + R8[reg] : string.Empty);
    }
}
