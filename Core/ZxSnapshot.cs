using System.Text;
using ZX2ILRecomp.Common;

namespace ZX2ILRecomp.Core;

/// <summary>Which Spectrum family a snapshot came from.</summary>
public enum SpectrumModel
{
    /// <summary>16K/48K: single ROM, one 48K RAM space, no paging.</summary>
    Zx48 = 48,

    /// <summary>128K / +2: two ROMs, eight 16K RAM banks, paging via port 0x7FFD.</summary>
    Zx128 = 128
}

/// <summary>
/// A ZX Spectrum machine state: registers, memory banks, paging latches and the
/// (optional) ROM image.
///
/// Memory layout is bank-oriented rather than flat, because that is the only
/// representation that survives 128K paging:
///
/// <code>
///   0x0000-0x3FFF  Rom0 / Rom1        (selected by port 0x7FFD bit 4)
///   0x4000-0x7FFF  RamBanks[5]        (fixed screen bank)
///   0x8000-0xBFFF  RamBanks[2]        (fixed, never paged on a 128K)
///   0xC000-0xFFFF  RamBanks[bank]     (bank = 0 on 48K, port 0x7FFD &amp; 7 on 128K)
/// </code>
///
/// The 48K "bank number" of each 16K slice is not 0/1/2 but 5/2/0 — that is the
/// mapping ULA+ and most snapshots use, and it keeps 48K and 128K code paths
/// identical.
/// </summary>
public sealed class ZxSnapshot
{
    public const int PageSize = 16384;
    public const int BankCount = 8;

    /// <summary>48K or 128K. Everything downstream compares numerically.</summary>
    public int Model = (int)SpectrumModel.Zx48;

    public byte A, F, B, C, D, E, H, L;
    public byte A2, F2, B2, C2, D2, E2, H2, L2;
    public ushort IX, IY, SP, PC;
    public byte I, R;
    public bool IFF1, IFF2;
    public int IM = 1;
    public byte Border = 7;

    /// <summary>Value last written to port 0x7FFD (128K paging latch).</summary>
    public byte Port7FFD;

    /// <summary>Value last written to port 0x1FFD (+2A/+3 extended latch).</summary>
    public byte Port1FFD;

    public byte[] Rom0 = new byte[PageSize];
    public byte[] Rom1 = new byte[PageSize];

    /// <summary>Power-on contents of each RAM bank, 16K each.</summary>
    public byte[][] RamBanks = CreateBanks();

    /// <summary>Address execution starts at.</summary>
    public ushort Entry;

    /// <summary>True when a real ROM image is present, so vectors are executable code.</summary>
    public bool HasRomCode;

    /// <summary>Human-readable notes about how this snapshot was interpreted.</summary>
    public List<string> Notes { get; } = new();

    /// <summary>Container format the snapshot was read from (<c>.z80</c>, <c>.sna</c>, ...).</summary>
    public string Format { get; set; } = string.Empty;

    private static byte[][] CreateBanks()
    {
        byte[][] banks = new byte[BankCount][];

        for (int i = 0; i < BankCount; i++)
        {
            banks[i] = new byte[PageSize];
        }

        return banks;
    }

    /// <summary>Reads any supported container based on its extension.</summary>
    public static ZxSnapshot Load(string path)
    {
        string ext = Path.GetExtension(path).ToLowerInvariant();
        byte[] data = File.ReadAllBytes(path);

        ZxSnapshot snapshot = ext switch
        {
            ".z80" => LoadZ80(data),
            ".sna" => LoadSna(data),
            ".tap" => TapeLoader.LoadTap(data),
            ".szx" => LoadSzx(data),
            ".tzx" => TapeLoader.LoadTzx(data),
            _ => throw new InvalidDataException("Unsupported snapshot extension: " + ext)
        };

        snapshot.Format = ext;
        return snapshot;
    }

    // -----------------------------------------------------------------
    // .z80
    // -----------------------------------------------------------------

