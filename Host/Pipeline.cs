using System.Globalization;
using System.Text;
using ZX2ILRecomp.Common;
using ZX2ILRecomp.Compilation;
using ZX2ILRecomp.Core;
using ZX2ILRecomp.Lifting;

namespace ZX2ILRecomp.Host;

/// <summary>What happened to one snapshot.</summary>
public enum LiftStatus
{
    Lifted,
    Skipped,
    Failed
}

/// <summary>Outcome of one snapshot, used by the CLI, the GUI and the self-test.</summary>
public sealed record LiftOutcome(LiftStatus Status, string SnapshotPath, string WorkDirectory, string Detail)
{
    public bool Ok => Status != LiftStatus.Failed;
}

/// <summary>
/// The batch pipeline: find snapshots, lift each one, report.
///
/// Order inside <see cref="LiftFile"/> is load-bearing and documented there:
/// model first, then ROM, then the entry-point heuristics. Doing it in any other
/// order silently breaks real snapshots (a forced entry point overrides a
/// legitimate PC inside a ROM call, which is how a working Manic Miner dump
/// turns into an infinite loop through the ROM editor).
/// </summary>
public sealed class Pipeline
{
    private static readonly string[] Extensions = { ".z80", ".sna", ".szx", ".tap", ".tzx" };

    private readonly CliOptions _options;

    public Pipeline(CliOptions options)
    {
        _options = options;
    }

    /// <summary>Called with a short status string before each snapshot.</summary>
    public Action<string>? OnStatus { get; set; }

    /// <summary>Called with (completed, total) as the batch advances.</summary>
    public Action<int, int>? OnProgress { get; set; }

    /// <summary>Set from another thread to stop after the current snapshot.</summary>
    public volatile bool CancelRequested;

    public IReadOnlyList<string> CollectFiles()
    {
        List<string> files = new();

        try
        {
            if (File.Exists(_options.InputPath))
            {
                files.Add(Path.GetFullPath(_options.InputPath));
            }
            else if (Directory.Exists(_options.InputPath))
            {
                SearchOption search = _options.Recursive
                    ? SearchOption.AllDirectories
                    : SearchOption.TopDirectoryOnly;

                foreach (string file in Directory.GetFiles(_options.InputPath, "*.*", search))
                {
                    string ext = Path.GetExtension(file).ToLowerInvariant();

                    if (Array.IndexOf(Extensions, ext) >= 0)
                    {
                        files.Add(Path.GetFullPath(file));
                    }
                }
            }
            else
            {
                Log.Error("Input path not found: " + _options.InputPath);
            }
        }
        catch (Exception ex)
        {
            Log.Error("File collection failed: " + ex.Message);
        }

        files.Sort(StringComparer.OrdinalIgnoreCase);
        return files;
    }

    public int Run()
    {
        Status.Set("Preparing");
        Directory.CreateDirectory(_options.OutputPath);

        IReadOnlyList<string> files = CollectFiles();

        if (files.Count == 0)
        {
            Log.Warn("No ZX Spectrum snapshots found in " + _options.InputPath);
            return 1;
        }

        Log.Info("Snapshots found: " + files.Count.ToString(CultureInfo.InvariantCulture));
        Log.Info("Output: " + _options.OutputPath);

        int lifted = 0;
        int skipped = 0;
        int failed = 0;

        for (int i = 0; i < files.Count; i++)
        {
            if (CancelRequested)
            {
                Log.Warn("Cancelled after " + lifted.ToString(CultureInfo.InvariantCulture) + " snapshot(s).");
                break;
            }

            string file = files[i];
            string name = Path.GetFileName(file);

            Status.Set("Lifting " + name);
            OnStatus?.Invoke(name);
            OnProgress?.Invoke(i, files.Count);

            LiftOutcome outcome = LiftFile(file);

            switch (outcome.Status)
            {
                case LiftStatus.Lifted:
                    lifted++;
                    break;
                case LiftStatus.Skipped:
                    skipped++;
                    break;
                default:
                    failed++;
                    break;
            }

            OnProgress?.Invoke(i + 1, files.Count);
        }

        Status.Set("Done");
        ConsoleUi.ProgressDone();

        Log.Info(string.Format(
            CultureInfo.InvariantCulture,
            "Summary: lifted={0}, skipped={1}, failed={2}",
            lifted,
            skipped,
            failed));

        return failed == 0 ? 0 : 2;
    }

