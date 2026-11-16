namespace ZX2ILRecomp.Core;

/// <summary>
/// Static Z80 instruction geometry: length in bytes and T-state cost per
/// opcode, for the base map and each prefix page.
///
/// These tables are shared by three consumers and must never diverge:
/// the disassembler (how many bytes to consume), the lifter (how far the
/// fall-through edge goes) and the generated runtime (how much time to charge).
/// That is why they live in one place and are serialised verbatim into the
/// emitted C# instead of being re-derived there.
/// </summary>
public static class CpuZ80
{
    /// <summary>Total T-states in one 50 Hz ZX Spectrum frame (69888).</summary>
    public const int FrameTStates = 69888;

    /// <summary>Z80 clock of every Spectrum model (3.5 MHz).</summary>
    public const int ClockHz = 3500000;

    /// <summary>Screen refresh rate of a PAL Spectrum.</summary>
    public const double FrameHz = 50.08;

    /// <summary>Bytes consumed by an unprefixed opcode.</summary>
    public static readonly byte[] BaseLen = new byte[256];

    /// <summary>Bytes consumed by an <c>ED</c>-prefixed opcode (prefix included).</summary>
    public static readonly byte[] EDLen = new byte[256];

    /// <summary>1 when the opcode takes an <c>(IX+d)</c> / <c>(IY+d)</c> operand.</summary>
    public static readonly byte[] UsesIXYDisp = new byte[256];

    /// <summary>T-states for an unprefixed opcode.</summary>
    public static readonly byte[] BaseT = new byte[256];

    /// <summary>T-states for a <c>CB</c>-prefixed opcode.</summary>
    public static readonly byte[] CbT = new byte[256];

    /// <summary>T-states for an <c>ED</c>-prefixed opcode.</summary>
    public static readonly byte[] EdT = new byte[256];

    /// <summary>T-states for a <c>DD</c>/<c>FD</c>-prefixed opcode.</summary>
    public static readonly byte[] PfT = new byte[256];

    /// <summary>T-states for a <c>DD CB</c> / <c>FD CB</c> opcode.</summary>
    public static readonly byte[] DdCbT = new byte[256];

    static CpuZ80()
    {
        Init();
    }