    private static ushort Get16(byte[] data, int position)
    {
        if (position < 0 || position + 1 >= data.Length)
        {
            return 0;
        }

        return (ushort)(data[position] | (data[position + 1] << 8));
    }

    private static int Get32(byte[] data, int position)
    {
        if (position < 0 || position + 3 >= data.Length)
        {
            return 0;
        }

        return (int)((uint)data[position] |
                     ((uint)data[position + 1] << 8) |
                     ((uint)data[position + 2] << 16) |
                     ((uint)data[position + 3] << 24));
    }

    private static ZxSnapshot LoadZ80(byte[] data)
    {
        if (data.Length < 30)
        {
            throw new InvalidDataException("Z80 file too short (need at least 30 bytes).");
        }

        ZxSnapshot s = new();

        s.A = data[0];
        s.F = data[1];
        s.C = data[2];
        s.B = data[3];

        // .z80 v1 stores L,H then E,D — reading them as HL then DE is the
        // classic off-by-one-pair bug that silently swaps HL and DE.
        s.L = data[4];
        s.H = data[5];
        s.E = data[6];
        s.D = data[7];

        ushort pc = Get16(data, 8);
        s.SP = Get16(data, 10);
        s.I = data[12];
        s.R = data[13];

        byte flags = data[14];
        s.Border = (byte)((flags >> 1) & 7);

        if ((flags & 1) != 0)
        {
            s.R |= 0x80;
        }

        s.E2 = data[15];
        s.D2 = data[16];
        s.C2 = data[17];
        s.B2 = data[18];
        s.L2 = data[19];
        s.H2 = data[20];
        s.F2 = data[21];
        s.A2 = data[22];

        s.IY = Get16(data, 23);
        s.IX = Get16(data, 25);
        s.IFF1 = data[27] != 0;
        s.IFF2 = data[28] != 0;
        s.IM = data[29] & 3;

        if (pc != 0)
        {
            // Version 1: a single 48K page follows the 30-byte header.
            s.PC = pc;
            s.Model = (int)SpectrumModel.Zx48;
            bool compressed = (flags & 0x20) != 0;
            s.SetRam48(DecompressZ80V1(data, 30, compressed));
            s.Notes.Add(compressed ? "Z80 v1, compressed 48K block" : "Z80 v1, uncompressed 48K block");
        }
        else
        {
            // Version 2/3: extended header, then a sequence of page blocks.
            int position = 30;
            int headerLength = Get16(data, position);
            position += 2;

            if (position + headerLength > data.Length)
            {
                throw new InvalidDataException("Z80 v2/v3 extended header runs past end of file.");
            }

            pc = Get16(data, position);
            byte hardware = data[position + 2];
            s.PC = pc;

            // 23-byte header = v2, longer = v3. Hardware ids 3..5 are 128K machines.
            if (headerLength == 23)
            {
                s.Model = hardware is >= 3 and <= 5 ? (int)SpectrumModel.Zx128 : (int)SpectrumModel.Zx48;
            }
            else
            {
                s.Model = (int)SpectrumModel.Zx128;
            }

            if (headerLength >= 4)
            {
                s.Port7FFD = data[position + 3];
            }

            if (headerLength >= 5)
            {
                s.Port1FFD = data[position + 4];
            }

            position += headerLength;
            LoadZ80Pages(s, data, position);
            s.Notes.Add("Z80 v2/v3, " + (headerLength == 23 ? "23" : headerLength + "-byte") +
                        " extended header, hardware id " + hardware);
        }

        s.Entry = s.PC;

        if (s.Entry == 0)
        {
            s.Entry = 0x8000;
        }

        s.HasRomCode = !AllZero(s.Rom0) || !AllZero(s.Rom1);
        return s;
    }

