namespace ZX2ILRecomp;

/// <summary>Severity of a single log line.</summary>
public enum LogLevel
{
    Debug,
    Info,
    Step,
    Ok,
    Warn,
    Error
}

/// <summary>One log record, as handed to every sink.</summary>
public readonly record struct LogEntry(DateTime Timestamp, LogLevel Level, string Message)
{
    public override string ToString()
    {
        return "[" + Timestamp.ToString("yyyy-MM-dd HH:mm:ss.fff", System.Globalization.CultureInfo.InvariantCulture) +
               "] [" + Level.ToString().ToUpperInvariant() + "] " + Message;
    }
}

/// <summary>
/// Process-wide logging facade.
///
/// Design notes:
/// * <see cref="Written"/> is a plain event so the GUI, the TUI and a file
///   writer can all subscribe without knowing about each other.
/// * Every sink is wrapped in its own try/catch by the raiser, so a broken
///   subscriber can never take the pipeline down mid-lift.
/// * Writes are serialised on a private lock: the CPU-side pipeline runs on a
///   worker thread while the GUI renders on the UI thread.
/// </summary>
public static class Log
{
    private static readonly object Sync = new();
    private static readonly List<LogEntry> Buffer = new();

    /// <summary>Raised for every record. Handlers must be fast and must not throw.</summary>
    public static event Action<LogEntry>? Written;

    /// <summary>True when <see cref="LogLevel.Debug"/> lines should be emitted.</summary>
    public static bool Verbose { get; set; }

    /// <summary>Number of records kept for the GUI / report (ring buffer).</summary>
    public const int HistoryLimit = 5000;

    public static void Debug(string message)
    {
        if (!Verbose)
        {
            return;
        }

        Write(LogLevel.Debug, message);
    }

    public static void Info(string message) => Write(LogLevel.Info, message);

    public static void Step(string message) => Write(LogLevel.Step, message);

    public static void Ok(string message) => Write(LogLevel.Ok, message);

    public static void Warn(string message) => Write(LogLevel.Warn, message);

    public static void Error(string message) => Write(LogLevel.Error, message);

    /// <summary>Snapshot of the retained history, oldest first.</summary>
    public static IReadOnlyList<LogEntry> History
    {
        get
        {
            lock (Sync)
            {
                return Buffer.ToArray();
            }
        }
    }

    public static void Clear()
    {
        lock (Sync)
        {
            Buffer.Clear();
        }
    }

    private static void Write(LogLevel level, string message)
    {
        LogEntry entry = new(DateTime.Now, level, message ?? string.Empty);

        lock (Sync)
        {
            Buffer.Add(entry);

            if (Buffer.Count > HistoryLimit)
            {
                Buffer.RemoveRange(0, Buffer.Count - HistoryLimit);
            }
        }

        Action<LogEntry>? handlers = Written;

        if (handlers is null)
        {
            return;
        }

        foreach (Action<LogEntry> handler in handlers.GetInvocationList())
        {
            try
            {
                handler(entry);
            }
            catch
            {
                // A sink must never break the lift.
            }
        }
    }
}

/// <summary>Single-line status text (window title, tray tooltip, status bar).</summary>
public static class Status
{
    private static string _current = "Idle";

    public static event Action<string>? Changed;

    public static string Current => _current;

    public static void Set(string status)
    {
        _current = status ?? string.Empty;

        try
        {
            Console.Title = "ZX2ILRecomp - " + _current;
        }
        catch
        {
            // No console attached; harmless.
        }

        Action<string>? handlers = Changed;

        if (handlers is null)
        {
            return;
        }

        foreach (Action<string> handler in handlers.GetInvocationList())
        {
            try
            {
                handler(_current);
            }
            catch
            {
                // Ignore misbehaving listeners.
            }
        }
    }
}

/// <summary>
/// Writes every log record to a rolling UTF-8 file next to the executable's
/// working directory, mirroring what the console shows.
/// </summary>
public sealed class LogFileSink : IDisposable
{
    private readonly object _sync = new();
    private StreamWriter? _writer;

    public LogFileSink(string path)
    {
        try
        {
            string? dir = Path.GetDirectoryName(Path.GetFullPath(path));

            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }

            _writer = new StreamWriter(path, append: false, System.Text.Encoding.UTF8)
            {
                AutoFlush = true
            };

            FilePath = path;
        }
        catch
        {
            _writer = null;
            FilePath = string.Empty;
        }
    }

    public string FilePath { get; }

    public bool IsOpen => _writer is not null;

    public void Attach() => Log.Written += OnWritten;

    public void Detach() => Log.Written -= OnWritten;

    private void OnWritten(LogEntry entry)
    {
        lock (_sync)
        {
            if (_writer is null)
            {
                return;
            }

            try
            {
                _writer.WriteLine(entry.ToString());
            }
            catch
            {
                // Disk full / file locked: logging is best effort.
            }
        }
    }

    public void Dispose()
    {
        Detach();

        lock (_sync)
        {
            try
            {
                _writer?.Dispose();
            }
            catch
            {
                // Ignore.
            }

            _writer = null;
        }
    }
}