    private static void Init()
    {
        for (int i = 0; i < 256; i++)
        {
            BaseLen[i] = 1;
            EDLen[i] = 2;
            UsesIXYDisp[i] = 0;
            BaseT[i] = 4;
            CbT[i] = 8;
            EdT[i] = 8;
            PfT[i] = 8;
            DdCbT[i] = 23;
        }

        // --- Lengths -----------------------------------------------------
        byte[] len3 =
        {
            0x01, 0x11, 0x21, 0x31, 0x22, 0x2A, 0x32, 0x3A,
            0xC2, 0xC3, 0xC4, 0xCA, 0xCC, 0xCD, 0xD2, 0xDA,
            0xDC, 0xE2, 0xEA, 0xEC, 0xF2, 0xFA, 0xFC
        };

        byte[] len2 =
        {
            0x06, 0x0E, 0x16, 0x1E, 0x26, 0x2E, 0x36, 0x3E,
            0x10, 0x18, 0x20, 0x28, 0x30, 0x38,
            0xC6, 0xCE, 0xD3, 0xD6, 0xDB, 0xDE, 0xE6, 0xEE, 0xF6, 0xFE
        };

        foreach (byte op in len3)
        {
            BaseLen[op] = 3;
        }

        foreach (byte op in len2)
        {
            BaseLen[op] = 2;
        }

        byte[] ed4 = { 0x43, 0x4B, 0x53, 0x5B, 0x63, 0x6B, 0x73, 0x7B };

        foreach (byte op in ed4)
        {
            EDLen[op] = 4;
        }

        // --- (IX+d) forms ------------------------------------------------
        byte[] ixy =
        {
            0x34, 0x35, 0x36, 0x46, 0x4E, 0x56, 0x5E, 0x66, 0x6E,
            0x70, 0x71, 0x72, 0x73, 0x74, 0x75, 0x77, 0x7E,
            0x86, 0x8E, 0x96, 0x9E, 0xA6, 0xAE, 0xB6, 0xBE
        };

        foreach (byte op in ixy)
        {
            UsesIXYDisp[op] = 1;
        }

        // --- Base timings -------------------------------------------------
        byte[] t7 =
        {
            0x46, 0x4E, 0x56, 0x5E, 0x66, 0x6E, 0x70, 0x71, 0x72, 0x73, 0x74, 0x75, 0x77, 0x7E,
            0x02, 0x0A, 0x12, 0x1A, 0x3A, 0x3E, 0x06, 0x0E, 0x16, 0x1E, 0x26, 0x2E,
            0xC6, 0xCE, 0xD6, 0xDE, 0xE6, 0xEE, 0xF6, 0xFE
        };

        foreach (byte op in t7)
        {
            BaseT[op] = 7;
        }

        byte[] t10 =
        {
            0x01, 0x11, 0x21, 0x31, 0x36,
            0xC2, 0xC3, 0xCA, 0xD2, 0xDA, 0xE2, 0xEA, 0xF2, 0xFA,
            0x18, 0x20, 0x28, 0x30, 0x38,
            0xC1, 0xD1, 0xE1, 0xF1, 0xC9,
            0x32, 0x22, 0x2A
        };

        foreach (byte op in t10)
        {
            BaseT[op] = 10;
        }

        byte[] t11 =
        {
            0x09, 0x19, 0x29, 0x39,
            0xC5, 0xD5, 0xE5, 0xF5,
            0xC0, 0xC8, 0xD0, 0xD8, 0xE0, 0xE8, 0xF0, 0xF8,
            0xC7, 0xCF, 0xD7, 0xDF, 0xE7, 0xEF, 0xF7, 0xFF,
            0xD3, 0xDB, 0x34, 0x35
        };

        foreach (byte op in t11)
        {
            BaseT[op] = 11;
        }

        byte[] t6 = { 0x03, 0x0B, 0x13, 0x1B, 0x23, 0x2B, 0x33, 0x3B, 0xF9 };

        foreach (byte op in t6)
        {
            BaseT[op] = 6;
        }

        byte[] t12 = { 0x10, 0x18, 0x20, 0x28, 0x30, 0x38 };

        foreach (byte op in t12)
        {
            BaseT[op] = 12;
        }

        byte[] t13 = { 0x10, 0x32, 0x3A };

        foreach (byte op in t13)
        {
            BaseT[op] = 13;
        }

        BaseT[0x22] = 16;
        BaseT[0x2A] = 16;

        byte[] t17 = { 0xC4, 0xCC, 0xCD, 0xD4, 0xDC, 0xE4, 0xEC, 0xF4, 0xFC };

        foreach (byte op in t17)
        {
            BaseT[op] = 17;
        }

        BaseT[0xE3] = 19;

        // Prefixes are not instructions on their own.
        BaseT[0xCB] = 0;
        BaseT[0xDD] = 0;
        BaseT[0xED] = 0;
        BaseT[0xFD] = 0;

        // --- CB timings ---------------------------------------------------
        for (int i = 0; i < 256; i++)
        {
            int kind = i >> 3;
            int reg = i & 7;

            CbT[i] = kind switch
            {
                < 8 => (byte)(reg == 6 ? 16 : 8),
                < 16 => (byte)(reg == 6 ? 12 : 8),
                _ => (byte)(reg == 6 ? 15 : 8)
            };
        }

        // --- ED timings ---------------------------------------------------
        for (int i = 0x40; i <= 0x7F; i++)
        {
            if ((i & 0xC7) == 0x40 || (i & 0xC7) == 0x41)
            {
                EdT[i] = 12;
            }
        }

        EdT[0x47] = 9;
        EdT[0x4F] = 9;
        EdT[0x57] = 9;
        EdT[0x5F] = 9;

        for (int i = 0x45; i <= 0x7D; i += 8)
        {
            EdT[i] = 14;
        }

        for (int i = 0x42; i <= 0x72; i += 8)
        {
            EdT[i] = 15;
        }

        for (int i = 0x4A; i <= 0x7A; i += 8)
        {
            EdT[i] = 15;
        }

        for (int i = 0x43; i <= 0x73; i += 8)
        {
            EdT[i] = 20;
        }

        for (int i = 0x4B; i <= 0x7B; i += 8)
        {
            EdT[i] = 20;
        }

        EdT[0x67] = 18;
        EdT[0x6F] = 18;

        for (int i = 0xA0; i <= 0xBF; i++)
        {
            EdT[i] = 16;
        }

        // --- DD/FD timings -------------------------------------------------
        for (int i = 0; i < 256; i++)
        {
            PfT[i] = (byte)(BaseT[i] + 4);
        }

        byte[] pf19 =
        {
            0x46, 0x4E, 0x56, 0x5E, 0x66, 0x6E,
            0x70, 0x71, 0x72, 0x73, 0x74, 0x75, 0x77, 0x7E,
            0x86, 0x8E, 0x96, 0x9E, 0xA6, 0xAE, 0xB6, 0xBE, 0x36
        };

        foreach (byte op in pf19)
        {
            PfT[op] = 19;
        }

        PfT[0x34] = 23;
        PfT[0x35] = 23;
        PfT[0x21] = 14;
        PfT[0x22] = 20;
        PfT[0x2A] = 20;
        PfT[0x23] = 10;
        PfT[0x2B] = 10;
        PfT[0x24] = 8;
        PfT[0x25] = 8;
        PfT[0x26] = 11;
        PfT[0x2C] = 8;
        PfT[0x2D] = 8;
        PfT[0x2E] = 11;
        PfT[0xE1] = 14;
        PfT[0xE5] = 15;
        PfT[0xE3] = 23;
        PfT[0xF9] = 10;
        PfT[0xE9] = 8;

        byte[] pf15 = { 0x09, 0x19, 0x29, 0x39 };

        foreach (byte op in pf15)
        {
            PfT[op] = 15;
        }

        for (int i = 0; i < 256; i++)
        {
            int k = i >> 3;
            DdCbT[i] = (byte)(k >= 8 && k < 16 ? 20 : 23);
        }
    }

    /// <summary>Total instruction length for the byte at <paramref name="pc"/>.</summary>
    public static int LengthOf(ReadOnlySpan<byte> memory, int pc)
    {
        byte op = memory[pc];

        if (op == 0xCB)
        {
            return 2;
        }

        if (op == 0xED)
        {
            return EDLen[memory[(pc + 1) & 0xFFFF]];
        }

        if (op == 0xDD || op == 0xFD)
        {
            byte op2 = memory[(pc + 1) & 0xFFFF];

            if (op2 == 0xCB)
            {
                return 4;
            }

            if (op2 == 0xDD || op2 == 0xFD || op2 == 0xED)
            {
                return 1;
            }

            if (UsesIXYDisp[op2] != 0)
            {
                return op2 == 0x36 ? 4 : 3;
            }

            return BaseLen[op2] + 1;
        }

        return BaseLen[op];
    }
}
