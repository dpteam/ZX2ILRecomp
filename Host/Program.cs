using System.Reflection;
using System.Text;
using ZX2ILRecomp.Compilation;
using ZX2ILRecomp.Core;
using ZX2ILRecomp.Lifting;

namespace ZX2ILRecomp.Host;

internal static class Program
{
    /// <summary>
    /// Entry point.
    ///
    /// STA is mandatory: WinForms requires a single-threaded apartment, and
    /// initialising COM as MTA first makes every later dialog fail.
    /// </summary>
    [STAThread]
    private static int Main(string[] args)
    {
        CliOptions options = CliOptions.Parse(args);

        ConsoleUi.Attach();
        Log.Verbose = options.Verbose;

        using LogFileSink sink = new(DefaultLogPath());
        sink.Attach();

        if (sink.IsOpen)
        {
            Log.Debug("Log file: " + sink.FilePath);
        }

        if (options.ShowVersion)
        {
            PrintVersion();
            return 0;
        }

        if (options.ShowHelp)
        {
            PrintHelp();
            return 0;
        }

        if (options.RunSelfTest)
        {
            return new SelfTest(options).Run();
        }

        if (options.Gui)
        {
            return RunGui(options);
        }

        foreach (string unknown in options.UnknownArgs)
        {
            Log.Warn("Unknown argument: " + unknown);
        }

        // No arguments at all: open the window on a real desktop, otherwise
        // offer the terminal menu. Explicit arguments always behave like the CLI
        // so scripts and CI never block on a prompt.
        if (args.Length == 0 && !options.NoTui)
        {
            if (Environment.UserInteractive)
            {
                return RunGui(options);
            }

            return RunInteractive(options);
        }

        if (string.IsNullOrWhiteSpace(options.InputPath))
        {
            PrintHelp();
            return 1;
        }

        try
        {
            return new Pipeline(options).Run();
        }
        catch (Exception ex)
        {
            Log.Error("Fatal error: " + ex.Message);
            Log.Debug(ex.ToString());
            return 1;
        }
    }

    private static string DefaultLogPath()
    {
        try
        {
            string directory = Path.Combine(Environment.CurrentDirectory, "logs");
            Directory.CreateDirectory(directory);
            string stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss", System.Globalization.CultureInfo.InvariantCulture);
            return Path.Combine(directory, "ZX2ILRecomp_" + stamp + ".log");
        }
        catch
        {
            return string.Empty;
        }
    }

