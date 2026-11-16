using ZX2ILRecomp.Compilation;

namespace ZX2ILRecomp.Host;

/// <summary>Parsed command line.</summary>
public sealed class CliOptions
{
    public string InputPath = string.Empty;
    public string OutputPath = string.Empty;

    public bool Recursive;

    /// <summary>True when the user passed -r/--recursive explicitly.</summary>
    public bool RecursiveSpecified;

    public bool SaveSource = true;
    public bool NoCompile;
    public bool ShowHelp;
    public bool ShowVersion;
    public bool Verbose;
    public bool RunSelfTest;
    public bool SkipRun;
    public bool KeepTemp;
    public bool Fresh;

    /// <summary>Frame budget for the headless verification run.</summary>
    public int HeadlessFrames = 60;

    /// <summary>0 = auto-detect, otherwise 48 or 128.</summary>
    public int Model;

    /// <summary>Run the linear sweep after recursive descent.</summary>
    public bool LinearSweep = true;

    /// <summary>
    /// Emit a method only for code that is actually reachable. The linear sweep
    /// decodes data as instructions; without this filter a 48K game produces
    /// 9 MB of C# and the C# compiler becomes the bottleneck. Off only for
    /// debugging a lift where pruning is suspected.
    /// </summary>
    public bool PruneUnreachable = true;

    /// <summary>
    /// Skip ROM discovery and always use the pseudo-ROM. The self-test sets
    /// this so its results do not depend on whether the machine happens to have
    /// a <c>48.rom</c> lying around.
    /// </summary>
    public bool SkipRomSearch;

    public PackageMode PackageMode = PackageMode.AppHost;

    /// <summary>Custom icon for the generated executable, or null.</summary>
    public string? IconPath;

    /// <summary>Seconds before the generated game captures its own icon.</summary>
    public int CaptureIconSeconds = 5;

    public bool NoTui;
    public bool Gui;

    public List<string> UnknownArgs = new();

