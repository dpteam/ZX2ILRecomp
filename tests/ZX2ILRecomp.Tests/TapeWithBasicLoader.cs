using System.Text;

namespace ZX2ILRecomp.Tests;

/// <summary>
/// Builds a tape image shaped like a real commercial release: a BASIC loader
/// that calls into a CODE block whose load address is *not* its entry point.
///
/// This is the case that a naive "start at the load address" loader gets wrong,
/// and it is common enough (Manic Miner, Jet Set Willy, most Ultimate titles)
/// that it deserves a permanent test rather than a manual check.
/// </summary>
internal static class TapeWithBasicLoader
{
    /// <summary>Where the CODE block loads.</summary>
    private const ushort LoadAddress = 0x8000;

    /// <summary>Where the game actually starts: 1K into the block.</summary>
    private const ushort EntryAddress = 0x8400;

    /// <summary>Writes the tape and returns its path.</summary>
    public static string Write(string directory)
    {
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "basic-loader.tap");
        File.WriteAllBytes(path, Build());
        return path;
    }

    private static byte[] Build()
    {
        List<byte> file = new();

        // ---- BASIC program block -------------------------------------
        // 10 CLEAR 32767 : LOAD "" CODE : RANDOMIZE USR 33792
        // Tokenised: 0x00 0x0A <line> <len> <tokens...> 0x0D
        List<byte> basic = new();

        // Line 10
        basic.Add(0x00);
        basic.Add(0x0A);
        basic.AddRange(ToUInt16(0));      // line length, patched below
        int lineStart = basic.Count;

        basic.Add(0xFD);                  // CLEAR
        basic.Add(0x0E);                  // number marker
        basic.AddRange(AsciiNumber(32767));
        basic.Add(0x00);                  // number terminator
        basic.Add(0x3A);                  // ':'

        basic.Add(0xEF);                  // LOAD
        basic.Add(0x22);                  // '"'
        basic.Add(0x22);                  // '"'  (empty name = next block)
        basic.Add(0xAF);                  // CODE
        basic.Add(0x3A);                  // ':'

        basic.Add(0xF9);                  // RANDOMIZE
        basic.Add(0xC0);                  // USR
        basic.Add(0x0E);                  // number marker
        basic.AddRange(AsciiNumber(EntryAddress));
        basic.Add(0x00);                  // number terminator
        basic.Add(0x0D);                  // end of line

        int lineLength = basic.Count - lineStart;
        basic[2] = (byte)(lineLength & 0xFF);
        basic[3] = (byte)((lineLength >> 8) & 0xFF);

        // A TAP tape is a *pair* of blocks per file: a 19-byte header block
        // (flag 0x00) followed by a data block (flag 0xFF). Emitting one
        // combined block is a mistake the loader correctly refuses to read.
        file.AddRange(HeaderBlock(0x00, "loader    ", basic.Count, 0x000A, basic.Count));
        file.AddRange(DataBlock(basic.ToArray()));

        // ---- CODE block ----------------------------------------------
        byte[] code = new byte[0x800];     // 2K: data at 0x8000, code at 0x8400
        code[0x000] = 0xAA;               // data at the load address
        code[0x400] = 0xF3;               // DI at the real entry point
        code[0x401] = 0x31;               // LD SP,0x9CFE
        code[0x402] = 0xFE;
        code[0x403] = 0x9C;

        file.AddRange(HeaderBlock(0x03, "game      ", code.Length, EntryAddress, LoadAddress));
        file.AddRange(DataBlock(code));

        return file.ToArray();
    }

    /// <summary>Builds a 19-byte TAP header block: flag, 17-byte header, checksum.</summary>
    private static byte[] HeaderBlock(byte type, string name, int length, int param1, int param2)
    {
        List<byte> block = new() { 0x00, type };

        for (int i = 0; i < 10; i++)
        {
            block.Add(i < name.Length ? (byte)name[i] : (byte)0x20);
        }

        block.AddRange(ToUInt16(length));
        block.AddRange(ToUInt16(param1));
        block.AddRange(ToUInt16(param2));
        block.Add(0x00);                  // checksum: the loader ignores it

        return LengthPrefixed(block);
    }

    /// <summary>Builds a data block: flag 0xFF, payload, checksum.</summary>
    private static byte[] DataBlock(byte[] data)
    {
        List<byte> block = new() { 0xFF };
        block.AddRange(data);
        block.Add(0x00);                  // checksum

        return LengthPrefixed(block);
    }

    private static byte[] LengthPrefixed(List<byte> block)
    {
        List<byte> result = new();
        result.AddRange(ToUInt16(block.Count));
        result.AddRange(block);
        return result.ToArray();
    }

    /// <summary>
    /// Tokenised BASIC stores numbers as five space-padded ASCII digits. The
    /// value must fit in five characters, which is why this takes an int and
    /// formats it, instead of padding an arbitrary string.
    /// </summary>
    private static byte[] AsciiNumber(int value)
    {
        string digits = value.ToString(System.Globalization.CultureInfo.InvariantCulture).PadLeft(5, ' ');

        if (digits.Length != 5)
        {
            throw new ArgumentOutOfRangeException(nameof(value), "BASIC numbers are five characters wide.");
        }

        byte[] result = new byte[5];

        for (int i = 0; i < 5; i++)
        {
            result[i] = (byte)digits[i];
        }

        return result;
    }

    private static byte[] ToUInt16(int value)
    {
        return new[] { (byte)(value & 0xFF), (byte)((value >> 8) & 0xFF) };
    }
}