    private static int RunGui(CliOptions options)
    {
        try
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new GuiHost(options));
            return 0;
        }
        catch (Exception ex)
        {
            Log.Error("Graphical interface unavailable: " + ex.Message);
            Log.Warn("Falling back to the terminal interface.");
            return RunInteractive(options);
        }
    }

    private static int RunInteractive(CliOptions options)
    {
        if (!ConsoleUi.CanUse())
        {
            PrintHelp();
            return 1;
        }

        string input = options.InputPath;
        bool recursive = options.Recursive;
        int model = options.Model;
        PackageMode package = options.PackageMode;

        while (true)
        {
            string[] items =
            {
                "Snapshot or folder: " + (string.IsNullOrWhiteSpace(input) ? "<none>" : input),
                "Scan sub-folders: " + (recursive ? "on" : "off"),
                "Spectrum model: " + (model == 0 ? "auto" : model + "K"),
                "Package: " + DescribePackage(package),
                "Start lifting",
                "Exit"
            };

            int selection = ConsoleUi.Select("ZX2ILRecomp - static ZX Spectrum recompiler", items);

            switch (selection)
            {
                case 0:
                    {
                        string path = ConsoleUi.Ask("Path: ");

                        if (path.Length > 0)
                        {
                            input = path;
                        }

                        break;
                    }

                case 1:
                    recursive = !recursive;
                    break;

                case 2:
                    model = model switch { 0 => 48, 48 => 128, _ => 0 };
                    break;

                case 3:
                    package = package switch
                    {
                        PackageMode.AppHost => PackageMode.SelfContained,
                        PackageMode.SelfContained => PackageMode.AssemblyOnly,
                        _ => PackageMode.AppHost
                    };
                    break;

                case 4:
                    {
                        if (string.IsNullOrWhiteSpace(input))
                        {
                            Log.Warn("Choose a snapshot or a folder first.");
                            break;
                        }

                        CliOptions run = new()
                        {
                            InputPath = input,
                            OutputPath = options.OutputPath,
                            Recursive = recursive,
                            RecursiveSpecified = true,
                            SaveSource = options.SaveSource,
                            Model = model,
                            PackageMode = package,
                            CaptureIconSeconds = options.CaptureIconSeconds,
                            Verbose = options.Verbose
                        };

                        return new Pipeline(run).Run();
                    }

                default:
                    return 0;
            }
        }
    }

    private static string DescribePackage(PackageMode mode) => mode switch
    {
        PackageMode.AssemblyOnly => "Game.dll",
        PackageMode.SelfContained => "single .exe",
        _ => "Game.exe"
    };

    private static void PrintVersion()
    {
        Version? version = Assembly.GetExecutingAssembly().GetName().Version;

        Console.WriteLine("ZX2ILRecomp " + (version?.ToString(3) ?? "0.0.0"));
        Console.WriteLine("Runtime: " + Environment.Version);
        Console.WriteLine("Formats: .z80, .sna, .szx, .tap, .tzx");
        Console.WriteLine("Models:  48K, 128K");
        Console.WriteLine("Generated: " + BuildInfo.Version);
    }

    private static void PrintHelp()
    {
        StringBuilder sb = new();

        sb.AppendLine();
        sb.AppendLine("ZX2ILRecomp - static ZX Spectrum (Z80) recompiler to .NET");
        sb.AppendLine("==========================================================");
        sb.AppendLine();
        sb.AppendLine("Usage:");
        sb.AppendLine("  ZX2ILRecomp                          open the graphical interface");
        sb.AppendLine("  ZX2ILRecomp <snapshot|folder> [opts] lift from the command line");
        sb.AppendLine("  ZX2ILRecomp --selftest [opts]        lift, package and run test snapshots");
        sb.AppendLine();
        sb.AppendLine("Interface:");
        sb.AppendLine("      --gui               Force the graphical interface.");
        sb.AppendLine("      --no-tui, --cli     Never start an interactive UI; arguments only.");
        sb.AppendLine();
        sb.AppendLine("Input / output:");
        sb.AppendLine("  -i, --input <path>      .z80/.sna/.szx/.tap/.tzx file or a folder.");
        sb.AppendLine("  -o, --output <dir>      Output directory. Default: zx_lifted_output");
        sb.AppendLine("  -r, --recursive         Recurse into sub-folders.");
        sb.AppendLine("      --model <48|128>    Force the Spectrum model. Default: auto.");
        sb.AppendLine("      --fresh             Re-lift snapshots that already have output.");
        sb.AppendLine();
        sb.AppendLine("Lifting:");
        sb.AppendLine("      --no-compile        Generate C# only, skip packaging.");
        sb.AppendLine("      --no-source         Do not keep the generated C#.");
        sb.AppendLine("      --keep-source       Keep the generated C# (default).");
        sb.AppendLine("      --no-sweep          Skip the linear sweep (recursive descent only).");
        sb.AppendLine("      --no-prune          Emit a method for every decoded address, including");
        sb.AppendLine("                          data the sweep misread. Slower to compile; for");
        sb.AppendLine("                          debugging a lift.");
        sb.AppendLine("  -v, --verbose           Verbose pipeline and publish output.");
        sb.AppendLine();
        sb.AppendLine("Packaging:");
        sb.AppendLine("      --exe               Game.exe + Game.dll (default). Needs .NET installed.");
        sb.AppendLine("      --dll               Only Game.dll, run with: dotnet Game.dll");
        sb.AppendLine("      --full-exe          Self-contained single .exe, no .NET needed (~70 MB).");
        sb.AppendLine("                          Aliases: --self-contained, --standalone");
        sb.AppendLine();
        sb.AppendLine("Icon:");
        sb.AppendLine("      --icon <image>      Use this image as the game icon.");
        sb.AppendLine("      --capture-icon <n>  Let the game screenshot itself after n seconds.");
        sb.AppendLine("                          Default: 5. Reused automatically on the next lift.");
        sb.AppendLine("      --no-icon           Do not capture a screenshot icon.");
        sb.AppendLine();
        sb.AppendLine("Verification:");
        sb.AppendLine("      --selftest          Lift, package and execute a synthetic snapshot for");
        sb.AppendLine("                          every supported container. Needs no game files.");
        sb.AppendLine("      --frames <n>        Frame budget for the headless run. Default: 60");
        sb.AppendLine("      --no-run            Package but do not execute during --selftest.");
        sb.AppendLine("      --keep              Keep --selftest artifacts.");
        sb.AppendLine();
        sb.AppendLine("Other:");
        sb.AppendLine("      --version           Print version, formats and models.");
        sb.AppendLine("  -h, --help              Show this help.");
        sb.AppendLine();
        sb.AppendLine("Examples:");
        sb.AppendLine("  ZX2ILRecomp");
        sb.AppendLine("  ZX2ILRecomp \"Manic Miner.z80\"");
        sb.AppendLine("  ZX2ILRecomp -i C:\\zx -o C:\\lifted -r");
        sb.AppendLine("  ZX2ILRecomp --selftest");
        sb.AppendLine();
        sb.AppendLine("Output:");
        sb.AppendLine("  <out>/<Name>/<Name>.exe          double-click to play (default)");
        sb.AppendLine("  <out>/<Name>/<Name>.dll          lifted game assembly");
        sb.AppendLine("  <out>/<Name>/<Name>.icon.ico     icon captured from the running game");
        sb.AppendLine("  <out>/<Name>/src/Game.generated.cs   generated C# (readable)");
        sb.AppendLine("  <out>/<Name>/analysis.txt        model, entry, coverage, disassembly");
        sb.AppendLine("  <out>/<Name>/dynamic_targets.txt addresses to force into the next lift");
        sb.AppendLine();
        sb.AppendLine("Run the result with:");
        sb.AppendLine("  Game.exe");
        sb.AppendLine("  Game.exe --headless 600          (no window, CI friendly)");
        sb.AppendLine("  Game.exe --capture-icon 5        (screenshot itself as the icon)");
        sb.AppendLine();

        Console.Write(sb.ToString());
    }
}
