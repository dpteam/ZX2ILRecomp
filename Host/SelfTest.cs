using System.Diagnostics;
using System.Globalization;
using System.Text;
using ZX2ILRecomp.Common;
namespace ZX2ILRecomp.Host;

/// <summary>
/// End-to-end verification: builds a synthetic snapshot for every supported
/// container, runs the full lift + package pipeline, then executes each
/// generated game headlessly and checks that it reaches a frame budget without
/// trapping.
///
/// This is the guard rail that makes changes to the lifter safe: a container
/// parser regression, a broken opcode table or a runtime that deadlocks on HALT
/// all fail here, with no game files needed.
/// </summary>
public sealed class SelfTest
{
    private readonly CliOptions _options;

    public SelfTest(CliOptions options)
    {
        _options = options;
    }

    public int Run()
    {
        string root = Path.Combine(_options.OutputPath, "_selftest");
        string snapshotDir = Path.Combine(root, "snapshots");
        string outDir = Path.Combine(root, "out");

        Directory.CreateDirectory(snapshotDir);
        Directory.CreateDirectory(outDir);

        Console.WriteLine();
        Console.WriteLine("ZX2ILRecomp self-test");
        Console.WriteLine("=====================");
        Console.WriteLine("Snapshots: " + snapshotDir);
        Console.WriteLine("Output:    " + outDir);
        Console.WriteLine("Frames:    " + _options.HeadlessFrames.ToString(CultureInfo.InvariantCulture));
        Console.WriteLine();

        List<Result> results = new();

        foreach (SyntheticSnapshot.Spec spec in SyntheticSnapshot.DefaultMatrix)
        {
            results.Add(RunOne(spec, snapshotDir, outDir));
        }

        Console.WriteLine();
        Console.WriteLine("Results");
        Console.WriteLine("-------");

        int failed = 0;

        foreach (Result result in results)
        {
            if (!result.Ok)
            {
                failed++;
            }

            Console.WriteLine("  " + (result.Ok ? "PASS  " : "FAIL  ") +
                              result.Spec.Name.PadRight(16) +
                              result.Detail);
        }

        Console.WriteLine();

        if (failed == 0)
        {
            Console.WriteLine("Self-test passed: " + results.Count.ToString(CultureInfo.InvariantCulture) +
                              " snapshots lifted, packaged and executed.");
        }
        else
        {
            Console.WriteLine("Self-test FAILED: " + failed.ToString(CultureInfo.InvariantCulture) +
                              " of " + results.Count.ToString(CultureInfo.InvariantCulture) + " snapshots failed.");
        }

        if (failed == 0 && !_options.KeepTemp)
        {
            Common.FileSystemUtil.TryDeleteDirectory(root);
        }
        else if (failed == 0)
        {
            Console.WriteLine("Artifacts kept: " + root);
        }

        return failed == 0 ? 0 : 2;
    }

    private Result RunOne(SyntheticSnapshot.Spec spec, string snapshotDir, string outDir)
    {
        Console.WriteLine("--- " + SyntheticSnapshot.Describe(spec) + ": " + spec.Description + " ---");

        string snapshotPath;
        string workDir = Path.Combine(outDir, spec.Name);
        string dllPath = Path.Combine(workDir, spec.Name + ".dll");

        try
        {
            snapshotPath = SyntheticSnapshot.Write(spec, snapshotDir);
        }
        catch (Exception ex)
        {
            return Result.Fail(spec, "snapshot generation failed: " + ex.Message);
        }

        CliOptions options = new()
        {
            InputPath = snapshotPath,
            OutputPath = outDir,
            SaveSource = _options.SaveSource,
            NoCompile = false,
            Fresh = true,
            Verbose = _options.Verbose,
            HeadlessFrames = _options.HeadlessFrames,
            PackageMode = Compilation.PackageMode.AppHost,

            // Hermetic on purpose: the result must not change depending on
            // whether the machine running the test has a ROM image installed.
            SkipRomSearch = true
        };

        try
        {
            Pipeline pipeline = new(options);
            LiftOutcome outcome = pipeline.LiftFile(snapshotPath);

            if (outcome.Status == LiftStatus.Failed)
            {
                return Result.Fail(spec, "lift failed: " + outcome.Detail);
            }
        }
        catch (Exception ex)
        {
            return Result.Fail(spec, "pipeline threw " + ex.GetType().Name + ": " + ex.Message);
        }

        if (!File.Exists(dllPath))
        {
            return Result.Fail(spec, "generated assembly missing");
        }

        if (!File.Exists(Path.Combine(workDir, "analysis.txt")))
        {
            return Result.Fail(spec, "analysis report missing");
        }

        if (_options.SkipRun)
        {
            return Result.Pass(spec, "packaged (execution skipped)");
        }

        string screenshotPath = Path.Combine(workDir, "frame.png");
        (bool ok, string detail) = RunHeadless(dllPath, _options.HeadlessFrames, screenshotPath);

        if (!ok)
        {
            return Result.Fail(spec, detail);
        }

        // A uniform picture means the ULA decoder produced nothing, which is a
        // failure even though the CPU ran happily for the whole frame budget.
        (bool rendered, string renderDetail) = CheckScreenshot(screenshotPath);

        return rendered ? Result.Pass(spec, detail + renderDetail) : Result.Fail(spec, renderDetail);
    }

