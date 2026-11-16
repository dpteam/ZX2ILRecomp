using System.Globalization;
using System.Text;

namespace ZX2ILRecomp.Core;

/// <summary>
/// Tape image loading for <c>.tap</c> and <c>.tzx</c>.
///
/// A tape is not a snapshot: it carries no registers, no ROM and no stack. What
/// it does carry is a sequence of named blocks, and the entry point is *not*
/// one of them — it lives in the BASIC loader as <c>RANDOMIZE USR n</c>.
///
/// That distinction matters more than it looks. A typical commercial tape is:
///
/// <code>
///   program block   "ManicMiner"   78 bytes   10 CLEAR 30000 / LOAD ""CODE
///                                             20 RANDOMIZE USR 33792
///   CODE block      "mmm"          256 bytes  load 0x8000
///   CODE block      "mm1"          32768 b    load 0x8000
/// </code>
///
/// The load address is 0x8000, but the game actually starts at 0x8400 — the
/// first 1K is data tables. Starting at the load address runs the data, which
/// looks like "it loads and then does nothing". So the loader:
///
/// 1. scans the BASIC program block for <c>RANDOMIZE USR n</c> and uses <c>n</c>;
/// 2. falls back to a CODE block's start address (parameter 1);
/// 3. then to its load address (parameter 2);
/// 4. then to 0x8000.
/// </summary>
public static class TapeLoader
{
    /// <summary>Tokenised BASIC: the marker that precedes a numeric literal.</summary>
    private const byte NumberMarker = 0x0E;

    /// <summary>Tokenised BASIC: <c>USR</c>.</summary>
    private const byte TokenUsr = 0xC0;

    /// <summary>Header type 3: a machine-code block.</summary>
    private const byte HeaderCode = 3;

    /// <summary>Header type 0: a BASIC program.</summary>
    private const byte HeaderProgram = 0;

    /// <summary>Accumulates what a tape tells us about its entry point.</summary>
    private sealed class EntryCandidates
    {
        /// <summary>From <c>RANDOMIZE USR n</c> in the BASIC loader. Best signal.</summary>
        public ushort? BasicUsr;

        /// <summary>CODE block start address (parameter 1).</summary>
        public ushort? CodeStart;

        /// <summary>CODE block load address (parameter 2).</summary>
        public ushort? CodeLoad;

        public ushort Resolve()
        {
            if (BasicUsr is ushort usr && usr >= 0x4000)
            {
                return usr;
            }

            if (CodeStart is ushort start && start >= 0x4000)
            {
                return start;
            }

            if (CodeLoad is ushort load && load >= 0x4000)
            {
                return load;
            }

            return 0x8000;
        }

        /// <summary>Which candidate won, for the analysis report.</summary>
        public string Describe(ushort resolved)
        {
            if (BasicUsr is ushort usr && usr == resolved)
            {
                return "BASIC RANDOMIZE USR " + usr.ToString(CultureInfo.InvariantCulture);
            }

            if (CodeStart is ushort start && start == resolved)
            {
                return "CODE block start address";
            }

            if (CodeLoad is ushort load && load == resolved)
            {
                return "CODE block load address";
            }

            return "default 0x8000";
        }
    }

    // -----------------------------------------------------------------
    // .tap
    // -----------------------------------------------------------------

    public static ZxSnapshot LoadTap(byte[] data)
    {
        ZxSnapshot snapshot = new()
        {
            Model = (int)SpectrumModel.Zx48
        };

        byte[] flat = new byte[65536];
        EntryCandidates entry = new();
        int pendingStart = -1;
        int pendingLength = -1;
        int position = 0;

        while (position + 2 <= data.Length)
        {
            int blockLength = (data[position] | (data[position + 1] << 8));
            position += 2;

            if (blockLength <= 0 || position + blockLength > data.Length)
            {
                break;
            }

            byte flag = data[position];

            if (flag == 0x00 && blockLength >= 19)
            {
                byte[] header = new byte[blockLength];
                Array.Copy(data, position, header, 0, blockLength);
                ApplyHeader(header, entry, ref pendingStart, ref pendingLength);
            }
            else if (flag == 0xFF && pendingStart >= 0)
            {
                CopyBlock(data, position + 1, blockLength - 2, pendingStart, pendingLength, flat, entry);
                pendingStart = -1;
            }

            position += blockLength;
        }

        return Finish(snapshot, flat, entry, "TAP");
    }