    /// <summary>
    /// Expands the v1 run-length encoding: <c>ED ED count value</c> repeats
    /// <paramref name="value"/> <paramref name="count"/> times; a count of 0
    /// terminates the stream.
    /// </summary>
    private static byte[] DecompressZ80V1(byte[] data, int position, bool compressed)
    {
        byte[] ram = new byte[49152];
        int output = 0;

        if (!compressed)
        {
            int length = Math.Min(ram.Length, data.Length - position);

            if (length > 0)
            {
                Array.Copy(data, position, ram, 0, length);
            }

            return ram;
        }

        int i = position;

        while (output < ram.Length && i < data.Length)
        {
            if (i + 3 < data.Length && data[i] == 0xED && data[i + 1] == 0xED)
            {
                int count = data[i + 2];
                byte value = data[i + 3];
                i += 4;

                if (count == 0)
                {
                    break;
                }

                for (int k = 0; k < count && output < ram.Length; k++)
                {
                    ram[output++] = value;
                }
            }
            else
            {
                ram[output++] = data[i++];
            }
        }

        return ram;
    }

    private static void LoadZ80Pages(ZxSnapshot s, byte[] data, int position)
    {
        while (position + 3 <= data.Length)
        {
            int length = Get16(data, position);
            position += 2;

            byte page = data[position];
            position++;

            int blockSize = length == 0 ? PageSize : length;

            if (position + blockSize > data.Length)
            {
                break;
            }

            MapZ80Page(s, page, DecompressZ80Block(data, position, blockSize));
            position += blockSize;
        }
    }

    private static byte[] DecompressZ80Block(byte[] data, int position, int size)
    {
        byte[] output = new byte[PageSize];
        int o = 0;
        int end = position + size;
        int i = position;

        if (size == PageSize)
        {
            Array.Copy(data, position, output, 0, PageSize);
            return output;
        }

        while (o < output.Length && i < end)
        {
            if (i + 3 < end && data[i] == 0xED && data[i + 1] == 0xED)
            {
                int count = data[i + 2];
                byte value = data[i + 3];
                i += 4;

                if (count == 0)
                {
                    break;
                }

                for (int k = 0; k < count && o < output.Length; k++)
                {
                    output[o++] = value;
                }
            }
            else
            {
                output[o++] = data[i++];
            }
        }

        return output;
    }

    /// <summary>
    /// Maps a .z80 page number onto a physical bank. Pages 3,4,5,6,7,8,9,10 are
    /// RAM banks 0..7 with the well-known 4→2 and 5→5 renumbering; pages 0 and 2
    /// are the two ROMs.
    /// </summary>
    private static void MapZ80Page(ZxSnapshot s, byte page, byte[] block)
    {
        switch (page)
        {
            case 4:
                s.RamBanks[2] = block;
                break;
            case 5:
                s.RamBanks[5] = block;
                break;
            case 8:
                s.RamBanks[0] = block;
                break;
            case >= 3 and <= 10:
                s.RamBanks[page - 3] = block;
                break;
            case 0 or 2:
                s.Rom0 = block;
                break;
            case 1 or 3:
                s.Rom1 = block;
                break;
        }
    }

    // -----------------------------------------------------------------
    // .sna
    // -----------------------------------------------------------------

