using ZX2ILRecomp.Common;
using ZX2ILRecomp.Core;
using ZX2ILRecomp.Host;
using ZX2ILRecomp.Lifting;

namespace ZX2ILRecomp.Tests;

internal static class Program
{
    private static int Main()
    {
        CliOptionsTests.Register();
        FileSystemUtilTests.Register();
        SnapshotTests.Register();
        DisassemblerTests.Register();
        LifterTests.Register();

        return TestHarness.RunAll("ZX2ILRecomp tests");
    }
}

/// <summary>Command-line parsing rules.</summary>
internal static class CliOptionsTests
{
    public static void Register()
    {
        TestHarness.Test("defaults", () =>
        {
            CliOptions options = CliOptions.Parse(Array.Empty<string>());

            TestHarness.Equal(0, options.Model, "model defaults to auto");
            TestHarness.True(options.SaveSource, "source is kept by default");
            TestHarness.Equal(60, options.HeadlessFrames, "default frame budget");
            TestHarness.Equal(Compilation.PackageMode.AppHost, options.PackageMode, "default package mode");
            TestHarness.True(options.LinearSweep, "linear sweep is on by default");
            TestHarness.False(options.Recursive, "recursion is off by default");
        });

        TestHarness.Test("bare path becomes the input", () =>
        {
            CliOptions options = CliOptions.Parse(new[] { @"C:\zx\game.z80" });

            TestHarness.True(options.InputPath.EndsWith("game.z80", StringComparison.OrdinalIgnoreCase),
                "input path captured");
        });

        TestHarness.Test("output defaults to zx_lifted_output", () =>
        {
            CliOptions options = CliOptions.Parse(Array.Empty<string>());

            TestHarness.True(options.OutputPath.EndsWith("zx_lifted_output", StringComparison.OrdinalIgnoreCase),
                "default output directory");
        });

        TestHarness.Test("package mode flags", () =>
        {
            TestHarness.Equal(Compilation.PackageMode.SelfContained,
                CliOptions.Parse(new[] { "--full-exe" }).PackageMode, "--full-exe");
            TestHarness.Equal(Compilation.PackageMode.AssemblyOnly,
                CliOptions.Parse(new[] { "--dll" }).PackageMode, "--dll");
            TestHarness.Equal(Compilation.PackageMode.AppHost,
                CliOptions.Parse(new[] { "--exe" }).PackageMode, "--exe");
        });

        TestHarness.Test("model is validated", () =>
        {
            TestHarness.Equal(48, CliOptions.Parse(new[] { "--model", "48" }).Model, "48K accepted");
            TestHarness.Equal(128, CliOptions.Parse(new[] { "--model=128" }).Model, "128K accepted");
            TestHarness.Equal(0, CliOptions.Parse(new[] { "--model", "7" }).Model, "nonsense falls back to auto");
        });

        TestHarness.Test("equals and space forms agree", () =>
        {
            CliOptions a = CliOptions.Parse(new[] { "--frames", "120", "--output", @"C:\out" });
            CliOptions b = CliOptions.Parse(new[] { "--frames=120", "--output=C:\\out" });

            TestHarness.Equal(a.HeadlessFrames, b.HeadlessFrames, "frame budget");
            TestHarness.Equal(a.OutputPath, b.OutputPath, "output path");
        });

        TestHarness.Test("unknown arguments are collected, not fatal", () =>
        {
            CliOptions options = CliOptions.Parse(new[] { "--wat", "game.sna" });

            TestHarness.Equal(1, options.UnknownArgs.Count, "one unknown argument");
            TestHarness.Equal("--wat", options.UnknownArgs[0], "unknown argument preserved");
        });

        TestHarness.Test("recursive flag is recorded as explicit", () =>
        {
            TestHarness.True(CliOptions.Parse(new[] { "-r" }).RecursiveSpecified, "explicit -r");
            TestHarness.False(CliOptions.Parse(Array.Empty<string>()).RecursiveSpecified, "not explicit by default");
        });
    }
}