    // -----------------------------------------------------------------
    // .tzx
    // -----------------------------------------------------------------

    public static ZxSnapshot LoadTzx(byte[] data)
    {
        ZxSnapshot snapshot = new()
        {
            Model = (int)SpectrumModel.Zx48
        };

        if (data.Length < 10)
        {
            throw new InvalidDataException("TZX file too short.");
        }

        byte[] flat = new byte[65536];
        EntryCandidates entry = new();
        int pendingStart = -1;
        int pendingLength = -1;

        // "ZXTape!" 0x1A major minor — 10 bytes.
        int position = 10;

        while (position < data.Length)
        {
            byte id = data[position];
            position++;

            int count;
            int total;

            switch (id)
            {
                case 0x10: // standard speed data
                case 0x11: // turbo speed data
                    {
                        int headerSize = id == 0x10 ? 4 : 18;

                        if (position + headerSize > data.Length)
                        {
                            return Finish(snapshot, flat, entry, "TZX");
                        }

                        total = id == 0x10
                            ? (data[position + 2] | (data[position + 3] << 8))
                            : (data[position + 15] | (data[position + 16] << 8) | (data[position + 17] << 16));

                        position += headerSize;

                        if (total < 0 || position + total > data.Length)
                        {
                            return Finish(snapshot, flat, entry, "TZX");
                        }

                        byte[] block = new byte[total];
                        Array.Copy(data, position, block, 0, total);
                        position += total;

                        if (block.Length > 0 && block[0] == 0x00 && block.Length >= 19)
                        {
                            ApplyHeader(block, entry, ref pendingStart, ref pendingLength);
                        }
                        else if (block.Length > 0 && block[0] == 0xFF && pendingStart >= 0)
                        {
                            CopyBlock(block, 1, block.Length - 2, pendingStart, pendingLength, flat, entry);
                            pendingStart = -1;
                        }

                        break;
                    }

                case 0x12: // pure tone
                    position += 4;
                    break;

                case 0x13: // pulse sequence
                    if (position + 1 > data.Length)
                    {
                        return Finish(snapshot, flat, entry, "TZX");
                    }

                    count = data[position];
                    position += 1 + count * 2;
                    break;

                case 0x14: // pure data
                    {
                        if (position + 10 > data.Length)
                        {
                            return Finish(snapshot, flat, entry, "TZX");
                        }

                        total = data[position + 7] | (data[position + 8] << 8) | (data[position + 9] << 16);
                        position += 10;

                        if (total < 0 || position + total > data.Length)
                        {
                            return Finish(snapshot, flat, entry, "TZX");
                        }

                        if (pendingStart >= 0)
                        {
                            CopyBlock(data, position, total, pendingStart, pendingLength, flat, entry);
                            pendingStart = -1;
                        }
                        else
                        {
                            Log.Warn("TZX pure-data block (0x14) without a preceding header; skipped.");
                        }

                        position += total;
                        break;
                    }

                case 0x15: // direct recording (audio, not memory)
                    if (position + 8 > data.Length)
                    {
                        return Finish(snapshot, flat, entry, "TZX");
                    }

                    total = data[position + 4] | (data[position + 5] << 8) | (data[position + 6] << 16);
                    position += 8 + total;
                    Log.Warn("TZX direct-recording block (0x15) skipped: it is audio, not memory.");
                    break;

                case 0x18: // CSW recording
                case 0x19: // generalized data
                    if (position + 4 > data.Length)
                    {
                        return Finish(snapshot, flat, entry, "TZX");
                    }

                    total = BitConverter.ToInt32(data, position);
                    position += 4 + total;
                    break;

                case 0x20: // pause
                    position += 2;
                    break;

                case 0x21: // group start
                    if (position + 1 > data.Length)
                    {
                        return Finish(snapshot, flat, entry, "TZX");
                    }

                    count = data[position];
                    position += 1 + count;
                    break;

                case 0x22: // group end
                    break;

                case 0x23: // jump to block
                case 0x24: // loop start
                    position += 2;
                    break;

                case 0x25: // loop end
                    break;

                case 0x26: // call sequence
                    if (position + 2 > data.Length)
                    {
                        return Finish(snapshot, flat, entry, "TZX");
                    }

                    count = data[position] | (data[position + 1] << 8);
                    position += 2 + count * 2;
                    break;

                case 0x27: // return from sequence
                    break;

                case 0x28: // select block
                    if (position + 2 > data.Length)
                    {
                        return Finish(snapshot, flat, entry, "TZX");
                    }

                    count = data[position] | (data[position + 1] << 8);
                    position += 2 + count;
                    break;

                case 0x2A: // stop if 48K
                    position += 4;
                    break;

                case 0x2B: // set signal level
                    position += 5;
                    break;

                case 0x30: // text description
                    if (position + 1 > data.Length)
                    {
                        return Finish(snapshot, flat, entry, "TZX");
                    }

                    count = data[position];
                    position += 1 + count;
                    break;

                case 0x31: // message block
                    if (position + 2 > data.Length)
                    {
                        return Finish(snapshot, flat, entry, "TZX");
                    }

                    count = data[position + 1];
                    position += 2 + count;
                    break;

                case 0x32: // archive info
                    if (position + 2 > data.Length)
                    {
                        return Finish(snapshot, flat, entry, "TZX");
                    }

                    count = data[position] | (data[position + 1] << 8);
                    position += 2 + count;
                    break;

                case 0x33: // hardware type
                    if (position + 1 > data.Length)
                    {
                        return Finish(snapshot, flat, entry, "TZX");
                    }

                    count = data[position];
                    position += 1 + count * 3;
                    break;

                case 0x35: // custom info
                    if (position + 20 > data.Length)
                    {
                        return Finish(snapshot, flat, entry, "TZX");
                    }

                    total = BitConverter.ToInt32(data, position + 16);
                    position += 20 + total;
                    break;

                case 0x5A: // glue block
                    position += 9;
                    break;

                default:
                    Log.Warn(string.Format(
                        CultureInfo.InvariantCulture,
                        "TZX unknown block id 0x{0:X2} at offset {1}; stopping parse.",
                        id,
                        position - 1));
                    return Finish(snapshot, flat, entry, "TZX");
            }

            if (position < 0 || position > data.Length)
            {
                break;
            }
        }

        return Finish(snapshot, flat, entry, "TZX");
    }