    private static ZxSnapshot LoadSna(byte[] data)
    {
        if (data.Length < 27 + 49152)
        {
            throw new InvalidDataException("SNA file too short (need 27 + 49152 bytes).");
        }

        ZxSnapshot s = new()
        {
            Model = (int)SpectrumModel.Zx48
        };

        s.I = data[0];

        s.L2 = data[1];
        s.H2 = data[2];
        s.E2 = data[3];
        s.D2 = data[4];
        s.C2 = data[5];
        s.B2 = data[6];
        s.F2 = data[7];
        s.A2 = data[8];

        s.L = data[9];
        s.H = data[10];
        s.E = data[11];
        s.D = data[12];
        s.C = data[13];
        s.B = data[14];

        s.IY = Get16(data, 15);
        s.IX = Get16(data, 17);

        byte iff = data[19];
        s.IFF1 = (iff & 4) != 0;
        s.IFF2 = s.IFF1;

        s.R = data[20];
        s.F = data[21];
        s.A = data[22];
        s.SP = Get16(data, 23);
        s.IM = data[25] & 3;
        s.Border = (byte)(data[26] & 7);

        byte[] ram = new byte[49152];
        Array.Copy(data, 27, ram, 0, 49152);
        s.SetRam48(ram);

        // A plain .sna does not store PC: it was pushed onto the stack, so it is
        // the top word of the saved 48K image at SP.
        byte[] flat = s.GetFlat();

        if (s.SP < 0xFFFF)
        {
            s.PC = Get16(flat, s.SP);
            s.SP = (ushort)(s.SP + 2);
        }
        else
        {
            s.PC = 0x8000;
        }

        if (data.Length > 27 + 49152)
        {
            int position = 27 + 49152;

            if (position + 4 <= data.Length)
            {
                int extraLength = Get16(data, position);
                position += 2;

                // 128K extension: PC, port 0x7FFD, then banked pages.
                if (extraLength >= 4 && position + 4 <= data.Length)
                {
                    s.PC = Get16(data, position);
                    position += 2;
                    s.Port7FFD = data[position];
                    s.Model = (int)SpectrumModel.Zx128;
                    s.Notes.Add("SNA 128K extension present");
                }
            }
        }

        s.Entry = s.PC == 0 ? (ushort)0x8000 : s.PC;
        s.HasRomCode = !AllZero(s.Rom0) || !AllZero(s.Rom1);
        return s;
    }

    // -----------------------------------------------------------------
    // .szx
    // -----------------------------------------------------------------

    private static ZxSnapshot LoadSzx(byte[] data)
    {
        if (data.Length < 8)
        {
            throw new InvalidDataException("SZX file too short.");
        }

        string signature = Encoding.ASCII.GetString(data, 0, 4);

        if (signature != "ZXST")
        {
            throw new InvalidDataException("Bad SZX signature (expected \"ZXST\").");
        }

        ZxSnapshot s = new();
        int position = 8;

        while (position + 8 <= data.Length)
        {
            string id = Encoding.ASCII.GetString(data, position, 4);
            int blockLength = BitConverter.ToInt32(data, position + 4);
            position += 8;

            if (blockLength < 0 || position + blockLength > data.Length)
            {
                break;
            }

            switch (id)
            {
                case "Z80R" when blockLength >= 17:
                    {
                        ushort pc = Get16(data, position + 15);

                        if (pc != 0)
                        {
                            s.PC = pc;
                        }

                        s.I = data[position + 0];
                        s.F = data[position + 3];
                        s.A = data[position + 4];
                        s.SP = Get16(data, position + 8);
                        s.IM = data[position + 10] & 3;
                        s.Border = (byte)(data[position + 11] & 7);
                        s.Port7FFD = data[position + 12];
                        s.Port1FFD = data[position + 13];
                        s.IFF1 = (data[position + 2] & 1) != 0;
                        s.IFF2 = s.IFF1;
                        s.Model = (int)SpectrumModel.Zx128;
                        break;
                    }

                case "RAMP" when blockLength >= 16386:
                    {
                        // RAMP layout: page number, page flags, then 16K of data.
                        byte page = data[position];
                        byte[] block = new byte[PageSize];
                        int copy = Math.Min(PageSize, blockLength - 2);
                        Array.Copy(data, position + 2, block, 0, copy);

                        if (page < BankCount)
                        {
                            s.RamBanks[page] = block;
                        }

                        break;
                    }

                case "ROMR" when blockLength >= 8:
                    {
                        byte which = data[position + 1];
                        int copy = Math.Min(PageSize, blockLength - 8);
                        byte[] target = which == 0 ? s.Rom0 : s.Rom1;
                        Array.Copy(data, position + 8, target, 0, copy);
                        break;
                    }
            }

            position += blockLength;
        }

        if (s.PC == 0)
        {
            s.PC = 0x8000;
        }

        s.Entry = s.PC;
        s.HasRomCode = !AllZero(s.Rom0) || !AllZero(s.Rom1);
        s.Notes.Add("SZX container");
        return s;
    }

