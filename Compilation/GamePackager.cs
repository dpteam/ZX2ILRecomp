using System.Diagnostics;
using System.Globalization;
using System.Text;
using ZX2ILRecomp.Common;

namespace ZX2ILRecomp.Compilation;

/// <summary>How the lifted game should be delivered.</summary>
public enum PackageMode
{
    /// <summary>Only <c>Game.dll</c>, run with <c>dotnet Game.dll</c>.</summary>
    AssemblyOnly,

    /// <summary>
    /// <c>Game.exe</c> + <c>Game.dll</c> + deps/runtimeconfig: the normal .NET
    /// desktop app shape. Needs the .NET desktop runtime installed.
    /// </summary>
    AppHost,

    /// <summary>
    /// Self-contained single file: runs on a machine with no .NET at all.
    /// Costs tens of megabytes per game.
    /// </summary>
    SelfContained
}

/// <summary>Options for <see cref="GamePackager"/>.</summary>
public sealed class PackageOptions
{
    public PackageMode Mode { get; set; } = PackageMode.AppHost;

    /// <summary>Absolute path of an <c>.ico</c> to embed, or null for the default.</summary>
    public string? IconPath { get; set; }

    /// <summary>Assembly name without extension.</summary>
    public string AssemblyName { get; set; } = "Game";

    /// <summary>Target runtime identifier for self-contained builds.</summary>
    public string RuntimeIdentifier { get; set; } = "win-x64";

    public bool Verbose { get; set; }
}

/// <summary>Result of packaging a generated game.</summary>
public sealed class PackageResult
{
    public bool Success { get; init; }

    /// <summary>Path of the file the user should launch.</summary>
    public string LaunchPath { get; init; } = string.Empty;

    /// <summary>Path of the managed assembly, always present on success.</summary>
    public string AssemblyPath { get; init; } = string.Empty;

    /// <summary>Everything that was produced.</summary>
    public IReadOnlyList<string> Produced { get; init; } = Array.Empty<string>();

    public string Error { get; init; } = string.Empty;
}

/// <summary>
/// Turns the generated C# into something double-clickable, using nothing but
/// the .NET SDK that is already installed.
///
/// Why not Roslyn: it would mean a NuGet dependency, and this project is
/// deliberately dependency-free. <c>dotnet publish</c> is part of the SDK, is
/// what every .NET developer already has, and produces a real apphost — which
/// is also the only way Explorer picks up an embedded icon.
///
/// Failure is reported, never half-written: a missing SDK produces a clear
/// error instead of a directory full of nothing.
/// </summary>
public sealed class GamePackager
{
    public PackageResult Package(string sourceCode, string workDir, PackageOptions options, Action<string> log)
    {
        ArgumentNullException.ThrowIfNull(sourceCode);
        ArgumentNullException.ThrowIfNull(log);

        if (string.IsNullOrWhiteSpace(workDir))
        {
            throw new ArgumentException("Work directory is required.", nameof(workDir));
        }

        string safeName = SanitizeAssemblyName(options.AssemblyName);
        string projectDir = Path.Combine(workDir, "_pkg");
        string? publishDir = null;

        try
        {
            Directory.CreateDirectory(workDir);
            Directory.CreateDirectory(projectDir);

            File.WriteAllText(Path.Combine(projectDir, "Game.generated.cs"), sourceCode);
            string? icon = ResolveIcon(options, workDir, projectDir);
            File.WriteAllText(Path.Combine(projectDir, "LiftedGame.csproj"), BuildProject(safeName, icon, options));

            // `dotnet publish -o <dir>` forwards the path to MSBuild as a
            // property, and MSBuild splits property values on commas. A game
            // called "Manic Miner, The" therefore breaks publishing with
            // MSB1006, so publish into a comma-free scratch directory and move
            // the results afterwards.
            publishDir = CreateScratchPublishDir();

            (bool ok, string output) = RunPublish(projectDir, publishDir, options, log);

            if (!ok)
            {
                return new PackageResult
                {
                    Success = false,
                    Error = output
                };
            }

            List<string> produced = MoveOutputs(publishDir, workDir);
            string assembly = Path.Combine(workDir, safeName + ".dll");
            string launch = Path.Combine(workDir, safeName + ".exe");

            if (options.Mode == PackageMode.AssemblyOnly)
            {
                FileSystemUtil.TryDeleteFile(launch);
                launch = assembly;
            }

            if (!File.Exists(launch))
            {
                launch = assembly;
            }

            bool success = File.Exists(assembly);

            return new PackageResult
            {
                Success = success,
                LaunchPath = success ? launch : string.Empty,
                AssemblyPath = success ? assembly : string.Empty,
                Produced = produced,
                Error = success ? string.Empty : "No launchable output produced."
            };
        }
        catch (Exception ex)
        {
            return new PackageResult
            {
                Success = false,
                Error = ex.GetType().Name + ": " + ex.Message
            };
        }
        finally
        {
            FileSystemUtil.TryDeleteDirectory(projectDir);

            if (publishDir != null)
            {
                FileSystemUtil.TryDeleteDirectory(publishDir);
            }
        }
    }