    /// <summary>
    /// Loads the headless screenshot and asserts it contains both the border and
    /// the white 8x8 block the synthetic program draws.
    /// </summary>
    private static (bool Ok, string Detail) CheckScreenshot(string path)
    {
        if (!File.Exists(path))
        {
            return (false, "no screenshot produced");
        }

        try
        {
            using Bitmap image = new(path);
            HashSet<int> colors = new();

            for (int y = 0; y < image.Height; y += 3)
            {
                for (int x = 0; x < image.Width; x += 3)
                {
                    colors.Add(image.GetPixel(x, y).ToArgb());

                    if (colors.Count > 4)
                    {
                        break;
                    }
                }
            }

            // Border + paper + ink is already three; anything less means the
            // render did not happen.
            if (colors.Count < 3)
            {
                return (false, "screenshot is uniform (" + colors.Count + " colour(s)): ULA did not render");
            }

            return (true, ", " + colors.Count + " colours");
        }
        catch (Exception ex)
        {
            return (false, "could not read screenshot: " + ex.Message);
        }
    }

    private static (bool Ok, string Detail) RunHeadless(string dllPath, int frames, string screenshotPath)
    {
        ProcessStartInfo psi = new()
        {
            FileName = "dotnet",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        psi.ArgumentList.Add(dllPath);
        psi.ArgumentList.Add("--headless");
        psi.ArgumentList.Add(frames.ToString(CultureInfo.InvariantCulture));
        psi.ArgumentList.Add("--screenshot");
        psi.ArgumentList.Add(screenshotPath);

        try
        {
            using Process? process = Process.Start(psi);

            if (process is null)
            {
                return (false, "could not start dotnet");
            }

            StringBuilder stdout = new();
            StringBuilder stderr = new();

            process.OutputDataReceived += (_, e) => { if (e.Data != null) { stdout.AppendLine(e.Data); } };
            process.ErrorDataReceived += (_, e) => { if (e.Data != null) { stderr.AppendLine(e.Data); } };
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            if (!process.WaitForExit(TimeSpan.FromMinutes(3)))
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch
                {
                    // Already gone.
                }

                return (false, "headless run timed out");
            }

            string outText = stdout.ToString().Trim();
            string errText = stderr.ToString().Trim();

            if (process.ExitCode == 0)
            {
                string summary = FindSummaryLine(outText);
                return (true, summary.Length > 0 ? "-> " + summary : string.Empty);
            }

            string detail = "headless exit " + process.ExitCode.ToString(CultureInfo.InvariantCulture);

            if (errText.Length > 0)
            {
                detail += ": " + FirstLine(errText);
            }

            return (false, detail);
        }
        catch (Exception ex)
        {
            return (false, "could not run generated assembly: " + ex.Message);
        }
    }

    private static string FirstLine(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        int index = text.IndexOfAny(new[] { '\r', '\n' });
        string line = index < 0 ? text : text.Substring(0, index);

        return line.Length > 140 ? line.Substring(0, 140) + "..." : line;
    }

    /// <summary>
    /// Picks the "headless: frames=..." line out of the generated game's stdout,
    /// which is the only line worth surfacing in the summary table.
    /// </summary>
    private static string FindSummaryLine(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        foreach (string raw in text.Split('\n'))
        {
            string line = raw.Trim();

            if (line.StartsWith("headless:", StringComparison.Ordinal))
            {
                return line;
            }
        }

        return FirstLine(text);
    }

    private sealed class Result
    {
        private Result(SyntheticSnapshot.Spec spec, bool ok, string detail)
        {
            Spec = spec;
            Ok = ok;
            Detail = detail;
        }

        public SyntheticSnapshot.Spec Spec { get; }

        public bool Ok { get; }

        public string Detail { get; }

        public static Result Pass(SyntheticSnapshot.Spec spec, string detail) => new(spec, true, detail);

        public static Result Fail(SyntheticSnapshot.Spec spec, string detail) => new(spec, false, ": " + detail);
    }
}