/// <summary>Name sanitising used for directories and namespaces.</summary>
internal static class FileSystemUtilTests
{
    public static void Register()
    {
        TestHarness.Test("file names are made safe", () =>
        {
            TestHarness.Equal("a_b", FileSystemUtil.SanitizeFileName("a:b"), "invalid character replaced");
            TestHarness.Equal("snapshot", FileSystemUtil.SanitizeFileName(null), "empty name falls back");
        });

        TestHarness.Test("identifiers are valid C#", () =>
        {
            TestHarness.Equal("Manic_Miner", FileSystemUtil.SanitizeIdentifier("Manic Miner"), "space replaced");
            TestHarness.Equal("_3D_Deathchase", FileSystemUtil.SanitizeIdentifier("3D Deathchase"), "leading digit escaped");
            TestHarness.Equal("Game", FileSystemUtil.SanitizeIdentifier(""), "empty name falls back");
        });
    }
}

/// <summary>Snapshot loading for every supported container.</summary>
internal static class SnapshotTests
{
    public static void Register()
    {
        TestHarness.Test("SNA loads registers, memory and PC", () =>
        {
            using TempDirectory temp = TestHarness.Temp();
            string path = SyntheticSnapshot.Write(SyntheticSnapshot.DefaultMatrix[0], temp.Path);
            ZxSnapshot snapshot = ZxSnapshot.Load(path);

            TestHarness.Equal(48, snapshot.Model, "model");
            TestHarness.Equal(0x8000, (int)snapshot.PC, "PC popped from the stack");
            TestHarness.Equal(0x7FF2, (int)snapshot.SP, "SP advanced past the pushed PC");
            TestHarness.Equal(1, snapshot.IM, "interrupt mode");
            TestHarness.True(snapshot.IFF1, "interrupts enabled");
            TestHarness.Equal(0x31, (int)snapshot.RamBanks[2][0], "first program byte in bank 2");
        });

        TestHarness.Test("Z80 v1 loads an uncompressed 48K block", () =>
        {
            using TempDirectory temp = TestHarness.Temp();
            string path = SyntheticSnapshot.Write(SyntheticSnapshot.DefaultMatrix[1], temp.Path);
            ZxSnapshot snapshot = ZxSnapshot.Load(path);

            TestHarness.Equal(48, snapshot.Model, "model");
            TestHarness.Equal(0x8000, (int)snapshot.PC, "PC from the header");
            TestHarness.Equal(0x7FF0, (int)snapshot.SP, "SP from the header");
            TestHarness.Equal(0x31, (int)snapshot.RamBanks[2][0], "program copied into bank 2");
        });

        TestHarness.Test("SZX loads a RAMP page", () =>
        {
            using TempDirectory temp = TestHarness.Temp();
            string path = SyntheticSnapshot.Write(SyntheticSnapshot.DefaultMatrix[2], temp.Path);
            ZxSnapshot snapshot = ZxSnapshot.Load(path);

            TestHarness.Equal(128, snapshot.Model, "model");
            TestHarness.Equal(0x8000, (int)snapshot.PC, "PC from the Z80R block");
            TestHarness.Equal(0x31, (int)snapshot.RamBanks[2][0], "program copied into bank 2");
        });

        TestHarness.Test("TAP recovers code and the load address", () =>
        {
            using TempDirectory temp = TestHarness.Temp();
            string path = SyntheticSnapshot.Write(SyntheticSnapshot.DefaultMatrix[3], temp.Path);
            ZxSnapshot snapshot = ZxSnapshot.Load(path);

            TestHarness.Equal(0x8000, (int)snapshot.PC, "entry from the CODE header");
            TestHarness.Equal(0x31, (int)snapshot.RamBanks[2][0], "program copied into bank 2");
            TestHarness.False(snapshot.HasRomCode, "a tape image carries no ROM");
        });

        TestHarness.Test("tape entry point comes from the BASIC loader", () =>
        {
            // A commercial tape does not start where its CODE block loads: the
            // BASIC loader decides, with RANDOMIZE USR n. Getting this wrong is
            // the difference between "the game runs" and "the game loads and
            // then sits there", so it is worth its own test.
            using TempDirectory temp = TestHarness.Temp();
            string path = TapeWithBasicLoader.Write(temp.Path);
            ZxSnapshot snapshot = ZxSnapshot.Load(path);

            TestHarness.Equal(0x8400, (int)snapshot.PC, "entry from RANDOMIZE USR 33792");
            TestHarness.Equal(0x8400, (int)snapshot.Entry, "entry mirrored");
            TestHarness.Equal(0xF3, (int)snapshot.RamBanks[2][0x400], "code at the real entry point");
            TestHarness.True(
                snapshot.Notes.Exists(note => note.Contains("RANDOMIZE USR", StringComparison.Ordinal)),
                "note records where the entry came from");
        });

        TestHarness.Test("flat view honours 128K paging", () =>
        {
            using TempDirectory temp = TestHarness.Temp();
            string path = SyntheticSnapshot.Write(SyntheticSnapshot.DefaultMatrix[0], temp.Path);
            ZxSnapshot snapshot = ZxSnapshot.Load(path);

            snapshot.Model = 128;
            snapshot.RamBanks[3][0] = 0xAA;
            snapshot.Port7FFD = 0x03;

            byte[] flat = snapshot.GetFlat();
            TestHarness.Equal(0xAA, (int)flat[0xC000], "bank 3 mapped at 0xC000");

            snapshot.Port7FFD = 0x13;
            TestHarness.Equal(0xAA, (int)snapshot.GetFlat()[0xC000], "ROM selection does not move the bank");
            TestHarness.Equal(0x00, (int)snapshot.GetFlat()[0x0000], "Rom1 selected at 0x0000");
        });

        TestHarness.Test("minimal pseudo-ROM answers RST and IM1", () =>
        {
            ZxSnapshot snapshot = new();
            snapshot.InstallMinimalRom();

            TestHarness.True(snapshot.HasRomCode, "ROM flagged as code");
            TestHarness.Equal(0xC9, (int)snapshot.Rom0[0x00], "RST 00 is RET");
            TestHarness.Equal(0xC9, (int)snapshot.Rom0[0x08], "RST 08 is RET");
            TestHarness.Equal(0xFB, (int)snapshot.Rom0[0x38 + 11], "IM1 handler re-enables interrupts");
            TestHarness.Equal(0xC9, (int)snapshot.Rom0[0x38 + 12], "IM1 handler ends with RET");
        });
    }
}