    public static CliOptions Parse(string[] args)
    {
        CliOptions o = new();

        for (int i = 0; i < args.Length; i++)
        {
            string a = args[i];

            if (string.IsNullOrWhiteSpace(a))
            {
                continue;
            }

            if (Is(a, "-h", "--help", "/?"))
            {
                o.ShowHelp = true;
                continue;
            }

            if (Is(a, "--version"))
            {
                o.ShowVersion = true;
                continue;
            }

            if (Is(a, "-r", "--recursive"))
            {
                o.Recursive = true;
                o.RecursiveSpecified = true;
                continue;
            }

            if (Is(a, "-v", "--verbose"))
            {
                o.Verbose = true;
                continue;
            }

            if (Is(a, "--no-compile"))
            {
                o.NoCompile = true;
                continue;
            }

            if (Is(a, "--no-source"))
            {
                o.SaveSource = false;
                continue;
            }

            if (Is(a, "--keep-source"))
            {
                o.SaveSource = true;
                continue;
            }

            if (Is(a, "--selftest"))
            {
                o.RunSelfTest = true;
                continue;
            }

            if (Is(a, "--no-run"))
            {
                o.SkipRun = true;
                continue;
            }

            if (Is(a, "--keep"))
            {
                o.KeepTemp = true;
                continue;
            }

            if (Is(a, "--fresh", "--overwrite"))
            {
                o.Fresh = true;
                continue;
            }

            if (Is(a, "--no-sweep", "--power-on-only"))
            {
                o.LinearSweep = false;
                continue;
            }

            if (Is(a, "--no-prune", "--keep-unreachable"))
            {
                o.PruneUnreachable = false;
                continue;
            }

            if (Is(a, "--dll"))
            {
                o.PackageMode = PackageMode.AssemblyOnly;
                continue;
            }

            if (Is(a, "--exe"))
            {
                o.PackageMode = PackageMode.AppHost;
                continue;
            }

            if (Is(a, "--full-exe", "--self-contained", "--standalone"))
            {
                o.PackageMode = PackageMode.SelfContained;
                continue;
            }

            if (Is(a, "--icon"))
            {
                if (i + 1 < args.Length)
                {
                    o.IconPath = args[++i];
                }

                continue;
            }

            if (Is(a, "--capture-icon"))
            {
                if (i + 1 < args.Length && int.TryParse(args[i + 1], out int seconds))
                {
                    o.CaptureIconSeconds = seconds;
                    i++;
                }

                continue;
            }

            if (Is(a, "--no-icon"))
            {
                o.CaptureIconSeconds = 0;
                continue;
            }

            if (Is(a, "--no-tui", "--cli"))
            {
                o.NoTui = true;
                continue;
            }

            if (Is(a, "--gui", "--ui"))
            {
                o.Gui = true;
                continue;
            }

            if (Is(a, "-i", "--input"))
            {
                if (i + 1 < args.Length)
                {
                    o.InputPath = args[++i];
                }

                continue;
            }

            if (Is(a, "-o", "--output"))
            {
                if (i + 1 < args.Length)
                {
                    o.OutputPath = args[++i];
                }

                continue;
            }

            if (Is(a, "--frames"))
            {
                if (i + 1 < args.Length && int.TryParse(args[i + 1], out int frames))
                {
                    o.HeadlessFrames = frames;
                    i++;
                }

                continue;
            }

            if (Is(a, "--model"))
            {
                if (i + 1 < args.Length && int.TryParse(args[i + 1], out int model))
                {
                    o.Model = model;
                    i++;
                }

                continue;
            }

            if (TryGetValue(a, "--input", out string input))
            {
                o.InputPath = input;
                continue;
            }

            if (TryGetValue(a, "--output", out string output))
            {
                o.OutputPath = output;
                continue;
            }

            if (TryGetValue(a, "--frames", out string framesValue) &&
                int.TryParse(framesValue, out int parsedFrames))
            {
                o.HeadlessFrames = parsedFrames;
                continue;
            }

            if (TryGetValue(a, "--model", out string modelValue) &&
                int.TryParse(modelValue, out int parsedModel))
            {
                o.Model = parsedModel;
                continue;
            }

            if (TryGetValue(a, "--icon", out string iconValue))
            {
                o.IconPath = iconValue;
                continue;
            }

            if (TryGetValue(a, "--capture-icon", out string captureValue) &&
                int.TryParse(captureValue, out int parsedCapture))
            {
                o.CaptureIconSeconds = parsedCapture;
                continue;
            }

            if (!a.StartsWith('-') && string.IsNullOrWhiteSpace(o.InputPath))
            {
                o.InputPath = a;
                continue;
            }

            o.UnknownArgs.Add(a);
        }

        if (string.IsNullOrWhiteSpace(o.OutputPath))
        {
            o.OutputPath = "zx_lifted_output";
        }

        try
        {
            o.OutputPath = Path.GetFullPath(o.OutputPath);
        }
        catch
        {
            o.OutputPath = Path.Combine(Environment.CurrentDirectory, "zx_lifted_output");
        }

        if (!string.IsNullOrWhiteSpace(o.InputPath))
        {
            try
            {
                o.InputPath = Path.GetFullPath(o.InputPath);
            }
            catch
            {
                // Keep the raw value; the pipeline reports it as missing.
            }
        }

        if (o.HeadlessFrames <= 0)
        {
            o.HeadlessFrames = 60;
        }

        if (o.Model != 0 && o.Model != 48 && o.Model != 128)
        {
            o.Model = 0;
        }

        if (o.CaptureIconSeconds < 0)
        {
            o.CaptureIconSeconds = 0;
        }

        return o;
    }

    private static bool Is(string arg, params string[] names)
    {
        foreach (string name in names)
        {
            if (string.Equals(arg, name, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static bool TryGetValue(string arg, string prefix, out string value)
    {
        string fullPrefix = prefix + "=";

        if (arg.StartsWith(fullPrefix, StringComparison.OrdinalIgnoreCase))
        {
            value = arg.Substring(fullPrefix.Length);
            return true;
        }

        value = string.Empty;
        return false;
    }
}