    // -----------------------------------------------------------------
    // Shared block handling
    // -----------------------------------------------------------------

    /// <summary>
    /// Interprets a 19-byte tape header: records where the following data block
    /// goes and, for a BASIC program, mines the entry point out of it.
    /// </summary>
    private static void ApplyHeader(
        byte[] header,
        EntryCandidates entry,
        ref int pendingStart,
        ref int pendingLength)
    {
        if (header.Length < 19)
        {
            pendingStart = -1;
            return;
        }

        byte type = header[1];
        int declaredLength = header[12] | (header[13] << 8);
        int param1 = header[14] | (header[15] << 8);
        int param2 = header[16] | (header[17] << 8);

        switch (type)
        {
            case HeaderCode:
                pendingStart = param2;
                pendingLength = declaredLength;

                // Parameter 1 is the auto-run address; parameter 2 is where the
                // bytes go. They differ more often than not.
                entry.CodeStart ??= param1 >= 0x4000 ? (ushort)param1 : null;
                entry.CodeLoad ??= param2 >= 0x4000 ? (ushort)param2 : null;
                break;

            case HeaderProgram:
                // A BASIC program always loads at 0x8000, behind the variables.
                pendingStart = 0x8000;
                pendingLength = declaredLength;
                break;

            default:
                pendingStart = -1;
                break;
        }
    }