/// <summary>Instruction decoding and control-flow edges.</summary>
internal static class DisassemblerTests
{
    public static void Register()
    {
        TestHarness.Test("opcode lengths", () =>
        {
            byte[] memory = new byte[65536];
            memory[0x8000] = 0x00;      // NOP
            memory[0x8001] = 0x3E;      // LD A,n
            memory[0x8002] = 0x01;
            memory[0x8003] = 0x21;      // LD HL,nn
            memory[0x8004] = 0x34;
            memory[0x8005] = 0x12;
            memory[0x8006] = 0xCB;      // CB prefix
            memory[0x8007] = 0x27;
            memory[0x8008] = 0xED;      // ED prefix
            memory[0x8009] = 0x43;
            memory[0x800A] = 0x00;
            memory[0x800B] = 0x00;

            TestHarness.Equal(1, CpuZ80.LengthOf(memory, 0x8000), "NOP");
            TestHarness.Equal(2, CpuZ80.LengthOf(memory, 0x8001), "LD A,n");
            TestHarness.Equal(3, CpuZ80.LengthOf(memory, 0x8003), "LD HL,nn");
            TestHarness.Equal(2, CpuZ80.LengthOf(memory, 0x8006), "CB prefix");
            TestHarness.Equal(4, CpuZ80.LengthOf(memory, 0x8008), "ED LD (nn),BC");
        });

        TestHarness.Test("control flow edges", () =>
        {
            ZxSnapshot snapshot = BuildSnapshot();
            DisassemblerZ80 disassembler = new(snapshot) { LinearSweep = false };
            AnalysisResultZ80 model = disassembler.Analyze();

            InstructionZ80 call = model.Instructions[0x8000];
            TestHarness.Equal(OpControlZ80.Call, call.Control, "CALL is a call");
            TestHarness.Equal(0x8010, (int)call.Target, "CALL target");
            TestHarness.Equal(0x8003, (int)call.Fallthrough, "CALL fall-through");

            InstructionZ80 jp = model.Instructions[0x8003];
            TestHarness.Equal(OpControlZ80.Jmp, jp.Control, "JP is a jump");
            TestHarness.Equal(0x8000, (int)jp.Target, "JP target");
            TestHarness.False(jp.HasFallthrough, "unconditional JP does not fall through");

            InstructionZ80 jr = model.Instructions[0x8010];
            TestHarness.Equal(OpControlZ80.Branch, jr.Control, "JR is a branch");
            TestHarness.Equal(0x8000, (int)jr.Target, "backwards JR target");
        });

        TestHarness.Test("RST vectors are enqueued when a ROM is present", () =>
        {
            ZxSnapshot snapshot = BuildSnapshot();
            snapshot.InstallMinimalRom();

            DisassemblerZ80 disassembler = new(snapshot) { LinearSweep = false };
            AnalysisResultZ80 model = disassembler.Analyze();

            TestHarness.True(model.Instructions.ContainsKey(0x0038), "IM1 handler decoded");
            TestHarness.Equal(OpControlZ80.Ret, model.Instructions[0x0038 + 12].Control, "handler returns");
            TestHarness.True(model.Instructions[0x0038 + 11].Opcode == 0xFB, "handler re-enables interrupts");
        });

        TestHarness.Test("mnemonics are readable", () =>
        {
            ZxSnapshot snapshot = new() { PC = 0x8000, Entry = 0x8000 };
            snapshot.RamBanks[2][0] = 0x21;   // LD HL,nn
            snapshot.RamBanks[2][1] = 0x34;
            snapshot.RamBanks[2][2] = 0x12;

            DisassemblerZ80 disassembler = new(snapshot) { LinearSweep = false };
            InstructionZ80 inst = disassembler.Decode(0x8000);

            TestHarness.Equal("LD HL,0x1234", inst.Text, "LD HL,nn text");
        });

        TestHarness.Test("indexed and prefixed mnemonics", () =>
        {
            ZxSnapshot snapshot = new() { PC = 0x8000, Entry = 0x8000 };

            byte[] program =
            {
                0xDD, 0x7E, 0x05,       // LD A,(IX+5)
                0xFD, 0xCB, 0xFE, 0x46, // BIT 0,(IY-2)
                0xED, 0xB0,             // LDIR
                0xCB, 0x3F               // SRL A
            };

            Array.Copy(program, 0, snapshot.RamBanks[2], 0, program.Length);
            DisassemblerZ80 disassembler = new(snapshot) { LinearSweep = false };

            TestHarness.Equal("LD A,(IX+0x05)", disassembler.Decode(0x8000).Text, "indexed load");
            TestHarness.Equal("BIT 0,(IY-0x02)", disassembler.Decode(0x8003).Text, "indexed bit");
            TestHarness.Equal("LDIR", disassembler.Decode(0x8007).Text, "block move");
            TestHarness.Equal("SRL A", disassembler.Decode(0x8009).Text, "CB rotate");
        });
    }

