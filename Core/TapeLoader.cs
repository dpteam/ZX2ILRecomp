using System.Globalization;

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
/// looks like "it loads and then does nothing".
///
/// So blocks are collected first and placed afterwards, because two things can
/// only be decided once the whole tape has been read:
///
/// * **which header field is the load address** — real files disagree, see
///   <see cref="PlaceBlocks"/>;
/// * **where the entry point is** — it comes from the BASIC loader, and that
///   program may appear before or after the code it launches.
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

    /// <summary>
    /// Marks a data block that arrived with no header in front of it. Its load
    /// address is decided by the game's own loader at run time, so it cannot be
    /// placed statically.
    /// </summary>
    private const byte Unheadered = 0xFF;

    /// <summary>One data block plus the header that described it.</summary>
    private sealed record DataBlock(byte[] Bytes, int Offset, int Length, byte Type, int Param1, int Param2);

    /// <summary>Accumulates what a tape tells us about its entry point.</summary>
    private sealed class EntryCandidates
    {
        /// <summary>From <c>RANDOMIZE USR n</c> in the BASIC loader. Best signal.</summary>
        public ushort? BasicUsr;

        /// <summary>
        /// True when the tape's CODE headers turned out to store the load
        /// address first, contrary to the ROM's own order.
        /// </summary>
        public bool UsedSwappedConvention;

        /// <summary>True when the tape carries a BASIC program block.</summary>
        public bool HasBasicProgram;

        /// <summary>
        /// True when the resolved entry point lies inside a block that was
        /// actually placed in memory.
        /// </summary>
        public bool CoveredByBlock;


        public ushort Resolve()
        {
            return BasicUsr is ushort usr && usr >= 0x4000 ? usr : (ushort)0x8000;
        }
    }

    // -----------------------------------------------------------------
    // .tap
    // -----------------------------------------------------------------

    public static ZxSnapshot LoadTap(byte[] data)
    {
        List<DataBlock> blocks = new();
        EntryCandidates entry = new();
        int position = 0;

        // Header of the block the next data block belongs to.
        byte pendingType = 0;
        int pendingLength = -1;
        int pendingParam1 = 0;
        int pendingParam2 = 0;

        while (position + 2 <= data.Length)
        {
            int blockLength = data[position] | (data[position + 1] << 8);
            position += 2;

            if (blockLength <= 0 || position + blockLength > data.Length)
            {
                break;
            }

            byte flag = data[position];

            if (flag == 0x00 && blockLength >= 19)
            {
                ReadHeader(data, position, blockLength, out pendingType, out pendingLength, out pendingParam1, out pendingParam2);
            }
            else if (flag == 0xFF)
            {
                if (pendingLength >= 0)
                {
                    blocks.Add(new DataBlock(data, position + 1, blockLength - 2, pendingType, pendingParam1, pendingParam2));
                    pendingLength = -1;
                }
                else
                {
                    // A data block with no header in front of it. The game's own
                    // loader decides where these go, so they are recorded as
                    // unheadered rather than dropped silently.
                    blocks.Add(new DataBlock(data, position + 1, blockLength - 2, Unheadered, 0, 0));
                }
            }

            position += blockLength;
        }

        return Finish(blocks, entry, "TAP");
    }

    // -----------------------------------------------------------------
    // .tzx
    // -----------------------------------------------------------------

    public static ZxSnapshot LoadTzx(byte[] data)
    {
        if (data.Length < 10)
        {
            throw new InvalidDataException("TZX file too short.");
        }

        List<DataBlock> blocks = new();
        EntryCandidates entry = new();

        byte pendingType = 0;
        int pendingLength = -1;
        int pendingParam1 = 0;
        int pendingParam2 = 0;

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
                            return Finish(blocks, entry, "TZX");
                        }

                        total = id == 0x10
                            ? (data[position + 2] | (data[position + 3] << 8))
                            : (data[position + 15] | (data[position + 16] << 8) | (data[position + 17] << 16));

                        position += headerSize;

                        if (total < 0 || position + total > data.Length)
                        {
                            return Finish(blocks, entry, "TZX");
                        }

                        if (total > 0 && data[position] == 0x00 && total >= 19)
                        {
                            ReadHeader(data, position, total, out pendingType, out pendingLength, out pendingParam1, out pendingParam2);
                        }
                        else if (total > 0 && data[position] == 0xFF)
                        {
                            if (pendingLength >= 0)
                            {
                                blocks.Add(new DataBlock(data, position + 1, total - 2, pendingType, pendingParam1, pendingParam2));
                                pendingLength = -1;
                            }
                            else
                            {
                                blocks.Add(new DataBlock(data, position + 1, total - 2, Unheadered, 0, 0));
                            }
                        }

                        position += total;
                        break;
                    }

                case 0x12: // pure tone
                    position += 4;
                    break;

                case 0x13: // pulse sequence
                    if (position + 1 > data.Length)
                    {
                        return Finish(blocks, entry, "TZX");
                    }

                    count = data[position];
                    position += 1 + count * 2;
                    break;

                case 0x14: // pure data
                    {
                        if (position + 10 > data.Length)
                        {
                            return Finish(blocks, entry, "TZX");
                        }

                        total = data[position + 7] | (data[position + 8] << 8) | (data[position + 9] << 16);
                        position += 10;

                        if (total < 0 || position + total > data.Length)
                        {
                            return Finish(blocks, entry, "TZX");
                        }

                        if (pendingLength >= 0)
                        {
                            blocks.Add(new DataBlock(data, position, total, pendingType, pendingParam1, pendingParam2));
                            pendingLength = -1;
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
                        return Finish(blocks, entry, "TZX");
                    }

                    total = data[position + 4] | (data[position + 5] << 8) | (data[position + 6] << 16);
                    position += 8 + total;
                    Log.Warn("TZX direct-recording block (0x15) skipped: it is audio, not memory.");
                    break;

                case 0x18: // CSW recording
                case 0x19: // generalized data
                    if (position + 4 > data.Length)
                    {
                        return Finish(blocks, entry, "TZX");
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
                        return Finish(blocks, entry, "TZX");
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
                        return Finish(blocks, entry, "TZX");
                    }

                    count = data[position] | (data[position + 1] << 8);
                    position += 2 + count * 2;
                    break;

                case 0x27: // return from sequence
                    break;

                case 0x28: // select block
                    if (position + 2 > data.Length)
                    {
                        return Finish(blocks, entry, "TZX");
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
                        return Finish(blocks, entry, "TZX");
                    }

                    count = data[position];
                    position += 1 + count;
                    break;

                case 0x31: // message block
                    if (position + 2 > data.Length)
                    {
                        return Finish(blocks, entry, "TZX");
                    }

                    count = data[position + 1];
                    position += 2 + count;
                    break;

                case 0x32: // archive info
                    if (position + 2 > data.Length)
                    {
                        return Finish(blocks, entry, "TZX");
                    }

                    count = data[position] | (data[position + 1] << 8);
                    position += 2 + count;
                    break;

                case 0x33: // hardware type
                    if (position + 1 > data.Length)
                    {
                        return Finish(blocks, entry, "TZX");
                    }

                    count = data[position];
                    position += 1 + count * 3;
                    break;

                case 0x35: // custom info
                    if (position + 20 > data.Length)
                    {
                        return Finish(blocks, entry, "TZX");
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
                    return Finish(blocks, entry, "TZX");
            }

            if (position < 0 || position > data.Length)
            {
                break;
            }
        }

        return Finish(blocks, entry, "TZX");
    }

    // -----------------------------------------------------------------
    // Header and placement
    // -----------------------------------------------------------------

    /// <summary>Reads the 17-byte payload of a tape header.</summary>
    private static void ReadHeader(
        byte[] data,
        int offset,
        int available,
        out byte type,
        out int declaredLength,
        out int param1,
        out int param2)
    {
        type = 0;
        declaredLength = 0;
        param1 = 0;
        param2 = 0;

        if (available < 19)
        {
            declaredLength = -1;
            return;
        }

        type = data[offset + 1];
        declaredLength = data[offset + 12] | (data[offset + 13] << 8);
        param1 = data[offset + 14] | (data[offset + 15] << 8);
        param2 = data[offset + 16] | (data[offset + 17] << 8);
    }

    /// <summary>
    /// Places every collected block into a flat 64K image.
    ///
    /// The one genuinely ambiguous thing in a tape header is which of the two
    /// CODE parameters is the load address. The ROM saves "start, load", and
    /// several tape-to-TZX converters emit "load, start". A single-block game
    /// hides the difference; a multi-stage loader does not.
    ///
    /// Two worked examples, and they disagree:
    ///
    /// * Atic Atac: <c>p1 = 0x5B80, p2 = 0x8000</c>, loader calls
    ///   <c>USR 0x5B80</c>. Only <c>p1</c> covers the entry point.
    /// * Jet Set Willy: <c>p1 = 0x7FFE, p2 = 0x80FB</c>, loader calls
    ///   <c>USR 0x8400</c>. **Both** cover it, because the block is 32K long —
    ///   so "which one covers the entry" cannot decide.
    ///
    /// The tie-breaker is block alignment. A load address is where a block
    /// starts in memory and is almost always a round number: `0x8000`, `0x4000`,
    /// `0x6000`. A start address is a jump target and is arbitrary: `0x80FB`.
    /// Jet Set Willy's real load address is `0x7FFE`, one byte below `0x8000`,
    /// because the block begins with two bytes of length prefix.
    ///
    /// So: prefer the convention whose addresses look like load addresses, and
    /// fall back to "covers the entry point" when neither is convincing.
    /// </summary>
    private static byte[] PlaceBlocks(List<DataBlock> blocks, EntryCandidates entry)
    {
        byte[] flat = new byte[65536];

        if (blocks.Count == 0)
        {
            return flat;
        }

        // The BASIC program has to be in place before the entry point can be
        // read out of it, and its own address is not in dispute.
        foreach (DataBlock block in blocks)
        {
            if (block.Type == HeaderProgram)
            {
                entry.HasBasicProgram = true;
                CopyBlock(block, 0x8000, flat, entry);
            }
        }

        ushort wanted = entry.Resolve();
        bool param1IsLoad = ChooseConvention(blocks, wanted);
        entry.UsedSwappedConvention = param1IsLoad;

        entry.CoveredByBlock = blocks.Any(b => b.Type == HeaderCode && Covers(b, wanted, param1IsLoad));

        foreach (DataBlock block in blocks)
        {
            if (block.Type == HeaderProgram)
            {
                continue;
            }

            if (block.Type != HeaderCode)
            {
                // Arrays and other types are not code; skip them rather than
                // splattering them into the address space.
                continue;
            }

            int start = param1IsLoad ? block.Param1 : block.Param2;

            if (start is >= 0x4000 and <= 0xFFFF)
            {
                CopyBlock(block, start, flat, entry);
            }
        }

        return flat;
    }

    /// <summary>
    /// Decides which header field is the load address. See <see cref="PlaceBlocks"/>.
    /// </summary>
    private static bool ChooseConvention(List<DataBlock> blocks, ushort entryPoint)
    {
        List<DataBlock> code = blocks.Where(b => b.Type == HeaderCode).ToList();

        if (code.Count == 0)
        {
            return false;
        }

        // 1. A load address must let the block fit in the 64K address space.
        //    Jet Set Willy's 32K block fits at 0x7FFE and overflows at 0x80FB.
        int p1Overflow = code.Count(b => b.Param1 + b.Length > 0x10000);
        int p2Overflow = code.Count(b => b.Param2 + b.Length > 0x10000);

        if (p1Overflow != p2Overflow)
        {
            return p1Overflow < p2Overflow;
        }

        // 2. Only one convention puts the entry point inside a block.
        bool p1Covers = code.Any(b => Covers(b, entryPoint, param1IsLoad: true));
        bool p2Covers = code.Any(b => Covers(b, entryPoint, param1IsLoad: false));

        if (p1Covers != p2Covers)
        {
            return p1Covers;
        }

        // 3. Both or neither: prefer the addresses that look like load
        //    addresses, which start on a round boundary.
        int p1Score = code.Sum(b => Alignment(b.Param1));
        int p2Score = code.Sum(b => Alignment(b.Param2));

        return p1Score > p2Score;
    }

    /// <summary>
    /// How much an address looks like a load address rather than a jump target.
    ///
    /// A block starts where the loader put it: `0x8000`, `0x4000`, `0x5B00`.
    /// A start address is wherever the code happens to begin, which is usually
    /// mid-page. `0x7FFE` scores 2 (256-byte aligned); `0x80FB` scores 0.
    /// </summary>
    private static int Alignment(int address)
    {
        if ((address & 0xFF) == 0)
        {
            return 4;
        }

        if ((address & 0x0F) == 0)
        {
            return 2;
        }

        return (address & 0x03) == 0 ? 1 : 0;
    }

    private static bool Covers(DataBlock block, ushort address, bool param1IsLoad)
    {
        int start = param1IsLoad ? block.Param1 : block.Param2;
        return address >= start && address < start + block.Length;
    }

    /// <summary>Copies one block into the flat image at <paramref name="start"/>.</summary>
    private static void CopyBlock(DataBlock block, int start, byte[] flat, EntryCandidates entry)
    {
        for (int i = 0; i < block.Length; i++)
        {
            int address = start + i;

            if (address is >= 0x4000 and < 0x10000 && block.Offset + i < block.Bytes.Length)
            {
                flat[address] = block.Bytes[block.Offset + i];
            }
        }

        // A BASIC program lands at 0x8000, so it can be scanned in place.
        if (start == 0x8000 && block.Length >= 9 && TryFindBasicUsr(flat, block.Length, out ushort usr))
        {
            entry.BasicUsr ??= usr;
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

    private static ZxSnapshot Finish(List<DataBlock> blocks, EntryCandidates entry, string format)
    {
        byte[] flat = PlaceBlocks(blocks, entry);

        ZxSnapshot snapshot = new()
        {
            Model = (int)SpectrumModel.Zx48
        };

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
        else if (entry.HasBasicProgram && !entry.CoveredByBlock)
        {
            // A BASIC loader exists, its USR address is computed rather than a
            // literal, and the guess is not even inside a loaded block. Batty
            // is the example: Elite's Uni-Loader builds the address from PEEKs
            // and moves the payload there before jumping.
            //
            // Saying so is the difference between a user debugging their
            // snapshot and a user filing a bug about a recompiler that
            // "silently does nothing".
            Log.Warn(string.Format(
                CultureInfo.InvariantCulture,
                "BASIC loader computes its USR address and 0x{0:X4} is not inside any loaded " +
                "block: this tape relocates its code at run time.",
                resolved));

            snapshot.Notes.Add("Entry point guessed and outside every loaded block: relocating loader");
        }
        else if (entry.HasBasicProgram)
        {
            snapshot.Notes.Add("Entry point guessed from a CODE block; the BASIC loader computes its USR address");
        }
        else
        {
            snapshot.Notes.Add("No BASIC loader found; assuming 0x8000");
        }

        if (entry.UsedSwappedConvention)
        {
            snapshot.Notes.Add("CODE headers use load,start order; placed by parameter 1");
        }

        snapshot.Notes.Add(string.Format(
            CultureInfo.InvariantCulture,
            "{0}: {1} data block(s) recovered; a tape carries no registers",
            format,
            blocks.Count));

        // A tape whose data blocks have no headers is loaded by the game's own
        // routine: the addresses are computed at run time, so static placement
        // cannot be right. Dizzy II is the example — one header, five raw data
        // blocks, and a BASIC loader that POKEs the entry point before calling
        // it.
        int unheaded = blocks.Count(b => b.Type == Unheadered);

        if (unheaded > 0)
        {
            Log.Warn(string.Format(
                CultureInfo.InvariantCulture,
                "{0} of {1} data blocks carry no header: this tape is loaded by the game's " +
                "own routine, so their addresses cannot be recovered statically.",
                unheaded,
                blocks.Count));

            snapshot.Notes.Add(string.Format(
                CultureInfo.InvariantCulture,
                "{0} unheadered data block(s): loaded by the game's own routine, addresses unknown",
                unheaded));
        }

        // The entry point must contain something. A relocating loader (Elite's
        // Uni-Loader, used by Batty) computes the address at run time and moves
        // the payload there, so the lift starts on uninitialised memory and the
        // game hangs in a field of NOPs.
        //
        // Testing the bytes rather than block coverage is deliberate: block
        // coverage depends on the header convention guess, and a wrong guess
        // there is already reported separately. Uniform bytes are the actual
        // symptom a user sees.
        if (IsUniform(flat, resolved, 64))
        {
            Log.Warn(string.Format(
                CultureInfo.InvariantCulture,
                "Entry point 0x{0:X4} contains uniform bytes: this tape relocates or generates " +
                "its code at run time, so the lift starts on uninitialised memory.",
                resolved));

            snapshot.Notes.Add("Entry point holds uniform bytes: relocating loader, lift is incomplete");
        }

        return snapshot;
    }

    /// <summary>
    /// True when the first <paramref name="length"/> bytes at
    /// <paramref name="address"/> are all identical. Uninitialised RAM reads as
    /// all zero, and an erased block reads as all <c>0xFF</c>.
    /// </summary>
    private static bool IsUniform(byte[] memory, ushort address, int length)
    {
        if (address + length > memory.Length)
        {
            return true;
        }

        byte first = memory[address];

        for (int i = 1; i < length; i++)
        {
            if (memory[address + i] != first)
            {
                return false;
            }
        }

        return true;
    }
}