    /// <summary>
    /// Copies a data block into the flat 64K image and, when it is a BASIC
    /// program, looks for the auto-start address inside it.
    /// </summary>
    private static void CopyBlock(
        byte[] source,
        int offset,
        int length,
        int start,
        int declaredLength,
        byte[] flat,
        EntryCandidates entry)
    {
        int copy = Math.Min(length, declaredLength);

        for (int i = 0; i < copy; i++)
        {
            int address = start + i;

            if (address is >= 0x4000 and < 0x10000 && offset + i < source.Length)
            {
                flat[address] = source[offset + i];
            }
        }

        // A BASIC program lands at 0x8000, so it can be scanned in place as
        // soon as it has been copied in.
        if (start == 0x8000 && copy >= 9)
        {
            if (TryFindBasicUsr(flat, copy, out ushort usr))
            {
                entry.BasicUsr ??= usr;
            }
        }
    }

    /// <summary>
    /// Finds the <c>RANDOMIZE USR n</c> call in a tokenised BASIC program.
    ///
    /// <c>USR</c> is token <c>0xC0</c>. Two number encodings appear in real
    /// tapes and both have to be handled:
    ///
    /// <code>
    ///   standard:  C0 0E "33792" 00     number marker, then five ASCII digits
    ///   variant:   C0 "33792" 0E 00     digits straight after USR
    /// </code>
    ///
    /// The second form is what several tape-to-TZX converters emit, and a
    /// loader that only understands the first silently misses the entry point —
    /// which shows up as "the game loads and then does nothing".
    /// </summary>
    private static bool TryFindBasicUsr(byte[] memory, int length, out ushort address)
    {
        address = 0;

        int limit = Math.Min(length, 0x8000);

        for (int i = 0; i + 6 < limit; i++)
        {
            if (memory[0x8000 + i] != TokenUsr)
            {
                continue;
            }

            // Standard form: USR, number marker, five digits.
            if (memory[0x8000 + i + 1] == NumberMarker &&
                TryParseBasicNumber(memory, 0x8000 + i + 2, limit - (i + 2), out int standard) &&
                standard is >= 0x4000 and <= 0xFFFF)
            {
                address = (ushort)standard;
                return true;
            }

            // Variant form: USR, then the digits directly.
            if (TryParseBasicNumber(memory, 0x8000 + i + 1, limit - (i + 1), out int variant) &&
                variant is >= 0x4000 and <= 0xFFFF)
            {
                address = (ushort)variant;
                return true;
            }
        }

        return false;
    }

    /// <summary>Reads five space-padded ASCII digits at <paramref name="offset"/>.</summary>
    private static bool TryParseBasicNumber(byte[] memory, int offset, int available, out int value)
    {
        value = 0;

        if (available < 5)
        {
            return false;
        }

        for (int digit = 0; digit < 5; digit++)
        {
            byte c = memory[offset + digit];

            if (c == ' ')
            {
                continue;
            }

            if (c is < (byte)'0' or > (byte)'9')
            {
                return false;
            }

            value = value * 10 + (c - '0');
        }

        return true;
    }

    private static ZxSnapshot Finish(ZxSnapshot snapshot, byte[] flat, EntryCandidates entry, string format)
    {
        ushort resolved = entry.Resolve();

        snapshot.SetFlatRam(flat);
        snapshot.PC = resolved;
        snapshot.Entry = resolved;
        snapshot.HasRomCode = false;

        if (entry.BasicUsr is ushort usr)
        {
            snapshot.Notes.Add("Entry point from the BASIC loader: RANDOMIZE USR " +
                               usr.ToString(CultureInfo.InvariantCulture));
        }
        else
        {
            snapshot.Notes.Add("Entry point from " + entry.Describe(resolved) + "; no BASIC loader found");
        }

        snapshot.Notes.Add(format + " is a tape image: memory only, no registers");
        return snapshot;
    }
}
