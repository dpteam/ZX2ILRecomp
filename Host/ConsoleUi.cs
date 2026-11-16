namespace ZX2ILRecomp.Host;

/// <summary>
/// Terminal front end: coloured log output, a progress bar and the arrow-key
/// menu used when the tool is started without arguments on a real console.
/// </summary>
public static class ConsoleUi
{
    private static readonly object Sync = new();
    private static bool _attached;
    private static bool _progressActive;

    /// <summary>True when the console can host an interactive UI.</summary>
    public static bool CanUse()
    {
        try
        {
            return Environment.UserInteractive && !Console.IsOutputRedirected && !Console.IsInputRedirected;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Routes the log to stdout with one colour per level.</summary>
    public static void Attach()
    {
        if (_attached)
        {
            return;
        }

        _attached = true;

        try
        {
            Console.OutputEncoding = System.Text.Encoding.UTF8;
        }
        catch
        {
            // Legacy console host: keep the default encoding.
        }

        Log.Written += entry => Write(entry);
    }

    private static void Write(LogEntry entry)
    {
        lock (Sync)
        {
            if (_progressActive)
            {
                ClearProgress();
            }

            ConsoleColor previous = ConsoleColor.Gray;

            try
            {
                previous = Console.ForegroundColor;
                Console.ForegroundColor = ColorFor(entry.Level);
            }
            catch
            {
                // No colour support (redirected output): ignore.
            }

            try
            {
                Console.WriteLine(entry.ToString());
            }
            catch
            {
                // Output closed mid-run.
            }
            finally
            {
                try
                {
                    Console.ForegroundColor = previous;
                }
                catch
                {
                    // Ignore.
                }
            }
        }
    }

    private static ConsoleColor ColorFor(LogLevel level) => level switch
    {
        LogLevel.Error => ConsoleColor.Red,
        LogLevel.Warn => ConsoleColor.Yellow,
        LogLevel.Ok => ConsoleColor.Green,
        LogLevel.Step => ConsoleColor.Cyan,
        LogLevel.Debug => ConsoleColor.DarkGray,
        _ => ConsoleColor.Gray
    };

    private static void ClearProgress()
    {
        try
        {
            Console.Write("\r" + new string(' ', 78) + "\r");
        }
        catch
        {
            // Ignore.
        }

        _progressActive = false;
    }

    /// <summary>Draws a one-line progress bar for batch mode.</summary>
    public static void Progress(string label, int current, int total)
    {
        if (!CanUse() || total <= 0)
        {
            return;
        }

        lock (Sync)
        {
            try
            {
                int clamped = Math.Clamp(current, 0, total);
                const int Width = 28;
                int filled = (int)((long)clamped * Width / total);
                string bar = new string('#', filled) + new string('-', Width - filled);
                int percent = (int)((long)clamped * 100 / total);
                string text = string.Format(
                    "\r[{0}] {1,3}% {2}/{3} {4,-28}",
                    bar,
                    percent,
                    clamped,
                    total,
                    Truncate(label, 28));

                Console.ForegroundColor = ConsoleColor.DarkCyan;
                Console.Write(text);
                Console.ResetColor();
                _progressActive = true;
            }
            catch
            {
                // Ignore.
            }
        }
    }

    public static void ProgressDone()
    {
        if (!CanUse())
        {
            return;
        }

        lock (Sync)
        {
            if (_progressActive)
            {
                ClearProgress();
            }

            try
            {
                Console.WriteLine();
            }
            catch
            {
                // Ignore.
            }
        }
    }

    private static string Truncate(string? text, int max)
    {
        if (string.IsNullOrEmpty(text) || max <= 0)
        {
            return string.Empty;
        }

        return text.Length <= max ? text : text.Substring(0, max);
    }

    /// <summary>
    /// Arrow-key menu. Returns the selected index, or -1 when the user pressed
    /// Escape (or the console cannot host the UI).
    /// </summary>
    public static int Select(string title, IReadOnlyList<string> options)
    {
        if (!CanUse() || options.Count == 0)
        {
            return -1;
        }

        int index = 0;

        while (true)
        {
            try
            {
                Console.Clear();
                Console.ForegroundColor = ConsoleColor.Cyan;
                Console.WriteLine(title);
                Console.ResetColor();
                Console.WriteLine("Arrows to move, Enter to choose, Esc to exit.");
                Console.WriteLine();

                for (int i = 0; i < options.Count; i++)
                {
                    if (i == index)
                    {
                        Console.ForegroundColor = ConsoleColor.Yellow;
                        Console.WriteLine("> " + options[i]);
                        Console.ResetColor();
                    }
                    else
                    {
                        Console.WriteLine("  " + options[i]);
                    }
                }

                ConsoleKeyInfo key = Console.ReadKey(true);

                switch (key.Key)
                {
                    case ConsoleKey.UpArrow:
                        index = index == 0 ? options.Count - 1 : index - 1;
                        break;

                    case ConsoleKey.DownArrow:
                        index = index == options.Count - 1 ? 0 : index + 1;
                        break;

                    case ConsoleKey.Enter:
                        return index;

                    case ConsoleKey.Escape:
                        return -1;
                }
            }
            catch
            {
                return -1;
            }
        }
    }

    /// <summary>Prompts for a path on stdin.</summary>
    public static string Ask(string prompt)
    {
        Console.Write(prompt);
        string? text = Console.ReadLine();
        return text is null ? string.Empty : text.Trim().Trim('"');
    }
}