    /// <summary>
    /// A unique, comma-free scratch directory for <c>dotnet publish -o</c>.
    /// The temp root itself can contain a comma (a Windows user name is enough),
    /// so each candidate is validated.
    /// </summary>
    private static string CreateScratchPublishDir()
    {
        string name = Guid.NewGuid().ToString("N");

        string[] roots =
        {
            Path.Combine(Path.GetTempPath(), "zx2il-publish"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ZX2ILRecomp", "publish"),
            Path.Combine(Path.GetPathRoot(Environment.SystemDirectory) ?? "C:\\", "ZX2ILPublish")
        };

        foreach (string root in roots)
        {
            if (string.IsNullOrWhiteSpace(root) || root.IndexOf(',') >= 0 || root.IndexOf(';') >= 0)
            {
                continue;
            }

            string dir = Path.Combine(root, name);

            try
            {
                Directory.CreateDirectory(dir);
                return dir;
            }
            catch
            {
                // Try the next candidate.
            }
        }

        throw new IOException("Could not create a comma-free scratch directory for publishing.");
    }

    /// <summary>
    /// Chooses the icon to embed: explicit path, then a previously captured
    /// <c>.icon.ico</c> next to the game, then the lifter's own default.
    /// </summary>
    private static string? ResolveIcon(PackageOptions options, string workDir, string projectDir)
    {
        List<string> candidates = new();

        if (!string.IsNullOrWhiteSpace(options.IconPath))
        {
            candidates.Add(options.IconPath!);
        }

        candidates.Add(Path.Combine(workDir, options.AssemblyName + ".icon.ico"));
        candidates.Add(Path.Combine(AppContext.BaseDirectory, "assets", "icon.ico"));

        foreach (string candidate in candidates)
        {
            if (!File.Exists(candidate))
            {
                continue;
            }

            try
            {
                // MSBuild resolves ApplicationIcon relative to the project file.
                File.Copy(candidate, Path.Combine(projectDir, "app.ico"), overwrite: true);
                return "app.ico";
            }
            catch
            {
                // Try the next candidate.
            }
        }

        return null;
    }

    private static string BuildProject(string assemblyName, string? icon, PackageOptions options)
    {
        string tfm = "net" + Environment.Version.Major.ToString(CultureInfo.InvariantCulture) + ".0-windows";
        bool selfContained = options.Mode == PackageMode.SelfContained;

        StringBuilder sb = new();

        sb.AppendLine("<Project Sdk=\"Microsoft.NET.Sdk\">");
        sb.AppendLine("  <PropertyGroup>");
        sb.AppendLine("    <OutputType>WinExe</OutputType>");
        sb.AppendLine("    <TargetFramework>" + tfm + "</TargetFramework>");
        sb.AppendLine("    <UseWindowsForms>true</UseWindowsForms>");
        sb.AppendLine("    <AssemblyName>" + Escape(assemblyName) + "</AssemblyName>");
        sb.AppendLine("    <RootNamespace>Lifted</RootNamespace>");
        sb.AppendLine("    <EnableDefaultCompileItems>false</EnableDefaultCompileItems>");
        sb.AppendLine("    <Nullable>disable</Nullable>");
        sb.AppendLine("    <ImplicitUsings>disable</ImplicitUsings>");
        sb.AppendLine("    <LangVersion>latest</LangVersion>");
        sb.AppendLine("    <DebugType>none</DebugType>");
        sb.AppendLine("    <GenerateDocumentationFile>false</GenerateDocumentationFile>");
        sb.AppendLine("    <SatelliteResourceLanguages>en</SatelliteResourceLanguages>");
        sb.AppendLine("    <IncludeSourceRevisionInInformationalVersion>false</IncludeSourceRevisionInInformationalVersion>");
        sb.AppendLine("    <!-- Machine-generated code: silence the noise the SDK would otherwise");
        sb.AppendLine("         print for every unused field and unreachable branch. -->");
        sb.AppendLine("    <NoWarn>$(NoWarn);CS0162;CS0168;CS0219;CS0414;CS0649;CS8509;CS8600;CS8601;CS8602;CS8603;CS8604;CS8618;CS8625</NoWarn>");

        if (icon != null)
        {
            sb.AppendLine("    <ApplicationIcon>" + Escape(icon) + "</ApplicationIcon>");
        }

        if (selfContained)
        {
            sb.AppendLine("    <RuntimeIdentifier>" + Escape(options.RuntimeIdentifier) + "</RuntimeIdentifier>");
            sb.AppendLine("    <SelfContained>true</SelfContained>");
            sb.AppendLine("    <PublishSingleFile>true</PublishSingleFile>");
            sb.AppendLine("    <IncludeNativeLibrariesForSelfExtract>true</IncludeNativeLibrariesForSelfExtract>");
            sb.AppendLine("    <EnableCompressionInSingleFile>true</EnableCompressionInSingleFile>");
        }

        sb.AppendLine("  </PropertyGroup>");
        sb.AppendLine("  <ItemGroup>");
        sb.AppendLine("    <Compile Include=\"Game.generated.cs\" />");
        sb.AppendLine("  </ItemGroup>");
        sb.AppendLine("</Project>");

        return sb.ToString();
    }

    private static (bool Ok, string Output) RunPublish(
        string projectDir,
        string publishDir,
        PackageOptions options,
        Action<string> log)
    {
        ProcessStartInfo psi = new()
        {
            FileName = "dotnet",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = projectDir
        };

        psi.ArgumentList.Add("publish");
        psi.ArgumentList.Add("LiftedGame.csproj");
        psi.ArgumentList.Add("-c");
        psi.ArgumentList.Add("Release");
        psi.ArgumentList.Add("-o");
        psi.ArgumentList.Add(publishDir);
        psi.ArgumentList.Add("--nologo");
        psi.ArgumentList.Add(options.Verbose ? "-v=minimal" : "-v=quiet");

        try
        {
            using Process process = Process.Start(psi)
                ?? throw new InvalidOperationException("dotnet could not be started.");

            StringBuilder stdout = new();
            StringBuilder stderr = new();

            process.OutputDataReceived += (_, e) => { if (e.Data != null) { stdout.AppendLine(e.Data); } };
            process.ErrorDataReceived += (_, e) => { if (e.Data != null) { stderr.AppendLine(e.Data); } };
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            if (!process.WaitForExit(TimeSpan.FromMinutes(10)))
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch
                {
                    // Already gone.
                }

                return (false, "dotnet publish timed out.");
            }

            string outText = stdout.ToString();
            string errText = stderr.ToString();

            if (options.Verbose && outText.Length > 0)
            {
                log(outText.TrimEnd());
            }

            if (process.ExitCode == 0)
            {
                return (true, outText);
            }

            StringBuilder error = new();
            error.AppendLine("dotnet publish failed (exit " + process.ExitCode + ").");

            foreach (string line in (outText + "\n" + errText).Split('\n'))
            {
                string trimmed = line.Trim();

                if (trimmed.Contains(" error ", StringComparison.OrdinalIgnoreCase))
                {
                    error.AppendLine(trimmed);
                }
            }

            return (false, error.ToString().TrimEnd());
        }
        catch (Exception ex)
        {
            return (false, "Could not run dotnet publish: " + ex.Message + " (is the .NET SDK installed?)");
        }
    }