    /// <summary>Lifts a single snapshot. Never throws; failures are logged and returned.</summary>
    public LiftOutcome LiftFile(string path)
    {
        string full = Path.GetFullPath(path);
        string romName = Path.GetFileNameWithoutExtension(full);
        string safeName = FileSystemUtil.SanitizeFileName(romName);
        string workDir = Path.Combine(_options.OutputPath, safeName);

        try
        {
            if (!_options.Fresh && AlreadyLifted(workDir, safeName))
            {
                Log.Info("Already lifted, skipping (use --fresh to redo): " + romName);
                return new LiftOutcome(LiftStatus.Skipped, full, workDir, "already lifted");
            }

            return Lift(full, romName, safeName, workDir);
        }
        catch (Exception ex)
        {
            Log.Error("Failed: " + romName + ": " + ex.Message);
            Log.Debug(ex.ToString());
            return new LiftOutcome(LiftStatus.Failed, full, workDir, ex.Message);
        }
    }

    private bool AlreadyLifted(string workDir, string safeName)
    {
        if (_options.NoCompile)
        {
            return false;
        }

        return File.Exists(Path.Combine(workDir, safeName + ".dll")) ||
               File.Exists(Path.Combine(workDir, safeName + ".exe"));
    }

    private LiftOutcome Lift(string fullPath, string romName, string safeName, string workDir)
    {
        string srcDir = Path.Combine(workDir, "src");
        Directory.CreateDirectory(workDir);

        if (_options.SaveSource)
        {
            Directory.CreateDirectory(srcDir);
        }

        Log.Step("=== " + romName + " ===");

        // 1. Load the container.
        ZxSnapshot snapshot = ZxSnapshot.Load(fullPath);

        // 2. Model override first: every heuristic below must see the final value.
        if (_options.Model is 48 or 128)
        {
            snapshot.Model = _options.Model;
        }

        // 3. ROM. RST calls, the IM 1 handler and the character set all live in
        //    ROM, so a snapshot without one can only ever run partially.
        string? romPath = null;

        if (ZxSnapshot.AllZero(snapshot.Rom0) && ZxSnapshot.AllZero(snapshot.Rom1))
        {
            RomSet? roms = _options.SkipRomSearch ? null : RomProvider.Find(fullPath, snapshot.Model);

            if (roms is not null)
            {
                snapshot.Rom0 = roms.Rom0;
                snapshot.HasRomCode = true;

                if (snapshot.Model >= (int)SpectrumModel.Zx128 && roms.Rom1 is not null)
                {
                    snapshot.Rom1 = roms.Rom1;
                }

                romPath = roms.Describe();
                Log.Info("Loaded ROM: " + romPath);
            }
            else
            {
                Log.Info("No ROM image found next to the snapshot; ROM calls will be stubs.");
            }
        }
        else
        {
            romPath = "(embedded in snapshot)";
        }

        // 4. SP heuristic: 0x0000 is inside ROM, where nothing can be pushed.
        //    Applies to both models — a 128K machine has the same ROM at 0.
        if (snapshot.SP == 0x0000)
        {
            Log.Warn("SP=0x0000 is not a legal stack; using 0xFF00.");
            snapshot.SP = 0xFF00;
        }

        // 5. Entry point. Forcing 0x8000 is only safe when there is no ROM to
        //    return into: a real snapshot may legitimately be paused inside a
        //    ROM call, and its own PC is then the correct place to resume.
        if (snapshot.PC < 0x4000 && !snapshot.HasRomCode)
        {
            Log.Warn(string.Format(
                CultureInfo.InvariantCulture,
                "PC=0x{0:X4} is in ROM and no ROM image is available; starting at 0x8000 instead.",
                snapshot.PC));

            snapshot.PC = 0x8000;
            snapshot.Entry = 0x8000;
        }

        // 6. Pseudo-ROM last: it is only needed as an interrupt handler, never as
        //    an entry point, so it must not influence step 5.
        if (!snapshot.HasRomCode)
        {
            snapshot.InstallMinimalRom();

            if (snapshot.Model >= (int)SpectrumModel.Zx128)
            {
                snapshot.Rom1 = (byte[])snapshot.Rom0.Clone();
            }

            romPath = null;
        }

        // A 128K tape is loaded by the game's own loader, which pages banks in
        // through port 0x7FFD. A tape image only records the *addresses* it was
        // saved at, so everything lands in the one bank that is paged in at
        // load time and the rest is lost. Say so instead of producing a game
        // that runs and shows a blank screen.
        if (snapshot.Model >= (int)SpectrumModel.Zx128 &&
            snapshot.Format is ".tap" or ".tzx")
        {
            Log.Warn("128K tape: only the load-time bank is recovered. " +
                     "Use a .z80/.sna/.szx snapshot of this game for a full lift.");
            snapshot.Notes.Add("128K tape: banks loaded by the game's own loader are not recovered");
        }

        Log.Info(string.Format(
            CultureInfo.InvariantCulture,
            "Model={0}K, PC=0x{1:X4}, SP=0x{2:X4}, AF=0x{3:X2}{4:X2}, BC=0x{5:X2}{6:X2}, DE=0x{7:X2}{8:X2}, HL=0x{9:X2}{10:X2}",
            snapshot.Model,
            snapshot.PC,
            snapshot.SP,
            snapshot.A,
            snapshot.F,
            snapshot.B,
            snapshot.C,
            snapshot.D,
            snapshot.E,
            snapshot.H,
            snapshot.L));

        // 7. User-forced code addresses.
        List<ushort> forced = ReadForcedTargets(workDir);

        DisassemblerZ80 disassembler = new(snapshot)
        {
            LinearSweep = _options.LinearSweep
        };

        foreach (ushort address in forced)
        {
            disassembler.ForcedAddresses.Add(address);
        }

        AnalysisResultZ80 model = disassembler.Analyze();

        Log.Info(string.Format(
            CultureInfo.InvariantCulture,
            "Analysis: instructions={0}, labels={1}, functions~={2}, indirectJumps={3}, unknownOps={4}",
            model.Instructions.Count,
            model.Labels.Count,
            model.Functions.Count,
            model.IndirectJumps.Count,
            model.UnknownOpcodes.Count));

        foreach (ushort address in model.IndirectJumps)
        {
            Log.Warn("Computed jump at 0x" + address.ToString("X4", CultureInfo.InvariantCulture) +
                     " (handled by the interpreter)");
        }

        foreach (byte op in model.UnknownOpcodes)
        {
            Log.Warn("Undecoded opcode 0x" + op.ToString("X2", CultureInfo.InvariantCulture));
        }

        // 8. Generate C#.
        LifterZ80 lifter = new(snapshot, model, safeName, _options.CaptureIconSeconds, _options.PruneUnreachable);
        string source = lifter.Generate();

        Log.Info(string.Format(
            CultureInfo.InvariantCulture,
            "Code generation: {0} blocks from {1} instructions, {2} decoded instructions pruned as unreachable",
            lifter.EmittedBlocks,
            lifter.EmittedLabels,
            lifter.DroppedLabels));

        AnalysisReport.Write(
            Path.Combine(workDir, "analysis.txt"),
            fullPath,
            snapshot,
            model,
            forced,
            romPath,
            lifter.EmittedBlocks,
            lifter.DroppedLabels);

        if (_options.SaveSource)
        {
            string sourcePath = Path.Combine(srcDir, "Game.generated.cs");
            File.WriteAllText(sourcePath, source, Encoding.UTF8);
            Log.Info("Generated C#: " + sourcePath);
        }

        if (_options.NoCompile)
        {
            Log.Warn("Compilation disabled (--no-compile): C# only.");
            return new LiftOutcome(LiftStatus.Lifted, fullPath, workDir, "source only");
        }

        // 9. Compile and package through the SDK.
        GamePackager packager = new();

        PackageOptions packageOptions = new()
        {
            Mode = _options.PackageMode,
            AssemblyName = safeName,
            IconPath = ResolveIcon(workDir, safeName),
            Verbose = _options.Verbose
        };

        PackageResult result = packager.Package(source, workDir, packageOptions, Log.Info);

        if (!result.Success)
        {
            Log.Error("Packaging failed: " + result.Error);
            return new LiftOutcome(LiftStatus.Failed, fullPath, workDir, result.Error);
        }

        Log.Ok("Lifted: " + result.LaunchPath);

        return new LiftOutcome(LiftStatus.Lifted, fullPath, workDir, result.LaunchPath);
    }

