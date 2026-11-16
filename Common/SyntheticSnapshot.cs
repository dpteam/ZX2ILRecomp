using System.Globalization;
using System.Text;
using ZX2ILRecomp.Core;

namespace ZX2ILRecomp.Common;

/// <summary>
/// Builds tiny, valid ZX Spectrum snapshots for the self-test.
///
/// The point is not to emulate a game but to exercise every loader and every
/// code path that a real lift depends on: the container parser, the bank
/// mapping, the disassembler, the lifter, the SDK packaging step and a real
/// headless execution of the result.
///
/// The embedded program is deliberately boring, but it is not minimal: it
/// touches **every** runtime entry point, because a method that is only reached
/// by real games is a method the self-test cannot protect. Concretely:
///
/// <code>
///   0x8000  LD SP,0x7FF0     stack works
///   0x8003  LD A,0           known start value
///   0x8005  EI               interrupts armed
///   0x8006  INC A            &lt;- loop (base opcode path)
///   0x8007  CB 3F SRL A      CB page      -> ExecCB
///   0x8009  ED 44 NEG        ED page      -> ExecEDNormal
///   0x800B  ED A1 CPI        ED block op  -> Block
///   0x800D  DD 21 00 80      DD page      -> ExecPrefixed
///   0x8011  DD 7E 00         DD + (IX+d)  -> ExecPrefixed indexed
///   0x8014  FD CB 01 46      FD CB page   -> ExecPrefixedCB
///   0x8018  OUT (0xFE),A     ULA port
///   0x801A  LD (0x4000),0xFF bitmap write -> ULA decoding
///   0x801F  LD (0x5800),0x47 attribute    -> ink/paper/bright
///   0x8024  HALT             IM 1 through 0x0038
///   0x8025  JP 0x8006        loop
/// </code>
///
/// The two memory writes exist so a headless screenshot has something to show:
/// a broken ULA decoder produces a uniform picture, which the self-test can
/// detect without a reference image.
/// </summary>
/// A snapshot that lifts but traps anywhere in that chain fails here, with no
/// game files required.
/// </summary>
public static class SyntheticSnapshot
{
    /// <summary>One generated snapshot.</summary>
    public sealed record Spec(string Name, string Extension, int Model, string Description);

    private static readonly byte[] Program =
    {
        0x31, 0xF0, 0x7F,       // 0x8000: LD SP,0x7FF0
        0x3E, 0x00,             // 0x8003: LD A,0
        0xFB,                   // 0x8005: EI
        0x3C,                   // 0x8006: INC A          <- loop
        0xCB, 0x3F,             // 0x8007: SRL A          (CB page)
        0xED, 0x44,             // 0x8009: NEG            (ED page)
        0xED, 0xA1,             // 0x800B: CPI            (ED block op)
        0xDD, 0x21, 0x00, 0x80, // 0x800D: LD IX,0x8000   (DD page)
        0xDD, 0x7E, 0x00,       // 0x8011: LD A,(IX+0)    (DD indexed)
        0xFD, 0xCB, 0x01, 0x46, // 0x8014: BIT 0,(IY+1)   (FD CB page)
        0xD3, 0xFE,             // 0x8018: OUT (0xFE),A   (border + beeper)
        0x21, 0x00, 0x40,       // 0x801A: LD HL,0x4000   (bitmap base)
        0x36, 0xFF,             // 0x801D: LD (HL),0xFF   (one solid 8x8 cell)
        0x21, 0x00, 0x58,       // 0x801F: LD HL,0x5800   (attributes base)
        0x36, 0x47,             // 0x8022: LD (HL),0x47   (bright, white ink, black paper)
        0x76,                   // 0x8024: HALT           (IM 1 via 0x0038)
        0xC3, 0x06, 0x80        // 0x8025: JP 0x8006
    };

    private const ushort ProgramAddress = 0x8000;

    /// <summary>The matrix the self-test runs: one per supported container.</summary>
    public static IReadOnlyList<Spec> DefaultMatrix { get; } = new[]
    {
        new Spec("loop-48-sna", ".sna", 48, "48K SNA, uncompressed 48K block"),
        new Spec("loop-48-z80", ".z80", 48, "48K Z80 v1, uncompressed"),
        new Spec("loop-128-szx", ".szx", 128, "128K SZX with a RAMP block"),
        new Spec("loop-48-tap", ".tap", 48, "48K TAP with a CODE header and data block")
    };

    /// <summary>Writes the snapshot into <paramref name="directory"/> and returns its path.</summary>
    public static string Write(Spec spec, string directory)
    {
        Directory.CreateDirectory(directory);

        string path = Path.Combine(directory, spec.Name + spec.Extension);
        byte[] data = spec.Extension switch
        {
            ".sna" => BuildSna(),
            ".z80" => BuildZ80(),
            ".szx" => BuildSzx(),
            ".tap" => BuildTap(),
            _ => throw new InvalidOperationException("Unsupported synthetic format: " + spec.Extension)
        };

        File.WriteAllBytes(path, data);
        return path;
    }

    /// <summary>Builds a 48K SNA: 27-byte header, 48K of RAM, PC on the stack.</summary>
    private static byte[] BuildSna()
    {
        const int RamSize = 49152;
        byte[] data = new byte[27 + RamSize];

        data[0] = 0x3F;                 // I
        data[19] = 0x04;                // IFF1 (bit 2) -> interrupts enabled
        data[20] = 0x00;                // R
        data[21] = 0x00;                // F
        data[22] = 0x00;                // A
        data[25] = 0x01;                // IM 1
        data[26] = 0x07;                // border white

        ushort stack = 0x7FF0;
        WriteUInt16(data, 23, stack);

        byte[] flat = new byte[65536];
        Array.Copy(Program, 0, flat, ProgramAddress, Program.Length);

        // A .sna stores PC by pushing it: the loader pops it back at SP.
        WriteUInt16(flat, stack, ProgramAddress);
        Array.Copy(flat, 0x4000, data, 27, RamSize);

        return data;
    }

