namespace ZX2ILRecomp.Core;

/// <summary>One or two ROM pages recovered from disk.</summary>
public sealed record RomSet(byte[] Rom0, byte[]? Rom1, string PrimaryPath, string? SecondaryPath)
{
    /// <summary>Human-readable source description for reports and logs.</summary>
    public string Describe()
    {
        return SecondaryPath is null
            ? PrimaryPath
            : PrimaryPath + " + " + SecondaryPath;
    }
}

/// <summary>
/// Locates Spectrum ROM images on disk.
///
/// Snapshots never contain the ROM, yet almost every game needs it: RST calls,
/// the IM 1 handler and the character set all live at 0x0000-0x3FFF. Without a
/// ROM the lifter installs a minimal pseudo-ROM instead, which is enough for
/// frame interrupts but not for games that call into the ROM proper.
///
/// A 128K machine has two ROM pages and they are genuinely different — page 0
/// is the 128K editor/BASIC, page 1 is the 48K BASIC. Cloning one into the
/// other makes `LD A,(0x0000)`-style checks and the `0x7FFD` bit 4 switch
/// silently wrong, so both are looked up and both are loaded.
/// </summary>
public static class RomProvider
{
    /// <summary>Single-page candidates, used for 48K and as the 128K page 0.</summary>
    private static readonly string[] Rom48Names =
    {
        "48.rom", "48k.rom", "zx48.rom", "spectrum.rom", "zx_spectrum_48.rom", "48K.ROM"
    };

    /// <summary>
    /// 128K pairs, in order of preference. Each entry is (page 0, page 1).
    /// A null second element means "this dump has only one page".
    /// </summary>
    private static readonly (string First, string? Second)[] Rom128Names =
    {
        ("128-0.rom", "128-1.rom"),
        ("128.rom", "128-1.rom"),
        ("zx128-0.rom", "zx128-1.rom"),
        ("plus2-0.rom", "plus2-1.rom"),
        ("plus3-0.rom", "plus3-1.rom"),
        ("128K.ROM", null)
    };

    /// <summary>
    /// Searches the input's folder, the working directory, the executable
    /// directory and a <c>roms</c> sub-folder of each.
    /// </summary>
    public static RomSet? Find(string inputFile, int model)
    {
        List<string> directories = new();

        void Add(string? directory)
        {
            if (string.IsNullOrWhiteSpace(directory))
            {
                return;
            }

            try
            {
                if (Directory.Exists(directory) && !directories.Contains(directory))
                {
                    directories.Add(directory);
                }
            }
            catch
            {
                // Unreadable directory: skip.
            }
        }

        try
        {
            Add(Path.GetDirectoryName(Path.GetFullPath(inputFile)));
        }
        catch
        {
            // Keep going with the remaining candidates.
        }

        Add(Environment.CurrentDirectory);
        Add(AppContext.BaseDirectory);

        List<string> roots = new();

        foreach (string directory in directories)
        {
            roots.Add(directory);
            roots.Add(Path.Combine(directory, "roms"));
        }

        if (model >= (int)SpectrumModel.Zx128)
        {
            RomSet? pair = FindPair(roots);

            if (pair is not null)
            {
                return pair;
            }
        }

        // Fall back to a single page: correct for 48K, and for a 128K snapshot
        // it is still better than the pseudo-ROM (the 48K BASIC page answers
        // most ROM calls a 128K game makes).
        string? single = FindFirst(roots, Rom48Names);

        if (single is null)
        {
            return null;
        }

        byte[]? page = LoadPage(single);

        return page is null ? null : new RomSet(page, null, single, null);
    }

    private static RomSet? FindPair(List<string> roots)
    {
        foreach ((string first, string? second) in Rom128Names)
        {
            foreach (string root in roots)
            {
                string firstPath = Path.Combine(root, first);

                if (!File.Exists(firstPath))
                {
                    continue;
                }

                byte[]? rom0 = LoadPage(firstPath);

                if (rom0 is null)
                {
                    continue;
                }

                if (second is null)
                {
                    return new RomSet(rom0, null, firstPath, null);
                }

                string secondPath = Path.Combine(root, second);
                byte[]? rom1 = File.Exists(secondPath) ? LoadPage(secondPath) : null;

                return new RomSet(rom0, rom1, firstPath, rom1 is null ? null : secondPath);
            }
        }

        return null;
    }

    private static string? FindFirst(List<string> roots, string[] names)
    {
        foreach (string root in roots)
        {
            foreach (string name in names)
            {
                string candidate = Path.Combine(root, name);

                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
        }

        return null;
    }

    /// <summary>Loads a ROM file into a fresh 16K buffer, or null when unusable.</summary>
    public static byte[]? LoadPage(string path)
    {
        try
        {
            byte[] data = File.ReadAllBytes(path);

            if (data.Length < ZxSnapshot.PageSize)
            {
                Log.Warn("ROM file too small (<16384 bytes): " + path);
                return null;
            }

            byte[] page = new byte[ZxSnapshot.PageSize];
            Array.Copy(data, 0, page, 0, ZxSnapshot.PageSize);
            return page;
        }
        catch (Exception ex)
        {
            Log.Warn("Failed to load ROM " + path + ": " + ex.Message);
            return null;
        }
    }
}