    // -----------------------------------------------------------------
    // Memory helpers
    // -----------------------------------------------------------------

    /// <summary>Installs a 48K image into the canonical 5/2/0 bank layout.</summary>
    private void SetRam48(byte[] ram48)
    {
        if (ram48.Length < 49152)
        {
            return;
        }

        Array.Copy(ram48, 0, RamBanks[5], 0, PageSize);
        Array.Copy(ram48, PageSize, RamBanks[2], 0, PageSize);
        Array.Copy(ram48, PageSize * 2, RamBanks[0], 0, PageSize);
    }

    /// <summary>Installs a flat 64K image (tape loaders produce these).</summary>
    internal void SetFlatRam(byte[] flat)
    {
        Array.Copy(flat, 0x4000, RamBanks[5], 0, PageSize);
        Array.Copy(flat, 0x8000, RamBanks[2], 0, PageSize);
        Array.Copy(flat, 0xC000, RamBanks[0], 0, PageSize);
    }

    /// <summary>Flattens the currently paged 64K address space.</summary>
    public byte[] GetFlat()
    {
        byte[] memory = new byte[65536];

        if (Model >= (int)SpectrumModel.Zx128 && (Port7FFD & 0x10) != 0)
        {
            Array.Copy(Rom1, 0, memory, 0, PageSize);
        }
        else
        {
            Array.Copy(Rom0, 0, memory, 0, PageSize);
        }

        Array.Copy(RamBanks[5], 0, memory, 0x4000, PageSize);
        Array.Copy(RamBanks[2], 0, memory, 0x8000, PageSize);

        int bank = Model >= (int)SpectrumModel.Zx128 ? Port7FFD & 7 : 0;
        Array.Copy(RamBanks[bank], 0, memory, 0xC000, PageSize);

        return memory;
    }

    /// <summary>
    /// Installs a minimal stand-in ROM for snapshots that carry no ROM image.
    ///
    /// Every <c>RST</c> vector becomes <c>RET</c>, and the IM 1 handler at
    /// 0x0038 bumps the system variable FRAMES (0x5C78), re-enables interrupts
    /// and returns. That is enough for the vast majority of games, which only
    /// need interrupts to advance a frame counter, and it keeps HALT from
    /// falling into a field of NOPs after the first interrupt.
    ///
    /// The <c>EI</c> before the <c>RET</c> is not optional: taking an interrupt
    /// clears <c>IFF1</c>, and the real 48K handler ends with <c>EI; RET</c> for
    /// exactly that reason. Without it, the machine services one interrupt and
    /// then sits in <c>HALT</c> forever — which looks like "the game freezes on
    /// frame 1".
    /// </summary>
    public void InstallMinimalRom()
    {
        Rom0[0x00] = 0xC9;
        Rom0[0x08] = 0xC9;
        Rom0[0x10] = 0xC9;
        Rom0[0x18] = 0xC9;
        Rom0[0x20] = 0xC9;
        Rom0[0x28] = 0xC9;
        Rom0[0x30] = 0xC9;

        byte[] im1 =
        {
            0xF5,             // PUSH AF
            0xE5,             // PUSH HL
            0x2A, 0x78, 0x5C, // LD HL,(5C78)
            0x23,             // INC HL
            0x22, 0x78, 0x5C, // LD (5C78),HL
            0xE1,             // POP HL
            0xF1,             // POP AF
            0xFB,             // EI
            0xC9              // RET
        };

        Array.Copy(im1, 0, Rom0, 0x38, im1.Length);
        HasRomCode = true;
        Notes.Add("Installed minimal pseudo-ROM (RST = RET, IM1 = FRAMES++)");
    }

    public static bool AllZero(byte[]? data)
    {
        if (data is null)
        {
            return true;
        }

        foreach (byte b in data)
        {
            if (b != 0)
            {
                return false;
            }
        }

        return true;
    }
}