    /// <summary>
    /// A previously captured icon wins over the built-in one: it is a real
    /// screenshot of the game, which is what a player expects to see.
    /// </summary>
    private string? ResolveIcon(string workDir, string safeName)
    {
        if (!string.IsNullOrWhiteSpace(_options.IconPath))
        {
            if (File.Exists(_options.IconPath))
            {
                return _options.IconPath;
            }

            Log.Warn("Icon not found: " + _options.IconPath);
        }

        string captured = Path.Combine(workDir, safeName + ".icon.ico");
        return File.Exists(captured) ? captured : null;
    }

    /// <summary>
    /// Reads <c>dynamic_targets.txt</c>, creating a commented template when it is
    /// missing. This is the only manual input the pipeline accepts, and it is
    /// what closes the loop after a run: the game writes the targets it actually
    /// jumped to, the user pastes them here, the next lift inlines them.
    /// </summary>
    private static List<ushort> ReadForcedTargets(string workDir)
    {
        List<ushort> forced = new();
        string path = Path.Combine(workDir, "dynamic_targets.txt");

        if (!File.Exists(path))
        {
            try
            {
                File.WriteAllText(
                    path,
                    "; Addresses to force into the analysis, one per line.\r\n" +
                    "; Accepted forms: 8000, 0x8000, $8000.\r\n" +
                    "; Fill this in from dynamic_targets.log after running the game.\r\n" +
                    "\r\n",
                    Encoding.UTF8);

                Log.Info("Created dynamic_targets.txt template.");
            }
            catch (Exception ex)
            {
                Log.Warn("Could not create dynamic_targets.txt: " + ex.Message);
            }

            return forced;
        }

        string[] files = { path, Path.Combine(workDir, "dynamic_targets.log") };
        HashSet<ushort> seen = new();

        foreach (string file in files)
        {
            if (!File.Exists(file))
            {
                continue;
            }

            try
            {
                foreach (string raw in File.ReadAllLines(file))
                {
                    string line = raw.Trim();

                    if (line.Length == 0 || line.StartsWith(';') || line.StartsWith('#'))
                    {
                        continue;
                    }

                    line = line.Replace("0x", string.Empty, StringComparison.OrdinalIgnoreCase)
                               .Replace("$", string.Empty, StringComparison.Ordinal)
                               .Trim();

                    if (ushort.TryParse(line, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out ushort address) &&
                        seen.Add(address))
                    {
                        forced.Add(address);
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Warn("Could not read " + Path.GetFileName(file) + ": " + ex.Message);
            }
        }

        if (forced.Count > 0)
        {
            Log.Info("Forced code addresses: " + forced.Count.ToString(CultureInfo.InvariantCulture));
        }

        return forced;
    }
}