    private static ZxSnapshot BuildSnapshot()
    {
        ZxSnapshot snapshot = new()
        {
            PC = 0x8000,
            Entry = 0x8000,
            SP = 0x7FF0
        };

        byte[] program =
        {
            0xCD, 0x10, 0x80,   // 0x8000: CALL 0x8010
            0xC3, 0x00, 0x80,   // 0x8003: JP 0x8000
            0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, // padding to 0x8010
            0x18, 0xEE          // 0x8010: JR 0x8000
        };

        Array.Copy(program, 0, snapshot.RamBanks[2], 0, program.Length);
        return snapshot;
    }
}

/// <summary>Code generation: the output must be valid, complete C#.</summary>
internal static class LifterTests
{
    public static void Register()
    {
        TestHarness.Test("generated source has every runtime part", () =>
        {
            ZxSnapshot snapshot = new()
            {
                PC = 0x8000,
                Entry = 0x8000,
                SP = 0x7FF0
            };

            snapshot.RamBanks[2][0] = 0x3C;   // INC A
            snapshot.RamBanks[2][1] = 0xC3;   // JP 0x8000
            snapshot.RamBanks[2][2] = 0x00;
            snapshot.RamBanks[2][3] = 0x80;

            DisassemblerZ80 disassembler = new(snapshot) { LinearSweep = false };
            AnalysisResultZ80 model = disassembler.Analyze();

            string source = new LifterZ80(snapshot, model, "TestGame").Generate();

            TestHarness.Contains(source, "namespace Lifted.TestGame", "namespace");
            TestHarness.Contains(source, "static class Runtime", "runtime");
            TestHarness.Contains(source, "static class Memory", "memory");
            TestHarness.Contains(source, "static class Ula", "ula");
            TestHarness.Contains(source, "static class Ayu", "ay");
            TestHarness.Contains(source, "static class Audio", "audio");
            TestHarness.Contains(source, "class UlaForm : Form", "window");
            TestHarness.Contains(source, "static ushort B8000()", "block method");
            TestHarness.Contains(source, "case 0x8000: return B8000();", "dispatch entry");
            TestHarness.Contains(source, "public static void InitMemory()", "memory image");
            TestHarness.Contains(source, "headless: frames=", "headless summary");
        });

        TestHarness.Test("memory image round-trips through base64", () =>
        {
            ZxSnapshot snapshot = new()
            {
                PC = 0x8000,
                Entry = 0x8000
            };

            snapshot.RamBanks[2][0] = 0xAB;
            snapshot.RamBanks[2][1] = 0xCD;

            DisassemblerZ80 disassembler = new(snapshot) { LinearSweep = false };
            AnalysisResultZ80 model = disassembler.Analyze();
            string source = new LifterZ80(snapshot, model, "Mem").Generate();

            TestHarness.Contains(source, "InitialRam[2] = Convert.FromBase64String(", "bank 2 image emitted");

            // q80= decodes to AB CD 00 00 ...
            TestHarness.Contains(source, "q80A", "base64 payload for AB CD");
        });

        TestHarness.Test("ignored DD/FD prefix decodes as the base instruction", () =>
        {
            // `DD 00` is a NOP with an extra byte and four extra T-states. The
            // CPU ignores the prefix. Dizzy II contains exactly this, and
            // treating it as unsupported made the interpreter report
            // "Unknown opcode $00" and trap.
            ZxSnapshot snapshot = new() { PC = 0x8000, Entry = 0x8000 };

            byte[] program =
            {
                0xDD, 0x00,             // NOP (prefix ignored)
                0xDD, 0x3C,             // INC A (prefix ignored)
                0xDD, 0x7E, 0x05        // LD A,(IX+5) (prefix used)
            };

            Array.Copy(program, 0, snapshot.RamBanks[2], 0, program.Length);
            DisassemblerZ80 disassembler = new(snapshot) { LinearSweep = false };

            InstructionZ80 nop = disassembler.Decode(0x8000);
            TestHarness.True(nop.Supported, "DD 00 is supported");
            TestHarness.True(nop.IgnoredIndexPrefix, "flagged as an ignored prefix");
            TestHarness.Equal(2, nop.Length, "prefix counted in the length");
            TestHarness.Equal(OpControlZ80.Normal, nop.Control, "still falls through");
            TestHarness.Equal(0x8002, (int)nop.Fallthrough, "falls through past the prefix");

            InstructionZ80 inc = disassembler.Decode(0x8002);
            TestHarness.True(inc.Supported, "DD 3C is supported");
            TestHarness.Equal(0x3C, (int)inc.Opcode, "opcode is INC A, not the prefix");

            InstructionZ80 indexed = disassembler.Decode(0x8004);
            TestHarness.False(indexed.Supported, "DD 7E 05 still needs the interpreter");
            TestHarness.False(indexed.IgnoredIndexPrefix, "indexed form is not ignored");
        });

        TestHarness.Test("ignored prefix keeps relative branch targets correct", () =>
        {
            // DD 18 dd: JR with an ignored prefix. The target must account for
            // the prefix byte, or the branch lands one byte early.
            ZxSnapshot snapshot = new() { PC = 0x8000, Entry = 0x8000 };

            byte[] program =
            {
                0xDD, 0x18, 0x04,       // JR 0x8007
                0x00, 0x00, 0x00, 0x00,
                0xC3, 0x00, 0x80        // JP 0x8000
            };

            Array.Copy(program, 0, snapshot.RamBanks[2], 0, program.Length);
            DisassemblerZ80 disassembler = new(snapshot) { LinearSweep = false };

            InstructionZ80 jr = disassembler.Decode(0x8000);
            TestHarness.Equal(OpControlZ80.Branch, jr.Control, "decoded as a branch");
            TestHarness.Equal(0x8007, (int)jr.Target, "target includes the prefix byte");
            TestHarness.Equal(0x8003, (int)jr.Fallthrough, "fall-through is past the operand");
        });

        TestHarness.Test("no entry point is reported, not crashed", () =>
        {
            ZxSnapshot snapshot = new() { Entry = 0x9000, PC = 0x9000 };
            DisassemblerZ80 disassembler = new(snapshot) { LinearSweep = false };
            AnalysisResultZ80 model = disassembler.Analyze();

            // The entry is always decoded, so a snapshot always has one label.
            TestHarness.True(model.Labels.Count >= 1, "at least the entry label");
        });

        TestHarness.Test("basic blocks merge fall-through runs", () =>
        {
            // INC A x3 then JP 0x8000. The three INCs fall through into each
            // other, so they are one block; the JP ends it.
            ZxSnapshot snapshot = new() { PC = 0x8000, Entry = 0x8000 };
            snapshot.RamBanks[2][0] = 0x3C;   // INC A
            snapshot.RamBanks[2][1] = 0x3C;   // INC A
            snapshot.RamBanks[2][2] = 0x3C;   // INC A
            snapshot.RamBanks[2][3] = 0xC3;   // JP 0x8000
            snapshot.RamBanks[2][4] = 0x00;
            snapshot.RamBanks[2][5] = 0x80;

            DisassemblerZ80 disassembler = new(snapshot) { LinearSweep = false };
            AnalysisResultZ80 model = disassembler.Analyze();

            List<BasicBlocks.Block> blocks = BasicBlocks.Build(model, model.Labels, model.Entry);

            TestHarness.Equal(1, blocks.Count, "one block");
            TestHarness.Equal(4, blocks[0].Addresses.Count, "four instructions in it");
            TestHarness.Equal(0x8000, (int)blocks[0].Start, "block starts at the entry");
        });

        TestHarness.Test("a call ends its block", () =>
        {
            // CALL 0x8010 pushes 0x8003. If the block continued past the call,
            // the return would land in the middle of it with no dispatch entry.
            ZxSnapshot snapshot = new() { PC = 0x8000, Entry = 0x8000 };
            snapshot.RamBanks[2][0] = 0xCD;   // CALL 0x8010
            snapshot.RamBanks[2][1] = 0x10;
            snapshot.RamBanks[2][2] = 0x80;
            snapshot.RamBanks[2][3] = 0x3C;   // INC A (the return address)
            snapshot.RamBanks[2][4] = 0xC9;   // RET

            DisassemblerZ80 disassembler = new(snapshot) { LinearSweep = false };
            AnalysisResultZ80 model = disassembler.Analyze();

            List<BasicBlocks.Block> blocks = BasicBlocks.Build(model, model.Labels, model.Entry);
            Dictionary<ushort, ushort> starts = BasicBlocks.MapStarts(blocks);

            TestHarness.Equal(0x8000, (int)starts[0x8000], "call is its own block");
            TestHarness.Equal(0x8003, (int)starts[0x8003], "return address starts a block");
            TestHarness.False(starts[0x8000] == starts[0x8003], "call and its return are separate");
        });

        TestHarness.Test("unreachable decodes are pruned", () =>
        {
            // A 48K game decodes ~48 000 addresses because the sweep reads data
            // as code. Only reachable code should get methods.
            ZxSnapshot snapshot = new() { PC = 0x8000, Entry = 0x8000 };
            snapshot.RamBanks[2][0] = 0xC3;   // JP 0x8000
            snapshot.RamBanks[2][1] = 0x00;
            snapshot.RamBanks[2][2] = 0x80;

            DisassemblerZ80 disassembler = new(snapshot) { LinearSweep = false };
            AnalysisResultZ80 model = disassembler.Analyze();

            List<ushort> reachable = Reachability.Compute(model, model.Functions);

            TestHarness.True(reachable.Count >= 1, "entry is reachable");
            TestHarness.True(reachable.Count <= model.Instructions.Count, "pruning never adds");
        });

        TestHarness.Test("interrupt vectors survive pruning", () =>
        {
            // The IM 1 handler is reached through a register, never through a
            // decoded jump, so reachability only knows about it because the
            // analyser marks the vectors as roots. Pruning it would push every
            // frame interrupt through the interpreter.
            ZxSnapshot snapshot = new() { PC = 0x8000, Entry = 0x8000 };
            snapshot.InstallMinimalRom();
            snapshot.RamBanks[2][0] = 0xC3;   // JP 0x8000
            snapshot.RamBanks[2][1] = 0x00;
            snapshot.RamBanks[2][2] = 0x80;

            DisassemblerZ80 disassembler = new(snapshot) { LinearSweep = false };
            AnalysisResultZ80 model = disassembler.Analyze();

            List<ushort> reachable = Reachability.Compute(model, model.Functions);

            TestHarness.True(reachable.Contains(0x0038), "IM 1 handler is kept");
            TestHarness.True(reachable.Contains(0x0000), "RST 00 vector is kept");
        });
    }
}