    private static List<string> MoveOutputs(string publishDir, string workDir)
    {
        List<string> produced = new();

        if (!Directory.Exists(publishDir))
        {
            return produced;
        }

        foreach (string file in Directory.GetFiles(publishDir))
        {
            string target = Path.Combine(workDir, Path.GetFileName(file));

            try
            {
                FileSystemUtil.TryDeleteFile(target);
                File.Move(file, target);
                produced.Add(target);
            }
            catch
            {
                // A locked file (game still running) must not abort the lift.
            }
        }

        produced.Sort(StringComparer.OrdinalIgnoreCase);
        return produced;
    }

    /// <summary>
    /// Makes a snapshot name safe to use as an assembly name. Commas stay: they
    /// are legal in an assembly name and common in scene titles.
    /// </summary>
    private static string SanitizeAssemblyName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return "Game";
        }

        char[] invalid = Path.GetInvalidFileNameChars();
        StringBuilder sb = new(name.Length);

        foreach (char c in name)
        {
            sb.Append(Array.IndexOf(invalid, c) >= 0 ? '_' : c);
        }

        string result = sb.ToString().Trim().TrimEnd('.');
        return result.Length == 0 ? "Game" : result;
    }

    private static string Escape(string text)
    {
        return text.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");
    }

    /// <summary>True when a .NET SDK is available to publish with.</summary>
    public static bool IsSdkAvailable()
    {
        try
        {
            ProcessStartInfo psi = new()
            {
                FileName = "dotnet",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            psi.ArgumentList.Add("--list-sdks");

            using Process? process = Process.Start(psi);

            if (process is null)
            {
                return false;
            }

            string output = process.StandardOutput.ReadToEnd();

            if (!process.WaitForExit(15000))
            {
                try
                {
                    process.Kill(true);
                }
                catch
                {
                    // Already gone.
                }

                return false;
            }

            return process.ExitCode == 0 && output.Trim().Length > 0;
        }
        catch
        {
            return false;
        }
    }
}