    /// <summary>Builds a Z80 v1 file: 30-byte header plus an uncompressed 48K block.</summary>
    private static byte[] BuildZ80()
    {
        const int RamSize = 49152;
        byte[] data = new byte[30 + RamSize];

        data[0] = 0x00;                 // A
        data[1] = 0x00;                 // F
        data[2] = 0x00;                 // C
        data[3] = 0x00;                 // B
        data[4] = 0x00;                 // L
        data[5] = 0x00;                 // H
        data[6] = 0x00;                 // E
        data[7] = 0x00;                 // D

        WriteUInt16(data, 8, ProgramAddress);   // PC (non-zero => v1)
        WriteUInt16(data, 10, 0x7FF0);          // SP
        data[12] = 0x3F;                        // I
        data[13] = 0x00;                        // R

        // bit 5 = 0: not compressed. Border bits 1-3.
        data[14] = (byte)(0x07 << 1);

        WriteUInt16(data, 23, 0x0000);          // IY
        WriteUInt16(data, 25, 0x0000);          // IX
        data[27] = 0x01;                        // IFF1
        data[28] = 0x01;                        // IFF2
        data[29] = 0x01;                        // IM 1

        byte[] flat = new byte[65536];
        Array.Copy(Program, 0, flat, ProgramAddress, Program.Length);
        Array.Copy(flat, 0x4000, data, 30, RamSize);

        return data;
    }

    /// <summary>Builds an SZX: ZXST header, one Z80R register block, one RAMP page.</summary>
    private static byte[] BuildSzx()
    {
        const int RegisterBlockLength = 37;

        List<byte> file = new();

        // "ZXST" + major + minor + machine id (3 = 128K) + flags.
        file.AddRange(Encoding.ASCII.GetBytes("ZXST"));
        file.Add(1);
        file.Add(5);
        file.Add(3);
        file.Add(0);

        // Z80R block.
        byte[] registers = new byte[RegisterBlockLength];
        registers[0] = 0x3F;                    // I
        registers[1] = 0x00;                    // F'
        registers[2] = 0x01;                    // IFF1/IFF2
        registers[3] = 0x00;                    // F
        registers[4] = 0x00;                    // A
        WriteUInt16(registers, 8, 0x7FF0);      // SP
        registers[10] = 0x01;                   // IM 1
        registers[11] = 0x07;                   // border
        registers[12] = 0x00;                   // port 0x7FFD: bank 0 at 0xC000
        registers[13] = 0x00;                   // port 0x1FFD
        WriteUInt16(registers, 15, ProgramAddress);

        file.AddRange(Encoding.ASCII.GetBytes("Z80R"));
        file.AddRange(BitConverter.GetBytes(RegisterBlockLength));
        file.AddRange(registers);

        // RAMP block for bank 2, which is the fixed 0x8000 page.
        byte[] page = new byte[16384];
        Array.Copy(Program, 0, page, 0, Program.Length);

        file.AddRange(Encoding.ASCII.GetBytes("RAMP"));
        file.AddRange(BitConverter.GetBytes(2 + page.Length));
        file.Add(2);            // page number, low byte
        file.Add(0xFF);         // page number, high byte (unused by the loader)
        file.AddRange(page);

        return file.ToArray();
    }

    /// <summary>Builds a TAP: a CODE header block followed by one data block.</summary>
    private static byte[] BuildTap()
    {
        List<byte> file = new();

        // Header block: flag 0x00, type 3 (CODE), 10-char name, length, param1, param2, checksum.
        List<byte> header = new() { 0x00, 0x03 };
        string name = "SYNTHETIC";

        for (int i = 0; i < 10; i++)
        {
            header.Add(i < name.Length ? (byte)name[i] : (byte)0x20);
        }

        header.AddRange(ToUInt16(Program.Length));
        header.AddRange(ToUInt16(ProgramAddress));   // param1: autostart line (unused for CODE)
        header.AddRange(ToUInt16(ProgramAddress));   // param2: load address
        header.Add(0x00);                            // checksum (the loader ignores it)

        file.AddRange(ToUInt16(header.Count));
        file.AddRange(header);

        // Data block: flag 0xFF, payload, checksum.
        List<byte> body = new() { 0xFF };
        body.AddRange(Program);
        body.Add(0x00);

        file.AddRange(ToUInt16(body.Count));
        file.AddRange(body);

        return file.ToArray();
    }

    private static byte[] ToUInt16(int value)
    {
        return new[] { (byte)(value & 0xFF), (byte)((value >> 8) & 0xFF) };
    }

    private static void WriteUInt16(byte[] buffer, int offset, int value)
    {
        buffer[offset] = (byte)(value & 0xFF);
        buffer[offset + 1] = (byte)((value >> 8) & 0xFF);
    }

    /// <summary>Describes a spec for the self-test banner.</summary>
    public static string Describe(Spec spec)
    {
        return spec.Name + " (" + spec.Extension.TrimStart('.').ToUpperInvariant() + ", " +
               spec.Model.ToString(CultureInfo.InvariantCulture) + "K)";
    }
}
