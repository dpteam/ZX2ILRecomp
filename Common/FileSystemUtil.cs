using System.Text;

namespace ZX2ILRecomp.Common;

/// <summary>Helpers for turning arbitrary snapshot names into safe paths and identifiers.</summary>
public static class FileSystemUtil
{
    /// <summary>Replaces every character the file system rejects with an underscore.</summary>
    public static string SanitizeFileName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return "snapshot";
        }

        char[] invalid = Path.GetInvalidFileNameChars();
        StringBuilder sb = new(name.Length);

        foreach (char c in name)
        {
            sb.Append(Array.IndexOf(invalid, c) >= 0 ? '_' : c);
        }

        string result = sb.ToString().Trim().TrimEnd('.');

        return result.Length == 0 ? "snapshot" : result;
    }

    /// <summary>
    /// Turns a snapshot name into a valid C# identifier (namespace segment).
    /// Commas are kept on purpose: they are legal in identifiers and common in
    /// scene releases ("Manic Miner, The").
    /// </summary>
    public static string SanitizeIdentifier(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return "Game";
        }

        StringBuilder sb = new(name.Length);

        foreach (char c in name)
        {
            sb.Append(char.IsLetterOrDigit(c) || c == '_' ? c : '_');
        }

        if (sb.Length == 0)
        {
            sb.Append("Game");
        }

        if (char.IsDigit(sb[0]))
        {
            sb.Insert(0, '_');
        }

        return sb.ToString();
    }

    /// <summary>Deletes a file if it exists, swallowing every error.</summary>
    public static void TryDeleteFile(string? path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return;
        }

        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // A locked file is not worth failing a lift over.
        }
    }

    /// <summary>Deletes a directory tree if it exists, swallowing every error.</summary>
    public static void TryDeleteDirectory(string? path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return;
        }

        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch
        {
            // Leftover build intermediates are harmless.
        }
    }

    /// <summary>Returns the first existing path from <paramref name="candidates"/>.</summary>
    public static string? FirstExisting(IEnumerable<string> candidates)
    {
        foreach (string candidate in candidates)
        {
            if (!string.IsNullOrWhiteSpace(candidate) && File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }
}
