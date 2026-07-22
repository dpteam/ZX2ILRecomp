// Program.cs
// ZX Spectrum Static Recompiler / Lifter to .NET Assembly
// One file. .NET Framework 2.0..4.8. No third-party libraries.
using System;
using System.Collections.Generic;
using System.Text;
using System.IO;
using System.Diagnostics;
using System.Threading;
using System.Windows.Forms;
using System.Drawing;
using System.CodeDom.Compiler;
using Microsoft.CSharp;
using System.Xml.Serialization;
using System.Globalization;

namespace ZX2ILRecomp
{
    static class Program
    {
        static Options _options;
        static StateManager _state;
        static TrayManager _tray;

        [STAThread]
        static int Main(string[] args)
        {
            Options opts = Options.Parse(args);
            if (opts.ShowHelp)
            {
                HelpPrinter.Print();
                return 0;
            }

            _options = opts;
            SetupLogging(opts);

            foreach (string u in opts.UnknownArgs)
                Log.Warn("Unknown argument: " + u);

            AppDomain.CurrentDomain.UnhandledException += delegate(object sender, UnhandledExceptionEventArgs e)
            {
                Log.Error("Unhandled exception: " + e.ExceptionObject);
                if (_state != null) _state.Save();
            };

            Console.CancelKeyPress += delegate(object sender, ConsoleCancelEventArgs e)
            {
                Log.Warn("Ctrl+C/Ctrl+Break received. Saving state...");
                if (_state != null) _state.Save();
                e.Cancel = false;
            };

            Status.Changed += delegate(string s)
            {
                if (_tray != null) _tray.SetStatus(s);
            };

            Log.Info("ZX Spectrum Static Recompiler started.");
            Log.Info("Output directory: " + opts.OutputPath);

            _state = new StateManager(opts);
            _state.Load();
            _state.StartTimer();

            if (!opts.NoTray && Environment.UserInteractive)
            {
                _tray = new TrayManager();
                _tray.Start();
            }

            int exitCode = 0;
            try
            {
                if (string.IsNullOrEmpty(opts.InputPath))
                {
                    if (CanInteractive())
                    {
                        if (!Interactive(opts))
                            return 0;
                    }
                    else
                    {
                        HelpPrinter.Print();
                        return 1;
                    }
                }

                Pipeline pipeline = new Pipeline(opts, _state);
                exitCode = pipeline.Run();
            }
            catch (Exception ex)
            {
                Log.Error("Fatal error: " + ex.Message);
                Log.Debug(ex.ToString());
                exitCode = 1;
            }
            finally
            {
                if (_state != null)
                {
                    _state.StopTimer();
                    _state.Save();
                }

                if (_tray != null)
                {
                    if (opts.WaitAfter)
                    {
                        Log.Info("Done. Application remains in tray. Exit via tray menu.");
                        _tray.WaitForExit();
                    }
                    else
                    {
                        _tray.Shutdown();
                    }
                }
                else if (opts.WaitAfter)
                {
                    Console.WriteLine("Press any key to exit...");
                    try { Console.ReadKey(true); }
                    catch { }
                }
            }

            return exitCode;
        }

        static bool CanInteractive()
        {
            return Environment.UserInteractive && !Console.IsInputRedirected && !Console.IsOutputRedirected;
        }

        static bool Interactive(Options opts)
        {
            string[] items = new string[5];
            items[0] = "Specify ROM/snapshot or folder path (current: <none>)";
            items[1] = "Recursive folder processing: off";
            items[2] = "Model: Auto-detect";
            items[3] = "Start recompilation";
            items[4] = "Exit";

            while (true)
            {
                int sel = ConsoleUI.Select("ZX Spectrum Static Recompiler -- TUI", items);
                if (sel < 0)
                {
                    HelpPrinter.Print();
                    return false;
                }

                if (sel == 0)
                {
                    string p = ConsoleUI.AskPath();
                    if (!string.IsNullOrEmpty(p))
                    {
                        opts.InputPath = p;
                        items[0] = "Specify path (current: " + p + ")";
                    }
                }
                else if (sel == 1)
                {
                    opts.Recursive = !opts.Recursive;
                    items[1] = "Recursive folder processing: " + (opts.Recursive ? "on" : "off");
                }
                else if (sel == 2)
                {
                    if (opts.Model == 0) opts.Model = 48;
                    else if (opts.Model == 48) opts.Model = 128;
                    else opts.Model = 0;
                    items[2] = "Model: " + (opts.Model == 0 ? "Auto-detect" : opts.Model.ToString());
                }
                else if (sel == 3)
                {
                    if (!string.IsNullOrEmpty(opts.InputPath))
                        return true;
                    Log.Warn("Please specify a ROM/snapshot or folder path first.");
                }
                else if (sel == 4)
                {
                    return false;
                }
            }
        }

        static void SetupLogging(Options opts)
        {
            try { Console.OutputEncoding = Encoding.UTF8; }
            catch { }

            string logFile = null;
            try
            {
                string logDir = Path.Combine(Environment.CurrentDirectory, "logs");
                Directory.CreateDirectory(logDir);
                string ts = DateTime.Now.ToString("yyyyMMdd_HHmmss_fff");
                logFile = Path.Combine(logDir, "ZxLifter_" + ts + ".log");
            }
            catch
            {
                logFile = null;
            }

            Trace.Listeners.Clear();
            Trace.Listeners.Add(new ColorConsoleFileTraceListener(logFile));
            Trace.AutoFlush = true;

            if (logFile == null)
                Log.Warn("Failed to create log file. Logging to console only.");
            else
                Log.Info("Log file: " + logFile);
        }
    }

    public static class Log
    {
        public static void Info(string message) { Trace.WriteLine(message, "INFO"); }
        public static void Step(string message) { Trace.WriteLine(message, "STEP"); }
        public static void Ok(string message) { Trace.WriteLine(message, "OK"); }
        public static void Warn(string message) { Trace.WriteLine(message, "WARN"); }
        public static void Error(string message) { Trace.WriteLine(message, "ERROR"); }
        public static void Debug(string message) { Trace.WriteLine(message, "DEBUG"); }
    }

    public static class Status
    {
        public static event Action<string> Changed;

        public static void Set(string status)
        {
            try { Console.Title = "ZX Lifter - " + status; }
            catch { }

            Action<string> h = Changed;
            if (h != null) h(status);
        }
    }

    public class ColorConsoleFileTraceListener : TraceListener
    {
        StreamWriter _writer;
        object _lock = new object();

        public ColorConsoleFileTraceListener(string fileName)
        {
            if (!string.IsNullOrEmpty(fileName))
            {
                try
                {
                    _writer = new StreamWriter(fileName, true, Encoding.UTF8);
                    _writer.AutoFlush = true;
                }
                catch
                {
                    _writer = null;
                }
            }
        }

        public override void Write(string message)
        {
            WriteRaw(message, false, "INFO");
        }

        public override void WriteLine(string message)
        {
            WriteRaw(message, true, "INFO");
        }

        public override void TraceEvent(TraceEventCache eventCache, string source, TraceEventType eventType, int id, string message)
        {
            WriteFormatted(message, source, eventType);
        }

        public override void TraceEvent(TraceEventCache eventCache, string source, TraceEventType eventType, int id, string format, params object[] args)
        {
            string msg;
            if (format == null) msg = string.Empty;
            else if (args == null || args.Length == 0) msg = format;
            else msg = string.Format(format, args);
            WriteFormatted(msg, source, eventType);
        }

        public override void Fail(string message)
        {
            WriteFormatted(message, "ERROR", TraceEventType.Error);
        }

        public override void Fail(string message, string detailMessage)
        {
            WriteFormatted(message + " " + detailMessage, "ERROR", TraceEventType.Error);
        }

        void WriteFormatted(string message, string category, TraceEventType type)
        {
            string cat = string.IsNullOrEmpty(category) ? type.ToString() : category;
            string time = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture);
            string full = "[" + time + "] [" + cat + "] " + (message ?? string.Empty);

            lock (_lock)
            {
                WriteConsole(full, cat, true);
                if (_writer != null)
                {
                    try { _writer.WriteLine(full); }
                    catch { }
                }
            }
        }

        void WriteRaw(string message, bool line, string category)
        {
            lock (_lock)
            {
                WriteConsole(message ?? string.Empty, category, line);
                if (_writer != null)
                {
                    try
                    {
                        if (line) _writer.WriteLine(message);
                        else _writer.Write(message);
                    }
                    catch { }
                }
            }
        }

        void WriteConsole(string text, string category, bool line)
        {
            ConsoleColor old = ConsoleColor.Gray;
            bool restore = false;
            try
            {
                old = Console.ForegroundColor;
                Console.ForegroundColor = GetColor(category);
                restore = true;
            }
            catch { }

            try
            {
                if (line) Console.WriteLine(text);
                else Console.Write(text);
            }
            finally
            {
                if (restore)
                {
                    try { Console.ForegroundColor = old; }
                    catch { }
                }
            }
        }

        ConsoleColor GetColor(string category)
        {
            if (string.IsNullOrEmpty(category)) return ConsoleColor.Gray;
            switch (category.ToUpperInvariant())
            {
                case "ERROR": return ConsoleColor.Red;
                case "WARN": return ConsoleColor.Yellow;
                case "OK": return ConsoleColor.Green;
                case "STEP": return ConsoleColor.Cyan;
                case "DEBUG": return ConsoleColor.DarkGray;
                case "PROGRESS": return ConsoleColor.Magenta;
                default: return ConsoleColor.Gray;
            }
        }

        public override void Close()
        {
            lock (_lock)
            {
                if (_writer != null)
                {
                    try { _writer.Close(); }
                    catch { }
                    _writer = null;
                }
            }
            base.Close();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) Close();
            base.Dispose(disposing);
        }
    }

    public class Options
    {
        public string InputPath;
        public string OutputPath;
        public bool Recursive;
        public bool NoTray;
        public bool WaitAfter;
        public bool Fresh;
        public bool NoCompile;
        public bool SaveSource = true;
        public int CheckpointMinutes = 10;
        public int Model = 0;
        public bool ShowHelp;
        public List<string> UnknownArgs = new List<string>();

        public static Options Parse(string[] args)
        {
            Options o = new Options();

            for (int i = 0; i < args.Length; i++)
            {
                string a = args[i];

                if (Name(a, "--help", "-h", "/?"))
                {
                    o.ShowHelp = true;
                }
                else if (Name(a, "-r", "--recursive"))
                {
                    o.Recursive = true;
                }
                else if (Name(a, "--no-tray"))
                {
                    o.NoTray = true;
                }
                else if (Name(a, "--wait"))
                {
                    o.WaitAfter = true;
                }
                else if (Name(a, "--fresh"))
                {
                    o.Fresh = true;
                }
                else if (Name(a, "--no-compile"))
                {
                    o.NoCompile = true;
                }
                else if (Name(a, "--no-source"))
                {
                    o.SaveSource = false;
                }
                else if (Name(a, "--keep-source"))
                {
                    o.SaveSource = true;
                }
                else if (Name(a, "-i", "--input"))
                {
                    if (i + 1 < args.Length) o.InputPath = args[++i];
                }
                else if (Name(a, "-o", "--output"))
                {
                    if (i + 1 < args.Length) o.OutputPath = args[++i];
                }
                else if (Name(a, "--checkpoint"))
                {
                    if (i + 1 < args.Length)
                    {
                        int m;
                        if (int.TryParse(args[++i], out m)) o.CheckpointMinutes = m;
                    }
                }
                else if (Name(a, "--model"))
                {
                    if (i + 1 < args.Length)
                    {
                        int m;
                        if (int.TryParse(args[++i], out m)) o.Model = m;
                    }
                }
                else if (StartsWith(a, "--input="))
                {
                    o.InputPath = a.Substring("--input=".Length);
                }
                else if (StartsWith(a, "--output="))
                {
                    o.OutputPath = a.Substring("--output=".Length);
                }
                else if (StartsWith(a, "--checkpoint="))
                {
                    int m;
                    if (int.TryParse(a.Substring("--checkpoint=".Length), out m)) o.CheckpointMinutes = m;
                }
                else if (StartsWith(a, "--model="))
                {
                    int m;
                    if (int.TryParse(a.Substring("--model=".Length), out m)) o.Model = m;
                }
                else if (!a.StartsWith("-") && string.IsNullOrEmpty(o.InputPath))
                {
                    o.InputPath = a;
                }
                else
                {
                    o.UnknownArgs.Add(a);
                }
            }

            if (string.IsNullOrEmpty(o.OutputPath))
                o.OutputPath = "zx_lifted_output";

            try { o.OutputPath = Path.GetFullPath(o.OutputPath); }
            catch { o.OutputPath = Path.Combine(Environment.CurrentDirectory, "zx_lifted_output"); }

            try
            {
                if (!string.IsNullOrEmpty(o.InputPath))
                    o.InputPath = Path.GetFullPath(o.InputPath);
            }
            catch { }

            return o;
        }

        static bool Name(string a, params string[] names)
        {
            foreach (string n in names)
            {
                if (string.Equals(a, n, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }

        static bool StartsWith(string a, string prefix)
        {
            return a.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
        }
    }

    public static class HelpPrinter
    {
        public static void Print()
        {
            Console.WriteLine();
            Console.WriteLine("ZX Spectrum Static Recompiler / Lifter to .NET Assembly");
            Console.WriteLine("=======================================================");
            Console.WriteLine();
            Console.WriteLine("Usage:");
            Console.WriteLine("  ZX2ILRecomp.exe --input <game.z80|game.sna|game.tap|folder> --output <dir> [options]");
            Console.WriteLine();
            Console.WriteLine("Options:");
            Console.WriteLine("  -i, --input <path>      Input .z80/.sna/.tap/.szx file or folder.");
            Console.WriteLine("  -o, --output <dir>      Output directory. Default: zx_lifted_output");
            Console.WriteLine("  -r, --recursive         Recursive folder processing.");
            Console.WriteLine("      --no-tray           Do not create tray icon.");
            Console.WriteLine("      --wait              Stay in tray/wait after completion.");
            Console.WriteLine("      --fresh             Ignore saved state (start from scratch).");
            Console.WriteLine("      --no-compile        Only generate C#, skip EXE compilation.");
            Console.WriteLine("      --no-source         Do not save intermediate C# (not recommended).");
            Console.WriteLine("      --keep-source       Save intermediate C# (default).");
            Console.WriteLine("      --checkpoint <min>  Auto-save interval in minutes. Default: 10.");
            Console.WriteLine("      --model <48|128>    Force ZX Spectrum model. Default: auto.");
            Console.WriteLine("  -h, --help              This help.");
            Console.WriteLine();
            Console.WriteLine("Examples:");
            Console.WriteLine("  ZX2ILRecomp.exe game.z80");
            Console.WriteLine("  ZX2ILRecomp.exe --input C:\\zx_roms --output C:\\lifted -r --wait");
            Console.WriteLine("  ZX2ILRecomp.exe --input C:\\zx_roms -r --no-compile --checkpoint 5 --model 128");
            Console.WriteLine();
            Console.WriteLine("Pipeline:");
            Console.WriteLine("  1. Parse snapshot: .z80/.sna/.tap/.szx, registers, RAM banks.");
            Console.WriteLine("  2. Disassemble Z80, build control-flow graph.");
            Console.WriteLine("  3. Lift instructions to C# code + dynamic dispatch table.");
            Console.WriteLine("  4. Generate Memory Bus, ULA, Beeper, AY, Keyboard, WinForms window.");
            Console.WriteLine("  5. Compile generated C# into Game.exe via CSharpCodeProvider.");
            Console.WriteLine();
        }
    }

    public static class ConsoleUI
    {
        public static bool CanUse()
        {
            return Environment.UserInteractive && !Console.IsOutputRedirected && !Console.IsInputRedirected;
        }

        public static int Select(string title, string[] options)
        {
            if (!CanUse() || options == null || options.Length == 0)
                return -1;

            int index = 0;
            while (true)
            {
                try
                {
                    Console.Clear();
                    Console.ForegroundColor = ConsoleColor.Cyan;
                    Console.WriteLine(title);
                    Console.WriteLine("Use arrows and Enter. Esc to exit.");
                    Console.ResetColor();
                    Console.WriteLine();

                    for (int i = 0; i < options.Length; i++)
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
                    if (key.Key == ConsoleKey.UpArrow)
                    {
                        index--;
                        if (index < 0) index = options.Length - 1;
                    }
                    else if (key.Key == ConsoleKey.DownArrow)
                    {
                        index++;
                        if (index >= options.Length) index = 0;
                    }
                    else if (key.Key == ConsoleKey.Enter)
                    {
                        return index;
                    }
                    else if (key.Key == ConsoleKey.Escape)
                    {
                        return -1;
                    }
                }
                catch
                {
                    return -1;
                }
            }
        }

        public static string AskPath()
        {
            Console.Write("Enter path: ");
            string s = Console.ReadLine();
            return s == null ? string.Empty : s.Trim();
        }

        public static void Progress(string label, int current, int total)
        {
            if (!CanUse() || total <= 0) return;

            lock (typeof(ConsoleUI))
            {
                try
                {
                    if (current > total) current = total;
                    int width = 30;
                    int filled = (int)((long)current * width / total);
                    if (filled < 0) filled = 0;
                    if (filled > width) filled = width;

                    string bar = new string('#', filled) + new string('-', width - filled);
                    int percent = (int)((long)current * 100 / total);
                    string text = string.Format(
                        "\r[{0}] {1,3}% {2}/{3} {4}   ",
                        bar,
                        percent,
                        current,
                        total,
                        Truncate(label, 24));

                    Console.ForegroundColor = ConsoleColor.DarkCyan;
                    Console.Write(text);
                    Console.ResetColor();
                }
                catch { }
            }
        }

        public static void ProgressDone()
        {
            if (!CanUse()) return;
            try { Console.WriteLine(); }
            catch { }
        }

        static string Truncate(string s, int max)
        {
            if (string.IsNullOrEmpty(s)) return string.Empty;
            if (max <= 0) return string.Empty;
            if (s.Length <= max) return s;
            return s.Substring(0, max);
        }
    }

    public class TrayManager
    {
        TrayAppContext _context;
        Thread _thread;
        ManualResetEvent _exited = new ManualResetEvent(false);
        ManualResetEvent _ready = new ManualResetEvent(false);

        public void Start()
        {
            try
            {
                _thread = new Thread(new ThreadStart(Run));
                _thread.IsBackground = true;
                _thread.SetApartmentState(ApartmentState.STA);
                _thread.Start();
                _ready.WaitOne(2000, false);
            }
            catch (Exception ex)
            {
                Log.Warn("Failed to start tray: " + ex.Message);
            }
        }

        void Run()
        {
            try
            {
                _context = new TrayAppContext(this);
                _ready.Set();
                Application.Run(_context);
            }
            catch (Exception ex)
            {
                Log.Warn("Tray error: " + ex.Message);
            }
            finally
            {
                _exited.Set();
                _ready.Set();
            }
        }

        public void SetStatus(string text)
        {
            TrayAppContext ctx = _context;
            if (ctx != null) ctx.SetStatus(text);
        }

        public void Shutdown()
        {
            TrayAppContext ctx = _context;
            if (ctx != null) ctx.RequestExit();
            _exited.WaitOne(2000, false);
        }

        public void WaitForExit()
        {
            _exited.WaitOne();
        }
    }

    public class TrayAppContext : ApplicationContext
    {
        NotifyIcon _notify;
        Form _invoker;
        TrayManager _manager;

        public TrayAppContext(TrayManager manager)
        {
            _manager = manager;

            _invoker = new Form();
            _invoker.ShowInTaskbar = false;
            _invoker.FormBorderStyle = FormBorderStyle.FixedToolWindow;
            _invoker.StartPosition = FormStartPosition.Manual;
            _invoker.Size = new Size(1, 1);
            _invoker.Opacity = 0;
            _invoker.Text = "ZX Lifter Invoker";
            _invoker.Show();
            _invoker.Hide();
            MainForm = _invoker;

            _notify = new NotifyIcon();
            _notify.Icon = SystemIcons.Application;
            _notify.Text = "ZX Lifter";

            ContextMenuStrip menu = new ContextMenuStrip();
            menu.Items.Add("Show status", null, delegate(object sender, EventArgs e)
            {
                Log.Info("Tray status: " + _notify.Text);
            });
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("Exit", null, delegate(object sender, EventArgs e)
            {
                RequestExit();
            });

            _notify.ContextMenuStrip = menu;
            _notify.DoubleClick += delegate(object sender, EventArgs e)
            {
                Log.Info("ZX Lifter in tray. Use context menu.");
            };
            _notify.Visible = true;
        }

        public void SetStatus(string text)
        {
            if (_notify == null) return;
            try { _notify.Text = Truncate(text, 63); }
            catch { }
        }

        public void RequestExit()
        {
            if (_invoker != null && _invoker.IsHandleCreated)
                _invoker.BeginInvoke(new MethodInvoker(ExitThread));
            else
                ExitThread();
        }

        string Truncate(string s, int max)
        {
            if (string.IsNullOrEmpty(s)) return "ZX Lifter";
            if (s.Length <= max) return s;
            return s.Substring(0, max);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                if (_notify != null)
                {
                    try
                    {
                        _notify.Visible = false;
                        _notify.Dispose();
                    }
                    catch { }
                    _notify = null;
                }
            }
            base.Dispose(disposing);
        }
    }

    [XmlRoot("Zx2IlState")]
    public class AppState
    {
        public int Version = 1;
        public DateTime Updated = DateTime.Now;
        public string LastFile = string.Empty;
        public List<string> ProcessedFiles = new List<string>();
    }

    public class StateManager
    {
        Options _opts;
        AppState _state = new AppState();
        string _path;
        object _lock = new object();
        System.Threading.Timer _timer;

        public StateManager(Options opts)
        {
            _opts = opts;
            _path = Path.Combine(opts.OutputPath, ".zx2il.state.xml");
        }

        public void Load()
        {
            try
            {
                if (_opts.Fresh)
                {
                    if (File.Exists(_path))
                    {
                        File.Delete(_path);
                        Log.Info("State reset (--fresh).");
                    }
                    _state = new AppState();
                    return;
                }

                if (!File.Exists(_path))
                {
                    _state = new AppState();
                    return;
                }

                XmlSerializer ser = new XmlSerializer(typeof(AppState));
                FileStream fs = new FileStream(_path, FileMode.Open, FileAccess.Read);
                try
                {
                    _state = (AppState)ser.Deserialize(fs);
                }
                finally
                {
                    fs.Close();
                }

                if (_state == null) _state = new AppState();
                if (_state.ProcessedFiles == null) _state.ProcessedFiles = new List<string>();

                Log.Info("State loaded. Already processed files: " + _state.ProcessedFiles.Count);
            }
            catch (Exception ex)
            {
                Log.Warn("Failed to load state: " + ex.Message);
                try
                {
                    if (File.Exists(_path))
                        File.Move(_path, _path + ".corrupt");
                }
                catch { }
                _state = new AppState();
            }
        }

        public void Save()
        {
            lock (_lock)
            {
                try
                {
                    string dir = Path.GetDirectoryName(_path);
                    if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

                    _state.Updated = DateTime.Now;

                    XmlSerializer ser = new XmlSerializer(typeof(AppState));
                    string tmp = _path + ".tmp";
                    FileStream fs = new FileStream(tmp, FileMode.Create, FileAccess.Write);
                    try
                    {
                        ser.Serialize(fs, _state);
                    }
                    finally
                    {
                        fs.Close();
                    }

                    if (File.Exists(_path)) File.Delete(_path);
                    File.Move(tmp, _path);
                }
                catch (Exception ex)
                {
                    Log.Warn("Failed to save state: " + ex.Message);
                }
            }
        }

        public bool IsProcessed(string file)
        {
            lock (_lock)
            {
                return _state.ProcessedFiles.Contains(Normalize(file));
            }
        }

        public void MarkProcessed(string file)
        {
            lock (_lock)
            {
                string n = Normalize(file);
                if (!_state.ProcessedFiles.Contains(n))
                    _state.ProcessedFiles.Add(n);
                _state.LastFile = file;
                Save();
            }
        }

        public void SetLastFile(string file)
        {
            lock (_lock)
            {
                _state.LastFile = file;
            }
        }

        public void StartTimer()
        {
            if (_opts.CheckpointMinutes <= 0) return;

            long msLong = (long)_opts.CheckpointMinutes * 60000L;
            int ms = msLong > int.MaxValue ? int.MaxValue : (int)msLong;
            if (ms < 1000) ms = 1000;

            _timer = new System.Threading.Timer(new TimerCallback(OnTimer), null, ms, ms);
            Log.Info("State checkpoint every " + _opts.CheckpointMinutes + " min.");
        }

        public void StopTimer()
        {
            if (_timer != null)
            {
                try { _timer.Dispose(); }
                catch { }
                _timer = null;
            }
        }

        void OnTimer(object state)
        {
            Save();
            Log.Debug("Checkpoint state saved.");
        }

        string Normalize(string path)
        {
            if (string.IsNullOrEmpty(path)) return string.Empty;
            return path.ToUpperInvariant();
        }
    }

    public class Pipeline
    {
        Options _opts;
        StateManager _state;

        public Pipeline(Options opts, StateManager state)
        {
            _opts = opts;
            _state = state;
        }

        public int Run()
        {
            Status.Set("Initializing");
            EnsureOutput();
            EnsureConfig();

            List<string> files = CollectFiles();
            if (files.Count == 0)
            {
                Log.Warn("No input ZX Spectrum files found.");
                return 1;
            }

            Log.Info("Files found: " + files.Count);

            int ok = 0;
            int failed = 0;
            int skipped = 0;

            for (int i = 0; i < files.Count; i++)
            {
                string file = files[i];
                string full = Path.GetFullPath(file);

                if (_state.IsProcessed(full))
                {
                    skipped++;
                    Log.Info("Skipping already processed: " + file);
                    ConsoleUI.Progress(Path.GetFileName(file), i + 1, files.Count);
                    continue;
                }

                Status.Set("Processing " + Path.GetFileName(file));
                _state.SetLastFile(full);
                ConsoleUI.Progress(Path.GetFileName(file), i, files.Count);

                bool success = SafeProcess(file);
                if (success)
                {
                    _state.MarkProcessed(full);
                    ok++;
                }
                else
                {
                    failed++;
                }

                ConsoleUI.Progress(Path.GetFileName(file), i + 1, files.Count);
            }

            ConsoleUI.ProgressDone();
            Status.Set("Done");
            Log.Info(string.Format("Summary: ok={0}, failed={1}, skipped={2}", ok, failed, skipped));
            return failed == 0 ? 0 : 2;
        }

        void EnsureOutput()
        {
            Directory.CreateDirectory(_opts.OutputPath);
        }

        void EnsureConfig()
        {
            try
            {
                string cfg = Path.Combine(_opts.OutputPath, "zx2ilrecomp.config.ini");
                if (!File.Exists(cfg))
                {
                    StringBuilder sb = new StringBuilder();
                    sb.AppendLine("; ZX2ILRecomp auto-created config");
                    sb.AppendLine("Recursive=" + (_opts.Recursive ? "true" : "false"));
                    sb.AppendLine("CheckpointMinutes=" + _opts.CheckpointMinutes);
                    sb.AppendLine("SaveSource=" + (_opts.SaveSource ? "true" : "false"));
                    sb.AppendLine("NoCompile=" + (_opts.NoCompile ? "true" : "false"));
                    sb.AppendLine("Model=" + (_opts.Model == 0 ? "auto" : _opts.Model.ToString()));
                    File.WriteAllText(cfg, sb.ToString(), Encoding.UTF8);
                    Log.Info("Config created: " + cfg);
                }
            }
            catch (Exception ex)
            {
                Log.Warn("Failed to create config: " + ex.Message);
            }
        }

        List<string> CollectFiles()
        {
            List<string> files = new List<string>();

            try
            {
                if (File.Exists(_opts.InputPath))
                {
                    files.Add(Path.GetFullPath(_opts.InputPath));
                }
                else if (Directory.Exists(_opts.InputPath))
                {
                    SearchOption so = _opts.Recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
                    string[] all = Directory.GetFiles(_opts.InputPath, "*.*", so);
                    foreach (string f in all)
                    {
                        string ext = Path.GetExtension(f).ToLowerInvariant();
                        if (ext == ".z80" || ext == ".sna" || ext == ".tap" || ext == ".szx" || ext == ".tzx")
                            files.Add(Path.GetFullPath(f));
                    }
                }
                else
                {
                    Log.Error("Input path not found: " + _opts.InputPath);
                }
            }
            catch (Exception ex)
            {
                Log.Error("File collection error: " + ex.Message);
            }

            files.Sort(StringComparer.OrdinalIgnoreCase);
            return files;
        }

        bool SafeProcess(string file)
        {
            try
            {
                return ProcessFile(file);
            }
            catch (Exception ex)
            {
                Log.Error("File processing error: " + file);
                Log.Error(ex.Message);
                Log.Debug(ex.ToString());
                return false;
            }
        }

        bool ProcessFile(string file)
        {
            string romName = Path.GetFileNameWithoutExtension(file);
            string safeName = FileSystemUtil.SanitizeFileName(romName);
            string workDir = Path.Combine(_opts.OutputPath, safeName);
            string srcDir = Path.Combine(workDir, "src");

            Directory.CreateDirectory(workDir);
            if (_opts.SaveSource) Directory.CreateDirectory(srcDir);

            Log.Step("=== Processing snapshot: " + file + " ===");

            ZxSnapshot snap = ZxSnapshot.Load(file);
            if (snap.PC < 0x4000 && !snap.HasRomCode)
            {
                Log.Warn(string.Format(
                    "PC=0x{0:X4} points to ROM, but no usable ROM code is present. Forcing entry to 0x8000.",
                    snap.PC));

                snap.PC = 0x8000;
                snap.Entry = 0x8000;
            }
            if (_opts.Model == 48 || _opts.Model == 128)
                snap.Model = _opts.Model;

            Log.Info(string.Format(
                "Model={0}, PC=0x{1:X4}, SP=0x{2:X4}, AF=0x{3:X2}{4:X2}, BC=0x{5:X2}{6:X2}, DE=0x{7:X2}{8:X2}, HL=0x{9:X2}{10:X2}",
                snap.Model,
                snap.PC,
                snap.SP,
                snap.A,
                snap.F,
                snap.B,
                snap.C,
                snap.D,
                snap.E,
                snap.H,
                snap.L));

            DisassemblerZ80 dis = new DisassemblerZ80(snap);

            string dynamicTargetsPath = Path.Combine(workDir, "dynamic_targets.txt");
            if (!File.Exists(dynamicTargetsPath))
            {
                try
                {
                    File.WriteAllText(
                        dynamicTargetsPath,
                        "; Hex addresses for forced disassembly.\r\n" +
                        "; Format examples:\r\n" +
                        "; 8000\r\n" +
                        "; 0x8000\r\n" +
                        "; $8000\r\n" +
                        "\r\n",
                        Encoding.UTF8);
                    Log.Info("Dynamic targets file created: " + dynamicTargetsPath);
                }
                catch (Exception ex)
                {
                    Log.Warn("Failed to create dynamic_targets.txt: " + ex.Message);
                }
            }

            string[] dynamicTargetFiles = new string[]
            {
                Path.Combine(workDir, "dynamic_targets.txt"),
                Path.Combine(workDir, "dynamic_targets.log")
            };

            foreach (string dynamicTargetsFile in dynamicTargetFiles)
            {
                if (!File.Exists(dynamicTargetsFile))
                    continue;

                try
                {
                    foreach (string rawLine in File.ReadAllLines(dynamicTargetsFile))
                    {
                        string line = rawLine.Trim();
                        if (line.Length == 0)
                            continue;
                        if (line.StartsWith(";") || line.StartsWith("#"))
                            continue;

                        line = line.Replace("0x", "").Replace("$", "").Trim();
                        ushort addr;
                        if (ushort.TryParse(line, NumberStyles.HexNumber, null, out addr))
                        {
                            if (!dis.ForcedAddresses.Contains(addr))
                                dis.ForcedAddresses.Add(addr);
                        }
                    }
                }
                catch (Exception ex)
                {
                    Log.Warn("Failed to read dynamic targets file: " + ex.Message);
                }
            }

            if (dis.ForcedAddresses.Count > 0)
                Log.Info("Forced dynamic targets count: " + dis.ForcedAddresses.Count);

            AnalysisResultZ80 model = dis.Analyze();

            Log.Info(string.Format(
                "Analysis: instructions={0}, labels={1}, functions~={2}, unknownOps={3}, indirectJumps={4}",
                model.Instructions.Count,
                model.Labels.Count,
                model.Functions.Count,
                model.UnknownOpcodes.Count,
                model.IndirectJumps.Count));

            foreach (ushort addr in model.IndirectJumps)
                Log.Warn("Indirect JP at: 0x" + addr.ToString("X4"));

            foreach (byte op in model.UnknownOpcodes)
                Log.Warn("Unknown opcode: 0x" + op.ToString("X2"));

            LifterZ80 lifter = new LifterZ80(snap, model, safeName);
            string code = lifter.Generate();

            if (_opts.SaveSource)
            {
                string srcPath = Path.Combine(srcDir, "Game.generated.cs");
                File.WriteAllText(srcPath, code, Encoding.UTF8);
                Log.Ok("Intermediate C# saved: " + srcPath);
            }

            if (_opts.NoCompile)
            {
                Log.Warn("Compilation disabled (--no-compile).");
                return true;
            }

            string exePath = Path.Combine(workDir, safeName + ".exe");
            bool compiled = Compile(code, exePath);
            if (compiled)
                Log.Ok("Compiled: " + exePath);
            else
                Log.Error("Compilation of generated code failed. See log and src/Game.generated.cs.");

            return compiled;
        }

        bool Compile(string code, string exePath)
        {
            try
            {
                if (File.Exists(exePath))
                {
                    try { File.Delete(exePath); }
                    catch { }
                }

                CSharpCodeProvider provider = new CSharpCodeProvider();
                try
                {
                    CompilerParameters cp = new CompilerParameters();
                    cp.GenerateExecutable = true;
                    cp.OutputAssembly = exePath;
                    cp.IncludeDebugInformation = false;
                    cp.CompilerOptions = "/optimize- /nowarn:0162,0164,0219,0168,0414";
                    cp.ReferencedAssemblies.Add("System.dll");
                    cp.ReferencedAssemblies.Add("System.Drawing.dll");
                    cp.ReferencedAssemblies.Add("System.Windows.Forms.dll");

                    CompilerResults res = provider.CompileAssemblyFromSource(cp, code);
                    if (res.Errors.HasErrors)
                    {
                        foreach (CompilerError err in res.Errors)
                        {
                            if (err.IsWarning)
                            {
                                Log.Warn(string.Format("Compile warning {0}: {1}", err.ErrorNumber, err.ErrorText));
                            }
                            else
                            {
                                Log.Error(string.Format("Compile error {0} at ({1},{2}): {3}",
                                    err.ErrorNumber, err.Line, err.Column, err.ErrorText));
                            }
                        }
                        return false;
                    }

                    return true;
                }
                finally
                {
                    IDisposable d = provider as IDisposable;
                    if (d != null) d.Dispose();
                }
            }
            catch (Exception ex)
            {
                Log.Error("Compilation exception: " + ex.Message);
                Log.Debug(ex.ToString());
                return false;
            }
        }
    }

    public static class FileSystemUtil
    {
        public static string SanitizeFileName(string name)
        {
            if (string.IsNullOrEmpty(name)) return "rom";

            StringBuilder sb = new StringBuilder();
            char[] invalid = Path.GetInvalidFileNameChars();
            foreach (char c in name)
            {
                if (Array.IndexOf(invalid, c) >= 0) sb.Append('_');
                else sb.Append(c);
            }

            string s = sb.ToString().Trim();
            if (s.Length == 0) s = "rom";
            return s;
        }

        public static string SanitizeIdentifier(string name)
        {
            if (string.IsNullOrEmpty(name)) return "Game";

            StringBuilder sb = new StringBuilder();
            foreach (char c in name)
            {
                if (char.IsLetterOrDigit(c) || c == '_') sb.Append(c);
                else sb.Append('_');
            }

            if (sb.Length == 0) sb.Append("Game");
            if (char.IsDigit(sb[0])) sb.Insert(0, '_');
            return sb.ToString();
        }
    }

    public class ZxSnapshot
    {
        public int Model = 48;
        public byte A, F, B, C, D, E, H, L;
        public byte A2, F2, B2, C2, D2, E2, H2, L2;
        public ushort IX, IY, SP, PC;
        public byte I, R;
        public bool IFF1, IFF2;
        public int IM = 1;
        public byte Border = 7;
        public byte Port7FFD, Port1FFD;
        public byte[] Rom0 = new byte[16384];
        public byte[] Rom1 = new byte[16384];
        public byte[][] RamBanks = new byte[8][];
        public ushort Entry;
        public bool HasRomCode;

        public ZxSnapshot()
        {
            for (int i = 0; i < 8; i++)
                RamBanks[i] = new byte[16384];
        }

        public static ZxSnapshot Load(string path)
        {
            string ext = Path.GetExtension(path).ToLowerInvariant();
            byte[] data = File.ReadAllBytes(path);

            if (ext == ".z80")
                return LoadZ80(data);
            if (ext == ".sna")
                return LoadSna(data);
            if (ext == ".tap")
                return LoadTap(data);
            if (ext == ".szx")
                return LoadSZX(data);

            throw new InvalidDataException("Unsupported extension: " + ext);
        }

        static ushort Get16(byte[] d, int p)
        {
            if (p + 1 >= d.Length) return 0;
            return (ushort)(d[p] | (d[p + 1] << 8));
        }

        static ZxSnapshot LoadZ80(byte[] data)
        {
            if (data.Length < 30)
                throw new InvalidDataException("Z80 file too short.");

            ZxSnapshot s = new ZxSnapshot();

            s.A = data[0];
            s.F = data[1];
            s.C = data[2];
            s.B = data[3];
            s.E = data[4];
            s.D = data[5];
            s.L = data[6];
            s.H = data[7];

            ushort pc = Get16(data, 8);
            s.SP = Get16(data, 10);
            s.I = data[12];
            s.R = data[13];

            byte flags = data[14];
            s.Border = (byte)((flags >> 1) & 7);
            if ((flags & 1) != 0) s.R |= 0x80;

            s.E2 = data[15];
            s.D2 = data[16];
            s.C2 = data[17];
            s.B2 = data[18];
            s.L2 = data[19];
            s.H2 = data[20];
            s.F2 = data[21];
            s.A2 = data[22];

            s.IY = Get16(data, 23);
            s.IX = Get16(data, 25);
            s.IFF1 = data[27] != 0;
            s.IFF2 = data[28] != 0;

            byte flags2 = data[29];
            s.IM = flags2 & 3;

            if (pc != 0)
            {
                s.PC = pc;
                s.Model = 48;
                bool compressed = (flags & 0x20) != 0;
                byte[] ram = DecompressZ80V1(data, 30, compressed);
                s.SetRam48(ram);
            }
            else
            {
                int pos = 30;
                int hdrLen = Get16(data, pos);
                pos += 2;

                if (pos + hdrLen > data.Length)
                    throw new InvalidDataException("Bad Z80 v2/v3 header.");

                pc = Get16(data, pos);
                byte hw = data[pos + 2];
                s.PC = pc;

                if (hdrLen == 23)
                    s.Model = (hw >= 3 && hw <= 5) ? 128 : 48;
                else
                    s.Model = 128;

                if (hdrLen >= 4)
                    s.Port7FFD = data[pos + 3];
                if (hdrLen >= 5)
                    s.Port1FFD = data[pos + 4];

                pos += hdrLen;
                LoadZ80Pages(s, data, pos);
            }

            s.Entry = s.PC;
            if (s.Entry == 0) s.Entry = 0x8000;
            s.HasRomCode = !AllZero(s.Rom0) || !AllZero(s.Rom1);
            return s;
        }

        static byte[] DecompressZ80V1(byte[] data, int pos, bool compressed)
        {
            byte[] ram = new byte[49152];
            int o = 0;

            if (!compressed)
            {
                int len = Math.Min(ram.Length, data.Length - pos);
                if (len > 0) Array.Copy(data, pos, ram, 0, len);
                return ram;
            }

            int i = pos;
            while (o < ram.Length && i < data.Length)
            {
                if (i + 3 < data.Length && data[i] == 0xED && data[i + 1] == 0xED)
                {
                    int count = data[i + 2];
                    byte val = data[i + 3];
                    i += 4;

                    if (count == 0)
                        break;

                    for (int k = 0; k < count && o < ram.Length; k++)
                        ram[o++] = val;
                }
                else
                {
                    ram[o++] = data[i++];
                }
            }

            return ram;
        }

        static void LoadZ80Pages(ZxSnapshot s, byte[] data, int pos)
        {
            while (pos + 3 <= data.Length)
            {
                int len = Get16(data, pos);
                pos += 2;

                byte page = data[pos];
                pos++;

                int blockSize = (len == 0) ? 16384 : len;
                if (pos + blockSize > data.Length)
                    break;

                byte[] block = DecompressZ80Block(data, pos, blockSize);
                MapZ80Page(s, page, block);

                pos += blockSize;
            }
        }

        static byte[] DecompressZ80Block(byte[] data, int pos, int size)
        {
            byte[] outp = new byte[16384];
            int o = 0;
            int end = pos + size;
            int i = pos;

            if (size == 16384)
            {
                Array.Copy(data, pos, outp, 0, 16384);
                return outp;
            }

            while (o < outp.Length && i < end)
            {
                if (i + 3 < end && data[i] == 0xED && data[i + 1] == 0xED)
                {
                    int count = data[i + 2];
                    byte val = data[i + 3];
                    i += 4;

                    if (count == 0)
                        break;

                    for (int k = 0; k < count && o < outp.Length; k++)
                        outp[o++] = val;
                }
                else
                {
                    outp[o++] = data[i++];
                }
            }

            return outp;
        }

        static void MapZ80Page(ZxSnapshot s, byte page, byte[] block)
        {
            if (page == 4)
                s.RamBanks[2] = block;
            else if (page == 5)
                s.RamBanks[5] = block;
            else if (page == 8)
                s.RamBanks[0] = block;
            else if (page >= 3 && page <= 10)
                s.RamBanks[page - 3] = block;
            else if (page == 0 || page == 2)
                s.Rom0 = block;
            else if (page == 1 || page == 3)
                s.Rom1 = block;
        }

        static ZxSnapshot LoadSna(byte[] data)
        {
            if (data.Length < 27 + 49152)
                throw new InvalidDataException("SNA file too short.");

            ZxSnapshot s = new ZxSnapshot();
            s.Model = 48;

            s.I = data[0];

            s.L2 = data[1];
            s.H2 = data[2];
            s.E2 = data[3];
            s.D2 = data[4];
            s.C2 = data[5];
            s.B2 = data[6];
            s.F2 = data[7];
            s.A2 = data[8];

            s.L = data[9];
            s.H = data[10];
            s.E = data[11];
            s.D = data[12];
            s.C = data[13];
            s.B = data[14];

            s.IY = Get16(data, 15);
            s.IX = Get16(data, 17);

            byte iff = data[19];
            s.IFF1 = (iff & 4) != 0;
            s.IFF2 = s.IFF1;

            s.R = data[20];
            s.F = data[21];
            s.A = data[22];
            s.SP = Get16(data, 23);
            s.IM = data[25] & 3;
            s.Border = (byte)(data[26] & 7);

            byte[] ram = new byte[49152];
            Array.Copy(data, 27, ram, 0, 49152);
            s.SetRam48(ram);

            byte[] flat = s.GetFlat();
            if (s.SP < 0xFFFF)
            {
                s.PC = Get16(flat, s.SP);
                s.SP = (ushort)(s.SP + 2);
            }
            else
            {
                s.PC = 0x8000;
            }

            if (data.Length > 27 + 49152)
            {
                int pos = 27 + 49152;
                if (pos + 4 <= data.Length)
                {
                    int extraLen = Get16(data, pos);
                    pos += 2;

                    if (extraLen >= 4 && pos + 4 <= data.Length)
                    {
                        s.PC = Get16(data, pos);
                        pos += 2;
                        s.Port7FFD = data[pos];
                        s.Model = 128;
                    }
                }
            }

            s.Entry = s.PC;
            if (s.Entry == 0) s.Entry = 0x8000;
            s.HasRomCode = !AllZero(s.Rom0) || !AllZero(s.Rom1);
            return s;
        }

        static ZxSnapshot LoadTap(byte[] data)
        {
            ZxSnapshot s = new ZxSnapshot();
            s.Model = 48;

            byte[] flat = new byte[65536];
            int pos = 0;
            ushort entry = 0;
            bool haveEntry = false;

            int pendingStart = -1;
            int pendingLen = -1;

            while (pos + 2 <= data.Length)
            {
                int blockLen = Get16(data, pos);
                pos += 2;

                if (blockLen <= 0 || pos + blockLen > data.Length)
                    break;

                byte flag = data[pos];

                if (flag == 0x00 && blockLen >= 19)
                {
                    byte type = data[pos + 1];
                    int dlen = Get16(data, pos + 12);
                    int param1 = Get16(data, pos + 14);
                    int param2 = Get16(data, pos + 16);

                    if (type == 3)
                    {
                        pendingStart = param2;
                        pendingLen = dlen;

                        if (!haveEntry && param2 >= 0x4000)
                        {
                            entry = (ushort)param2;
                            haveEntry = true;
                        }
                    }
                    else if (type == 0)
                    {
                        pendingStart = 0x8000;
                        pendingLen = dlen;

                        if (!haveEntry && param1 < 32768)
                        {
                            entry = 0x8000;
                            haveEntry = true;
                        }
                    }
                    else
                    {
                        pendingStart = -1;
                    }
                }
                else if (flag == 0xFF && pendingStart >= 0)
                {
                    int dataLen = blockLen - 2;
                    int copy = Math.Min(dataLen, pendingLen);

                    for (int i = 0; i < copy && pos + 1 + i < data.Length; i++)
                    {
                        int addr = pendingStart + i;
                        if (addr >= 0x4000 && addr < 0x10000)
                            flat[addr] = data[pos + 1 + i];
                    }

                    pendingStart = -1;
                }

                pos += blockLen;
            }

            if (!haveEntry)
                entry = 0x8000;

            s.SetFlatRam(flat);
            s.PC = entry;
            s.Entry = entry;
            s.HasRomCode = false;
            return s;
        }

        static ZxSnapshot LoadSZX(byte[] data)
        {
            if (data.Length < 8)
                throw new InvalidDataException("SZX too short.");

            string sig = Encoding.ASCII.GetString(data, 0, 4);
            if (sig != "ZXST")
                throw new InvalidDataException("Bad SZX signature.");

            ZxSnapshot s = new ZxSnapshot();
            int pos = 8;

            while (pos + 8 <= data.Length)
            {
                string id = Encoding.ASCII.GetString(data, pos, 4);
                int blen = BitConverter.ToInt32(data, pos + 4);
                pos += 8;

                if (pos + blen > data.Length)
                    break;

                if (id == "Z80R" && blen >= 17)
                {
                    ushort pc = Get16(data, pos + 15);
                    if (pc != 0) s.PC = pc;
                }
                else if (id == "RAMP" && blen >= 16386)
                {
                    byte page = data[pos + 1];
                    byte[] block = new byte[16384];
                    int copy = Math.Min(16384, blen - 2);
                    Array.Copy(data, pos + 2, block, 0, copy);

                    if (page < 8)
                        s.RamBanks[page] = block;
                }

                pos += blen;
            }

            if (s.PC == 0) s.PC = 0x8000;
            s.Entry = s.PC;
            s.HasRomCode = !AllZero(s.Rom0) || !AllZero(s.Rom1);
            return s;
        }

        void SetRam48(byte[] ram48)
        {
            if (ram48 == null || ram48.Length < 49152)
                return;

            Array.Copy(ram48, 0, RamBanks[5], 0, 16384);
            Array.Copy(ram48, 16384, RamBanks[2], 0, 16384);
            Array.Copy(ram48, 32768, RamBanks[0], 0, 16384);
        }

        void SetFlatRam(byte[] flat)
        {
            Array.Copy(flat, 0x4000, RamBanks[5], 0, 16384);
            Array.Copy(flat, 0x8000, RamBanks[2], 0, 16384);
            Array.Copy(flat, 0xC000, RamBanks[0], 0, 16384);
        }

        public byte[] GetFlat()
        {
            byte[] m = new byte[65536];

            if (Model >= 128 && (Port7FFD & 0x10) != 0)
                Array.Copy(Rom1, 0, m, 0, 16384);
            else
                Array.Copy(Rom0, 0, m, 0, 16384);

            Array.Copy(RamBanks[5], 0, m, 0x4000, 16384);
            Array.Copy(RamBanks[2], 0, m, 0x8000, 16384);

            int bank = (Model >= 128) ? (Port7FFD & 7) : 0;
            Array.Copy(RamBanks[bank], 0, m, 0xC000, 16384);

            return m;
        }

        static bool AllZero(byte[] data)
        {
            if (data == null) return true;
            for (int i = 0; i < data.Length; i++)
            {
                if (data[i] != 0) return false;
            }
            return true;
        }
    }

    public enum OpControlZ80
    {
        Normal,
        Branch,
        Jmp,
        JmpInd,
        Call,
        Ret,
        Rst,
        Halt,
        Invalid
    }

    public class InstructionZ80
    {
        public ushort Address;
        public int Prefix;
        public byte Opcode;
        public int Length;
        public ushort Operand;
        public sbyte Displacement;
        public string Text;
        public OpControlZ80 Control;
        public ushort Target;
        public bool HasTarget;
        public ushort Fallthrough;
        public bool HasFallthrough;
        public bool Supported;
        public int Condition = -1;
        public bool IsDJNZ;
    }

    public class AnalysisResultZ80
    {
        public SortedDictionary<ushort, InstructionZ80> Instructions = new SortedDictionary<ushort, InstructionZ80>();
        public List<ushort> Labels = new List<ushort>();
        public List<ushort> Functions = new List<ushort>();
        public List<byte> UnknownOpcodes = new List<byte>();
        public List<ushort> IndirectJumps = new List<ushort>();
        public List<ushort> DynamicTargets = new List<ushort>();
        public ushort Entry;
    }

    public static class CpuZ80
    {
        public static byte[] BaseLen = new byte[256];
        public static byte[] EDLen = new byte[256];
        public static byte[] UsesIXYDisp = new byte[256];

        static CpuZ80()
        {
            Init();
        }

        static void Init()
        {
            int i;
            for (i = 0; i < 256; i++)
            {
                BaseLen[i] = 1;
                EDLen[i] = 2;
            }

            byte[] len3 = new byte[]
            {
                0x01,0x11,0x21,0x31,0x22,0x2A,0x32,0x3A,
                0xC2,0xC3,0xC4,0xCA,0xCC,0xCD,
                0xD2,0xDA,0xDC,
                0xE2,0xEA,0xEC,
                0xF2,0xFA,0xFC
            };

            byte[] len2 = new byte[]
            {
                0x06,0x0E,0x16,0x1E,0x26,0x2E,0x36,0x3E,
                0x10,0x18,0x20,0x28,0x30,0x38,
                0xC6,0xCE,0xD3,0xD6,0xDB,0xDE,0xE6,0xEE,0xF6,0xFE
            };

            foreach (byte op in len3) BaseLen[op] = 3;
            foreach (byte op in len2) BaseLen[op] = 2;

            byte[] ed4 = new byte[] { 0x43, 0x4B, 0x53, 0x5B, 0x63, 0x6B, 0x73, 0x7B };
            foreach (byte op in ed4) EDLen[op] = 4;

            byte[] ixy = new byte[]
            {
                0x34,0x35,0x36,
                0x46,0x4E,0x56,0x5E,0x66,0x6E,
                0x70,0x71,0x72,0x73,0x74,0x75,0x77,0x7E,
                0x86,0x8E,0x96,0x9E,0xA6,0xAE,0xB6,0xBE
            };

            foreach (byte op in ixy) UsesIXYDisp[op] = 1;
        }
    }

    public class DisassemblerZ80
    {
        ZxSnapshot _snap;
        byte[] _flat;
        Queue<ushort> _queue = new Queue<ushort>();
        Dictionary<ushort, bool> _seen = new Dictionary<ushort, bool>();
        AnalysisResultZ80 _result;
        public List<ushort> ForcedAddresses = new List<ushort>();

        public DisassemblerZ80(ZxSnapshot snap)
        {
            _snap = snap;
            _flat = snap.GetFlat();
        }

        public AnalysisResultZ80 Analyze()
        {
            _result = new AnalysisResultZ80();
            _queue.Clear();
            _seen.Clear();

            ushort entry = _snap.Entry;
            if (entry == 0) entry = 0x8000;
            _result.Entry = entry;

            Enqueue(entry);
            if (!_result.Functions.Contains(entry))
                _result.Functions.Add(entry);

            if (_snap.HasRomCode)
            {
                Enqueue(0x0000);
                Enqueue(0x0008);
                Enqueue(0x0010);
                Enqueue(0x0018);
                Enqueue(0x0020);
                Enqueue(0x0028);
                Enqueue(0x0030);
                Enqueue(0x0038);
            }

            foreach (ushort forced in ForcedAddresses)
            {
                Enqueue(forced);
                if (!_result.Functions.Contains(forced))
                    _result.Functions.Add(forced);
                if (!_result.DynamicTargets.Contains(forced))
                    _result.DynamicTargets.Add(forced);
            }

            int max = 200000;
            while (_queue.Count > 0 && _result.Instructions.Count < max)
            {
                ushort addr = _queue.Dequeue();
                if (_result.Instructions.ContainsKey(addr))
                    continue;

                InstructionZ80 inst = Decode(addr);
                _result.Instructions.Add(addr, inst);

                if (inst.Control == OpControlZ80.Invalid && !_result.UnknownOpcodes.Contains(inst.Opcode))
                    _result.UnknownOpcodes.Add(inst.Opcode);

                if (inst.Control == OpControlZ80.JmpInd && !_result.IndirectJumps.Contains(addr))
                    _result.IndirectJumps.Add(addr);

                if ((inst.Control == OpControlZ80.Call || inst.Control == OpControlZ80.Jmp) && inst.HasTarget)
                {
                    if (!_result.Functions.Contains(inst.Target))
                        _result.Functions.Add(inst.Target);
                }

                if (inst.HasTarget) Enqueue(inst.Target);
                if (inst.HasFallthrough) Enqueue(inst.Fallthrough);
            }

            if (_result.Instructions.Count >= max)
                Log.Warn("Instruction limit reached during disassembly.");

            _result.Labels = new List<ushort>(_result.Instructions.Keys);
            _result.Labels.Sort();

            return _result;
        }

        void Enqueue(ushort addr)
        {
            if (_seen.ContainsKey(addr)) return;
            _seen.Add(addr, true);
            _queue.Enqueue(addr);
        }

        byte Read8(ushort addr)
        {
            return _flat[addr];
        }

        InstructionZ80 Decode(ushort addr)
        {
            InstructionZ80 inst = new InstructionZ80();
            inst.Address = addr;

            byte b0 = Read8(addr);
            inst.Opcode = b0;

            if (b0 == 0xCB)
            {
                inst.Prefix = 0xCB;
                inst.Length = 2;
                inst.Opcode = Read8((ushort)(addr + 1));
                inst.Control = OpControlZ80.Normal;
                inst.HasFallthrough = true;
                inst.Fallthrough = (ushort)(addr + 2);
                inst.Supported = true;
                inst.Text = string.Format("{0:X4} CB {1:X2}", addr, inst.Opcode);
                return inst;
            }

            if (b0 == 0xED)
            {
                inst.Prefix = 0xED;
                byte op2 = Read8((ushort)(addr + 1));
                inst.Opcode = op2;
                inst.Length = CpuZ80.EDLen[op2];

                if (inst.Length == 4)
                    inst.Operand = (ushort)(Read8((ushort)(addr + 2)) | (Read8((ushort)(addr + 3)) << 8));

                if ((op2 & 0xC7) == 0x45)
                {
                    inst.Control = OpControlZ80.Ret;
                    inst.HasFallthrough = false;
                }
                else
                {
                    inst.Control = OpControlZ80.Normal;
                    inst.HasFallthrough = true;
                    inst.Fallthrough = (ushort)(addr + inst.Length);
                }

                inst.Supported = true;
                inst.Text = string.Format("{0:X4} ED {1:X2}", addr, op2);
                return inst;
            }

            if (b0 == 0xDD || b0 == 0xFD)
            {
                inst.Prefix = b0;
                byte op2 = Read8((ushort)(addr + 1));
                inst.Opcode = op2;

                if (op2 == 0xCB)
                {
                    inst.Prefix = (b0 == 0xDD) ? 0xDDCB : 0xFDCB;
                    inst.Length = 4;
                    inst.Displacement = (sbyte)Read8((ushort)(addr + 2));
                    inst.Opcode = Read8((ushort)(addr + 3));
                    inst.Control = OpControlZ80.Normal;
                    inst.HasFallthrough = true;
                    inst.Fallthrough = (ushort)(addr + 4);
                    inst.Supported = false;
                    inst.Text = string.Format("{0:X4} {1}CB {2:X2}", addr, b0 == 0xDD ? "DD" : "FD", inst.Opcode);
                    return inst;
                }

                inst.Length = PrefixedLength(b0, op2, addr);
                ParsePrefixedOperand(inst, addr);

                if (op2 == 0xE9)
                {
                    inst.Control = OpControlZ80.JmpInd;
                    inst.HasFallthrough = false;
                }
                else
                {
                    inst.Control = OpControlZ80.Normal;
                    inst.HasFallthrough = true;
                    inst.Fallthrough = (ushort)(addr + inst.Length);
                }

                inst.Supported = false;
                inst.Text = string.Format("{0:X4} {1} {2:X2}", addr, b0 == 0xDD ? "DD" : "FD", op2);
                return inst;
            }

            inst.Length = CpuZ80.BaseLen[b0];
            if (inst.Length >= 2)
                inst.Operand = Read8((ushort)(addr + 1));
            if (inst.Length == 3)
                inst.Operand |= (ushort)(Read8((ushort)(addr + 2)) << 8);

            SetBaseControl(inst, addr);
            inst.Supported = true;
            inst.Text = string.Format("{0:X4} {1:X2}", addr, b0);
            return inst;
        }

        int PrefixedLength(byte prefix, byte op, ushort addr)
        {
            if (op == 0xCB) return 4;
            if (op == 0xDD || op == 0xFD || op == 0xED) return 1;

            if (CpuZ80.UsesIXYDisp[op] != 0)
            {
                if (op == 0x36) return 4;
                return 3;
            }

            int baseLen = CpuZ80.BaseLen[op];
            return baseLen + 1;
        }

        void ParsePrefixedOperand(InstructionZ80 inst, ushort addr)
        {
            byte op2 = inst.Opcode;

            if (CpuZ80.UsesIXYDisp[op2] != 0)
            {
                inst.Displacement = (sbyte)Read8((ushort)(addr + 2));
                if (inst.Length == 4)
                    inst.Operand = Read8((ushort)(addr + 3));
                return;
            }

            if (inst.Length >= 3)
            {
                if (CpuZ80.BaseLen[op2] == 3 || op2 == 0x21 || op2 == 0x22 || op2 == 0x2A || op2 == 0x2B)
                    inst.Operand = (ushort)(Read8((ushort)(addr + 2)) | (Read8((ushort)(addr + 3)) << 8));
                else
                    inst.Operand = Read8((ushort)(addr + 2));
            }
        }

        void SetBaseControl(InstructionZ80 inst, ushort addr)
        {
            byte op = inst.Opcode;
            ushort operand = inst.Operand;
            int len = inst.Length;

            inst.Control = OpControlZ80.Normal;
            inst.HasFallthrough = true;
            inst.Fallthrough = (ushort)(addr + len);

            switch (op)
            {
                case 0xC3:
                    inst.Control = OpControlZ80.Jmp;
                    inst.Target = operand;
                    inst.HasTarget = true;
                    inst.HasFallthrough = false;
                    break;

                case 0xC2:
                case 0xCA:
                case 0xD2:
                case 0xDA:
                case 0xE2:
                case 0xEA:
                case 0xF2:
                case 0xFA:
                    inst.Control = OpControlZ80.Jmp;
                    inst.Condition = (op >> 3) & 7;
                    inst.Target = operand;
                    inst.HasTarget = true;
                    inst.HasFallthrough = true;
                    inst.Fallthrough = (ushort)(addr + 3);
                    break;

                case 0x18:
                    inst.Control = OpControlZ80.Branch;
                    inst.Target = (ushort)(addr + 2 + (sbyte)operand);
                    inst.HasTarget = true;
                    inst.HasFallthrough = true;
                    inst.Fallthrough = (ushort)(addr + 2);
                    break;

                case 0x10:
                    inst.Control = OpControlZ80.Branch;
                    inst.IsDJNZ = true;
                    inst.Target = (ushort)(addr + 2 + (sbyte)operand);
                    inst.HasTarget = true;
                    inst.HasFallthrough = true;
                    inst.Fallthrough = (ushort)(addr + 2);
                    break;

                case 0x20:
                case 0x28:
                case 0x30:
                case 0x38:
                    inst.Control = OpControlZ80.Branch;
                    inst.Condition = (op >> 3) & 3;
                    inst.Target = (ushort)(addr + 2 + (sbyte)operand);
                    inst.HasTarget = true;
                    inst.HasFallthrough = true;
                    inst.Fallthrough = (ushort)(addr + 2);
                    break;

                case 0xCD:
                    inst.Control = OpControlZ80.Call;
                    inst.Target = operand;
                    inst.HasTarget = true;
                    inst.HasFallthrough = true;
                    inst.Fallthrough = (ushort)(addr + 3);
                    break;

                case 0xC4:
                case 0xCC:
                case 0xD4:
                case 0xDC:
                case 0xE4:
                case 0xEC:
                case 0xF4:
                case 0xFC:
                    inst.Control = OpControlZ80.Call;
                    inst.Condition = (op >> 3) & 7;
                    inst.Target = operand;
                    inst.HasTarget = true;
                    inst.HasFallthrough = true;
                    inst.Fallthrough = (ushort)(addr + 3);
                    break;

                case 0xC9:
                    inst.Control = OpControlZ80.Ret;
                    inst.HasFallthrough = false;
                    break;

                case 0xC0:
                case 0xC8:
                case 0xD0:
                case 0xD8:
                case 0xE0:
                case 0xE8:
                case 0xF0:
                case 0xF8:
                    inst.Control = OpControlZ80.Ret;
                    inst.Condition = (op >> 3) & 7;
                    inst.HasFallthrough = true;
                    inst.Fallthrough = (ushort)(addr + 1);
                    break;

                case 0xC7:
                case 0xCF:
                case 0xD7:
                case 0xDF:
                case 0xE7:
                case 0xEF:
                case 0xF7:
                case 0xFF:
                    inst.Control = OpControlZ80.Rst;
                    inst.Target = (ushort)(op & 0x38);
                    inst.HasTarget = true;
                    inst.HasFallthrough = false;
                    break;

                case 0xE9:
                    inst.Control = OpControlZ80.JmpInd;
                    inst.HasFallthrough = false;
                    break;

                case 0x76:
                    inst.Control = OpControlZ80.Halt;
                    inst.HasFallthrough = true;
                    inst.Fallthrough = (ushort)(addr + 1);
                    break;
            }
        }
    }

    public class LifterZ80
    {
        ZxSnapshot _snap;
        AnalysisResultZ80 _model;
        string _nsName;
        Dictionary<ushort, bool> _labels = new Dictionary<ushort, bool>();
        List<ushort> _emitted = new List<ushort>();

        public LifterZ80(ZxSnapshot snap, AnalysisResultZ80 model, string gameName)
        {
            _snap = snap;
            _model = model;
            _nsName = FileSystemUtil.SanitizeIdentifier(gameName);

            foreach (ushort a in _model.Labels)
                _labels[a] = true;

            _emitted = new List<ushort>(_labels.Keys);
            _emitted.Sort();

            // ==========================================================
            // Anti-CS1647 cap.
            // Если статических label слишком много, компилятор C# может
            // упасть с CS1647. Оставляем самые важные адреса, остальные
            // будут выполняться через dynamic fallback.
            // ==========================================================
            const int MAX_STATIC_LABELS = 4096;

            if (_emitted.Count > MAX_STATIC_LABELS)
            {
                List<ushort> kept = new List<ushort>();

                if (_labels.ContainsKey(_model.Entry) && !kept.Contains(_model.Entry))
                    kept.Add(_model.Entry);

                foreach (ushort a in _model.Functions)
                {
                    if (kept.Count >= MAX_STATIC_LABELS) break;
                    if (_labels.ContainsKey(a) && !kept.Contains(a))
                        kept.Add(a);
                }

                foreach (ushort a in _model.DynamicTargets)
                {
                    if (kept.Count >= MAX_STATIC_LABELS) break;
                    if (_labels.ContainsKey(a) && !kept.Contains(a))
                        kept.Add(a);
                }

                foreach (ushort a in _emitted)
                {
                    if (kept.Count >= MAX_STATIC_LABELS) break;
                    if (!kept.Contains(a))
                        kept.Add(a);
                }

                kept.Sort();
                _emitted = kept;

                _labels.Clear();
                foreach (ushort a in _emitted)
                    _labels[a] = true;

                Log.Warn("Static label count capped to " + MAX_STATIC_LABELS + " to avoid CS1647.");
            }
        }

        public string Generate()
        {
            StringBuilder sb = new StringBuilder();

            sb.AppendLine("// <auto-generated>");
            sb.AppendLine("// ZX Spectrum static recompiler generated code.");
            sb.AppendLine("// Runtime: .NET Framework. ULA/Beeper/AY/Keyboard are stubbed.");
            sb.AppendLine("// </auto-generated>");
            sb.AppendLine();
            sb.AppendLine("using System;");
            sb.AppendLine("using System.Drawing;");
            sb.AppendLine("using System.Drawing.Imaging;");
            sb.AppendLine("using System.Windows.Forms;");
            sb.AppendLine("using System.Threading;");
            sb.AppendLine("using System.Runtime.InteropServices;");
            sb.AppendLine("using System.Collections.Generic;");
            sb.AppendLine();
            sb.AppendLine("namespace Lifted." + _nsName);
            sb.AppendLine("{");

            AppendProgram(sb);
            AppendRuntime(sb);
            AppendMemory(sb);
            AppendUla(sb);
            AppendAyu(sb);
            AppendAudio(sb);
            AppendForm(sb);

            sb.AppendLine("}");
            return sb.ToString();
        }

        void AppendProgram(StringBuilder sb)
        {
            sb.AppendLine("static class Program");
            sb.AppendLine("{");
            sb.AppendLine("[STAThread]");
            sb.AppendLine("static void Main()");
            sb.AppendLine("{");
            sb.AppendLine("try");
            sb.AppendLine("{");
            sb.AppendLine("Application.EnableVisualStyles();");
            sb.AppendLine("Runtime.Init();");
            sb.AppendLine("UlaForm form = new UlaForm();");
            sb.AppendLine("Thread cpuThread = new Thread(new ThreadStart(Runtime.CpuThread));");
            sb.AppendLine("cpuThread.IsBackground = true;");
            sb.AppendLine("cpuThread.Priority = ThreadPriority.BelowNormal;");
            sb.AppendLine("cpuThread.Start();");
            sb.AppendLine("Application.Run(form);");
            sb.AppendLine("}");
            sb.AppendLine("catch (Exception ex)");
            sb.AppendLine("{");
            sb.AppendLine("try { System.IO.File.WriteAllText(System.IO.Path.Combine(System.AppDomain.CurrentDomain.BaseDirectory, \"fatal.log\"), ex.ToString()); } catch { }");
            sb.AppendLine("MessageBox.Show(ex.ToString(), \"ZX Lifted fatal\", MessageBoxButtons.OK, MessageBoxIcon.Error);");
            sb.AppendLine("}");
            sb.AppendLine("}");
            sb.AppendLine("}");
            sb.AppendLine();
        }

        void AppendRuntime(StringBuilder sb)
        {
            sb.AppendLine("static class Runtime");
            sb.AppendLine("{");

            sb.AppendLine("public static string GameName = \"" + _nsName + "\";");
            sb.AppendLine("public static int Model = " + _snap.Model + ";");
            sb.AppendLine("public static byte A, F, B, C, D, E, H, L;");
            sb.AppendLine("public static byte A2, F2, B2, C2, D2, E2, H2, L2;");
            sb.AppendLine("public static ushort IX, IY, SP, PC;");
            sb.AppendLine("public static byte I, R;");
            sb.AppendLine("public static bool IFF1, IFF2;");
            sb.AppendLine("public static int IM;");
            sb.AppendLine("public static bool Halted;");
            sb.AppendLine("public static volatile bool InterruptPending;");
            sb.AppendLine("public static byte Border, Port7FFD, Port1FFD;");
            sb.AppendLine("public static bool PagingDisabled;");
            sb.AppendLine("public static volatile bool BeeperLevel;");
            sb.AppendLine("public static byte[] Rom0;");
            sb.AppendLine("public static byte[] Rom1;");
            sb.AppendLine("public static byte[][] InitialRam = new byte[8][];");
            sb.AppendLine("public static byte[][] RamBanks = new byte[8][];");
            sb.AppendLine("public static byte[] KeyMatrix = new byte[8];");
            sb.AppendLine("public static byte Kempston;");
            sb.AppendLine("public static byte[] Parity = new byte[256];");
            sb.AppendLine("public static long InsCount;");
            sb.AppendLine("public static ushort LastPC;");
            sb.AppendLine("public static ushort DispatchTarget;");
            sb.AppendLine("public static string LastError;");
            sb.AppendLine("public static volatile string CpuState;");
            sb.AppendLine("public static volatile int TrapCount;");
            sb.AppendLine("public static string LastTrap;");
            sb.AppendLine("public static object DynLock = new object();");
            sb.AppendLine("public static Dictionary<ushort, bool> SeenDynamic = new Dictionary<ushort, bool>();");
            sb.AppendLine("public static object FrameLock = new object();");
            sb.AppendLine("public static long ClockStart = System.Diagnostics.Stopwatch.GetTimestamp();");
            sb.AppendLine("public static volatile bool ThrottleEnabled = true;");

            AppendRuntimeTables(sb);
            AppendInitMemory(sb);

            sb.AppendLine("public static ushort BC { get { return (ushort)((B << 8) | C); } set { B = (byte)(value >> 8); C = (byte)value; } }");
            sb.AppendLine("public static ushort DE { get { return (ushort)((D << 8) | E); } set { D = (byte)(value >> 8); E = (byte)value; } }");
            sb.AppendLine("public static ushort HL { get { return (ushort)((H << 8) | L); } set { H = (byte)(value >> 8); L = (byte)value; } }");
            sb.AppendLine("public static ushort AF { get { return (ushort)((A << 8) | F); } set { A = (byte)(value >> 8); F = (byte)value; } }");

            sb.AppendLine("public static void Init()");
            sb.AppendLine("{");
            sb.AppendLine("InitMemory();");
            sb.AppendLine("InitParity();");
            sb.AppendLine("A = 0x" + _snap.A.ToString("X2") + "; F = 0x" + _snap.F.ToString("X2") + ";");
            sb.AppendLine("B = 0x" + _snap.B.ToString("X2") + "; C = 0x" + _snap.C.ToString("X2") + ";");
            sb.AppendLine("D = 0x" + _snap.D.ToString("X2") + "; E = 0x" + _snap.E.ToString("X2") + ";");
            sb.AppendLine("H = 0x" + _snap.H.ToString("X2") + "; L = 0x" + _snap.L.ToString("X2") + ";");
            sb.AppendLine("A2 = 0x" + _snap.A2.ToString("X2") + "; F2 = 0x" + _snap.F2.ToString("X2") + ";");
            sb.AppendLine("B2 = 0x" + _snap.B2.ToString("X2") + "; C2 = 0x" + _snap.C2.ToString("X2") + ";");
            sb.AppendLine("D2 = 0x" + _snap.D2.ToString("X2") + "; E2 = 0x" + _snap.E2.ToString("X2") + ";");
            sb.AppendLine("H2 = 0x" + _snap.H2.ToString("X2") + "; L2 = 0x" + _snap.L2.ToString("X2") + ";");
            sb.AppendLine("IX = 0x" + _snap.IX.ToString("X4") + "; IY = 0x" + _snap.IY.ToString("X4") + ";");
            sb.AppendLine("SP = 0x" + _snap.SP.ToString("X4") + "; PC = 0x" + _snap.PC.ToString("X4") + ";");
            sb.AppendLine("I = 0x" + _snap.I.ToString("X2") + "; R = 0x" + _snap.R.ToString("X2") + ";");
            sb.AppendLine("IFF1 = " + (_snap.IFF1 ? "true" : "false") + "; IFF2 = " + (_snap.IFF2 ? "true" : "false") + ";");
            sb.AppendLine("IM = " + _snap.IM + ";");
            sb.AppendLine("Border = 0x" + _snap.Border.ToString("X2") + ";");
            sb.AppendLine("Port7FFD = 0x" + _snap.Port7FFD.ToString("X2") + ";");
            sb.AppendLine("Port1FFD = 0x" + _snap.Port1FFD.ToString("X2") + ";");
            sb.AppendLine("DispatchTarget = PC; CpuState = \"Init\";");
            sb.AppendLine("for (int i = 0; i < 8; i++) KeyMatrix[i] = 0xFF;");
            sb.AppendLine("Memory.Reset();");
            sb.AppendLine("Ula.Reset();");
            sb.AppendLine("Audio.TryStart();");
            sb.AppendLine("}");

            sb.AppendLine("public static void InitParity()");
            sb.AppendLine("{");
            sb.AppendLine("for (int i = 0; i < 256; i++)");
            sb.AppendLine("{");
            sb.AppendLine("int c = 0;");
            sb.AppendLine("for (int b = 0; b < 8; b++) if ((i & (1 << b)) != 0) c++;");
            sb.AppendLine("Parity[i] = (byte)((c % 2 == 0) ? 4 : 0);");
            sb.AppendLine("}");
            sb.AppendLine("}");

            sb.AppendLine("public static void CpuThread()");
            sb.AppendLine("{");
            sb.AppendLine("try { CpuState = \"Starting\"; Reset(); CpuState = \"EnteringRun\"; Run(); CpuState = LastError != null ? \"Trap\" : \"Exited\"; }");
            sb.AppendLine("catch (Exception ex) { CpuState = \"Exception\"; OnCpuException(ex); }");
            sb.AppendLine("}");

            sb.AppendLine("public static void Reset()");
            sb.AppendLine("{");
            sb.AppendLine("LastError = null; LastTrap = null; InsCount = 0; LastPC = 0; TrapCount = 0; DispatchTarget = PC; CpuState = \"Reset\";");
            sb.AppendLine("if (SeenDynamic != null) SeenDynamic.Clear();");
            sb.AppendLine("Memory.Reset();");
            sb.AppendLine("}");

            AppendRun(sb);
            AppendCpuHelpers(sb);
            AppendExecMethods(sb);
            AppendDynamicExecutor(sb);

            sb.AppendLine("}");
            sb.AppendLine();
        }

        void AppendRuntimeTables(StringBuilder sb)
        {
            sb.Append("public static readonly byte[] BaseLen = new byte[256]{");
            for (int i = 0; i < 256; i++)
            {
                if (i > 0) sb.Append(",");
                sb.Append(CpuZ80.BaseLen[i].ToString(CultureInfo.InvariantCulture));
            }
            sb.AppendLine("};");

            sb.Append("public static readonly byte[] EDLen = new byte[256]{");
            for (int i = 0; i < 256; i++)
            {
                if (i > 0) sb.Append(",");
                sb.Append(CpuZ80.EDLen[i].ToString(CultureInfo.InvariantCulture));
            }
            sb.AppendLine("};");

            sb.Append("public static readonly byte[] UsesIXYDisp = new byte[256]{");
            for (int i = 0; i < 256; i++)
            {
                if (i > 0) sb.Append(",");
                sb.Append(CpuZ80.UsesIXYDisp[i].ToString(CultureInfo.InvariantCulture));
            }
            sb.AppendLine("};");
        }

        void AppendInitMemory(StringBuilder sb)
        {
            sb.AppendLine("public static void InitMemory()");
            sb.AppendLine("{");
            AppendByteArrayAssignment(sb, "Rom0", _snap.Rom0);
            AppendByteArrayAssignment(sb, "Rom1", _snap.Rom1);

            for (int i = 0; i < 8; i++)
            {
                byte[] bank = _snap.RamBanks[i];
                if (bank == null) bank = new byte[16384];
                AppendByteArrayAssignment(sb, "InitialRam[" + i + "]", bank);
            }

            sb.AppendLine("for (int i = 0; i < 8; i++) RamBanks[i] = new byte[16384];");
            sb.AppendLine("}");
        }

        void AppendByteArrayAssignment(StringBuilder sb, string field, byte[] data)
        {
            if (data == null || data.Length == 0)
            {
                sb.AppendLine(field + " = new byte[16384];");
                return;
            }

            string b64 = Convert.ToBase64String(data);
            sb.AppendLine(field + " = Convert.FromBase64String(");
            int chunk = 32768;

            for (int i = 0; i < b64.Length; i += chunk)
            {
                int len = Math.Min(chunk, b64.Length - i);
                sb.Append("\"" + b64.Substring(i, len) + "\"");
                if (i + chunk < b64.Length) sb.AppendLine(" +");
                else sb.AppendLine(");");
            }
        }

        void AppendRun(StringBuilder sb)
        {
            bool hasEntry = _model.Entry != 0 && _labels.ContainsKey(_model.Entry);
            ushort entry = hasEntry ? _model.Entry : (ushort)0x8000;

            sb.AppendLine("public static void Run()");
            sb.AppendLine("{");
            sb.AppendLine("try");
            sb.AppendLine("{");
            Line(sb, "Runtime.CpuState = \"Running\";");
            Line(sb, "ushort pc = 0x" + entry.ToString("X4") + ";");

            if (!hasEntry)
                Line(sb, "Runtime.Trap(\"No valid entry point.\"); return;");

            sb.AppendLine("while (true)");
            sb.AppendLine("{");
            Line(sb, "Runtime.LastPC = pc;");
            Line(sb, "Runtime.InsCount++;");
            Line(sb, "if (Runtime.CheckInterrupt(pc)) pc = Runtime.DispatchTarget;");
            Line(sb, "else pc = StepStatic(pc);");
            sb.AppendLine("}");
            sb.AppendLine("}");
            sb.AppendLine("catch (Exception ex) { OnCpuException(ex); }");
            sb.AppendLine("}");
            sb.AppendLine();

            Dictionary<int, List<ushort>> pages = new Dictionary<int, List<ushort>>();

            foreach (ushort a in _emitted)
            {
                int page = a >> 8;
                List<ushort> list;
                if (!pages.TryGetValue(page, out list))
                {
                    list = new List<ushort>();
                    pages[page] = list;
                }
                list.Add(a);
            }

            List<int> pageKeys = new List<int>(pages.Keys);
            pageKeys.Sort();

            sb.AppendLine("static ushort StepStatic(ushort pc)");
            sb.AppendLine("{");
            sb.AppendLine("switch (pc >> 8)");
            sb.AppendLine("{");

            foreach (int page in pageKeys)
            {
                Line(sb, "case 0x" + page.ToString("X2") + ": return StepPage" + page.ToString("X2") + "(pc);");
            }

            Line(sb, "default: return StepDynamic(pc);");
            sb.AppendLine("}");
            sb.AppendLine("}");
            sb.AppendLine();

            sb.AppendLine("static ushort StepDynamic(ushort pc)");
            sb.AppendLine("{");
            Line(sb, "Runtime.ReportDynamicTarget(pc);");
            Line(sb, "Runtime.DispatchTarget = pc;");
            Line(sb, "if (!Runtime.ExecDynamicOne(Runtime.DispatchTarget)) { Runtime.Trap(\"Dynamic execution failed at $\" + pc.ToString(\"X4\")); return 0; }");
            Line(sb, "return Runtime.DispatchTarget;");
            sb.AppendLine("}");
            sb.AppendLine();

            foreach (int page in pageKeys)
            {
                sb.AppendLine("static ushort StepPage" + page.ToString("X2") + "(ushort pc)");
                sb.AppendLine("{");
                sb.AppendLine("switch (pc)");
                sb.AppendLine("{");

                foreach (ushort a in pages[page])
                {
                    Line(sb, "case 0x" + a.ToString("X4") + ": return L" + a.ToString("X4") + "();");
                }

                Line(sb, "default: return StepDynamic(pc);");
                sb.AppendLine("}");
                sb.AppendLine("}");
                sb.AppendLine();
            }

            foreach (ushort addr in _emitted)
            {
                sb.AppendLine("static ushort L" + addr.ToString("X4") + "()");
                sb.AppendLine("{");

                InstructionZ80 inst;
                if (_model.Instructions.TryGetValue(addr, out inst))
                {
                    Line(sb, "// " + inst.Text);
                    EmitInstruction(sb, inst);
                }
                else
                {
                    Line(sb, "Runtime.Trap(\"Label without instruction.\"); return 0;");
                }

                sb.AppendLine("}");
                sb.AppendLine();
            }
        }

        void AppendCpuHelpers(StringBuilder sb)
        {
            sb.AppendLine("public static void SetTrap(string message)");
            sb.AppendLine("{");
            sb.AppendLine("LastError = message;");
            sb.AppendLine("CpuState = \"Trap\";");
            sb.AppendLine("if (LastTrap != message)");
            sb.AppendLine("{");
            sb.AppendLine("LastTrap = message;");
            sb.AppendLine("TrapCount++;");
            sb.AppendLine("Console.WriteLine(message);");
            sb.AppendLine("try { System.IO.File.AppendAllText(System.IO.Path.Combine(System.AppDomain.CurrentDomain.BaseDirectory, \"cpu_trap.log\"), DateTime.Now.ToString(\"s\") + \" \" + message + Environment.NewLine); } catch { }");
            sb.AppendLine("}");
            sb.AppendLine("throw new Exception(message);");
            sb.AppendLine("}");

            sb.AppendLine("public static void Trap(string message) { SetTrap(message); }");
            sb.AppendLine("public static void UnknownOpcode(byte op, ushort addr) { SetTrap(string.Format(\"Unknown opcode ${0:X2} at ${1:X4}\", op, addr)); }");
            sb.AppendLine("public static void OnCpuException(Exception ex) { SetTrap(\"CPU exception: \" + ex.ToString()); }");

            sb.AppendLine("public static void ReportDynamicTarget(ushort addr)");
            sb.AppendLine("{");
            sb.AppendLine("lock (DynLock)");
            sb.AppendLine("{");
            sb.AppendLine("if (!SeenDynamic.ContainsKey(addr))");
            sb.AppendLine("{");
            sb.AppendLine("SeenDynamic.Add(addr, true);");
            sb.AppendLine("try { System.IO.File.AppendAllText(System.IO.Path.Combine(System.AppDomain.CurrentDomain.BaseDirectory, \"dynamic_targets.log\"), addr.ToString(\"X4\") + System.Environment.NewLine); } catch { }");
            sb.AppendLine("}");
            sb.AppendLine("}");
            sb.AppendLine("}");

            sb.AppendLine("public static bool Conditional(int cc)");
            sb.AppendLine("{");
            sb.AppendLine("switch (cc)");
            sb.AppendLine("{");
            sb.AppendLine("case 0: return (F & 0x40) == 0;");
            sb.AppendLine("case 1: return (F & 0x40) != 0;");
            sb.AppendLine("case 2: return (F & 0x01) == 0;");
            sb.AppendLine("case 3: return (F & 0x01) != 0;");
            sb.AppendLine("case 4: return (F & 0x04) == 0;");
            sb.AppendLine("case 5: return (F & 0x04) != 0;");
            sb.AppendLine("case 6: return (F & 0x80) == 0;");
            sb.AppendLine("case 7: return (F & 0x80) != 0;");
            sb.AppendLine("default: return true;");
            sb.AppendLine("}");
            sb.AppendLine("}");

            sb.AppendLine("public static void Push16(ushort v)");
            sb.AppendLine("{");
            sb.AppendLine("SP = (ushort)(SP - 1); Memory.Write(SP, (byte)(v >> 8));");
            sb.AppendLine("SP = (ushort)(SP - 1); Memory.Write(SP, (byte)(v & 0xFF));");
            sb.AppendLine("}");

            sb.AppendLine("public static ushort Pop16()");
            sb.AppendLine("{");
            sb.AppendLine("byte lo = Memory.Read(SP); SP = (ushort)(SP + 1);");
            sb.AppendLine("byte hi = Memory.Read(SP); SP = (ushort)(SP + 1);");
            sb.AppendLine("return (ushort)(lo | (hi << 8));");
            sb.AppendLine("}");

            sb.AppendLine("public static byte GetR8(int r)");
            sb.AppendLine("{");
            sb.AppendLine("switch (r)");
            sb.AppendLine("{");
            sb.AppendLine("case 0: return B; case 1: return C; case 2: return D; case 3: return E;");
            sb.AppendLine("case 4: return H; case 5: return L; case 6: return Memory.Read(HL); case 7: return A;");
            sb.AppendLine("default: return 0;");
            sb.AppendLine("}");
            sb.AppendLine("}");

            sb.AppendLine("public static void SetR8(int r, byte v)");
            sb.AppendLine("{");
            sb.AppendLine("switch (r)");
            sb.AppendLine("{");
            sb.AppendLine("case 0: B = v; break; case 1: C = v; break; case 2: D = v; break; case 3: E = v; break;");
            sb.AppendLine("case 4: H = v; break; case 5: L = v; break; case 6: Memory.Write(HL, v); break; case 7: A = v; break;");
            sb.AppendLine("}");
            sb.AppendLine("}");

            sb.AppendLine("public static ushort GetRP16(int rp)");
            sb.AppendLine("{");
            sb.AppendLine("switch (rp) { case 0: return BC; case 1: return DE; case 2: return HL; case 3: return AF; default: return 0; }");
            sb.AppendLine("}");

            sb.AppendLine("public static void SetRP16(int rp, ushort v)");
            sb.AppendLine("{");
            sb.AppendLine("switch (rp) { case 0: BC = v; break; case 1: DE = v; break; case 2: HL = v; break; case 3: AF = v; break; }");
            sb.AppendLine("}");

            sb.AppendLine("public static ushort GetRP16Arith(int rp)");
            sb.AppendLine("{");
            sb.AppendLine("switch (rp) { case 0: return BC; case 1: return DE; case 2: return HL; case 3: return SP; default: return 0; }");
            sb.AppendLine("}");

            sb.AppendLine("public static void SetRP16Arith(int rp, ushort v)");
            sb.AppendLine("{");
            sb.AppendLine("switch (rp) { case 0: BC = v; break; case 1: DE = v; break; case 2: HL = v; break; case 3: SP = v; break; }");
            sb.AppendLine("}");

            sb.AppendLine("public static void SetSZ(byte v) { F = (byte)((F & 0x29) | (v == 0 ? 0x40 : 0) | (v & 0x80)); }");

            sb.AppendLine("public static byte Inc8(byte v)");
            sb.AppendLine("{");
            sb.AppendLine("int res = v + 1;");
            sb.AppendLine("bool h = (v & 0x0F) == 0x0F;");
            sb.AppendLine("bool pv = v == 0x7F;");
            sb.AppendLine("F = (byte)((F & 0x01) | ((byte)res & 0x80) | ((byte)res == 0 ? 0x40 : 0) | (h ? 0x10 : 0) | (pv ? 0x04 : 0));");
            sb.AppendLine("return (byte)res;");
            sb.AppendLine("}");

            sb.AppendLine("public static byte Dec8(byte v)");
            sb.AppendLine("{");
            sb.AppendLine("int res = v - 1;");
            sb.AppendLine("bool h = (v & 0x0F) == 0;");
            sb.AppendLine("bool pv = v == 0x80;");
            sb.AppendLine("F = (byte)((F & 0x01) | ((byte)res & 0x80) | ((byte)res == 0 ? 0x40 : 0) | (h ? 0x10 : 0) | (pv ? 0x04 : 0) | 0x02);");
            sb.AppendLine("return (byte)res;");
            sb.AppendLine("}");

            sb.AppendLine("public static void AddA(byte v)");
            sb.AppendLine("{");
            sb.AppendLine("int aa = A; int res = aa + v;");
            sb.AppendLine("bool c = res > 0xFF;");
            sb.AppendLine("bool h = ((aa & 0x0F) + (v & 0x0F)) > 0x0F;");
            sb.AppendLine("bool pv = ((~(aa ^ v) & (aa ^ (byte)res)) & 0x80) != 0;");
            sb.AppendLine("A = (byte)res;");
            sb.AppendLine("F = (byte)((A & 0x80) | (A == 0 ? 0x40 : 0) | (h ? 0x10 : 0) | (pv ? 0x04 : 0) | (c ? 0x01 : 0));");
            sb.AppendLine("}");

            sb.AppendLine("public static void AdcA(byte v)");
            sb.AppendLine("{");
            sb.AppendLine("int aa = A; int carry = F & 1; int res = aa + v + carry;");
            sb.AppendLine("bool c = res > 0xFF;");
            sb.AppendLine("bool h = ((aa & 0x0F) + (v & 0x0F) + carry) > 0x0F;");
            sb.AppendLine("bool pv = ((~(aa ^ v) & (aa ^ (byte)res)) & 0x80) != 0;");
            sb.AppendLine("A = (byte)res;");
            sb.AppendLine("F = (byte)((A & 0x80) | (A == 0 ? 0x40 : 0) | (h ? 0x10 : 0) | (pv ? 0x04 : 0) | (c ? 0x01 : 0));");
            sb.AppendLine("}");

            sb.AppendLine("public static void SubA(byte v)");
            sb.AppendLine("{");
            sb.AppendLine("int aa = A; int res = aa - v;");
            sb.AppendLine("bool c = aa < v;");
            sb.AppendLine("bool h = (aa & 0x0F) < (v & 0x0F);");
            sb.AppendLine("bool pv = ((aa ^ v) & (aa ^ (byte)res) & 0x80) != 0;");
            sb.AppendLine("F = (byte)(((byte)res & 0x80) | ((byte)res == 0 ? 0x40 : 0) | (h ? 0x10 : 0) | (pv ? 0x04 : 0) | 0x02 | (c ? 0x01 : 0));");
            sb.AppendLine("A = (byte)res;");
            sb.AppendLine("}");

            sb.AppendLine("public static void SbcA(byte v)");
            sb.AppendLine("{");
            sb.AppendLine("int aa = A; int carry = F & 1; int res = aa - v - carry;");
            sb.AppendLine("bool c = res < 0;");
            sb.AppendLine("bool h = ((aa & 0x0F) - (v & 0x0F) - carry) < 0;");
            sb.AppendLine("bool pv = ((aa ^ v) & (aa ^ (byte)res) & 0x80) != 0;");
            sb.AppendLine("F = (byte)(((byte)res & 0x80) | ((byte)res == 0 ? 0x40 : 0) | (h ? 0x10 : 0) | (pv ? 0x04 : 0) | 0x02 | (c ? 0x01 : 0));");
            sb.AppendLine("A = (byte)res;");
            sb.AppendLine("}");

            sb.AppendLine("public static void AndA(byte v) { A = (byte)(A & v); F = (byte)((A == 0 ? 0x40 : 0) | (A & 0x80) | Parity[A] | 0x10); }");
            sb.AppendLine("public static void XorA(byte v) { A = (byte)(A ^ v); F = (byte)((A == 0 ? 0x40 : 0) | (A & 0x80) | Parity[A]); }");
            sb.AppendLine("public static void OrA(byte v) { A = (byte)(A | v); F = (byte)((A == 0 ? 0x40 : 0) | (A & 0x80) | Parity[A]); }");

            sb.AppendLine("public static void CpA(byte v)");
            sb.AppendLine("{");
            sb.AppendLine("int aa = A; int res = aa - v;");
            sb.AppendLine("bool c = aa < v;");
            sb.AppendLine("bool h = (aa & 0x0F) < (v & 0x0F);");
            sb.AppendLine("bool pv = ((aa ^ v) & (aa ^ (byte)res) & 0x80) != 0;");
            sb.AppendLine("F = (byte)(((byte)res & 0x80) | ((byte)res == 0 ? 0x40 : 0) | (h ? 0x10 : 0) | (pv ? 0x04 : 0) | 0x02 | (c ? 0x01 : 0));");
            sb.AppendLine("}");

            sb.AppendLine("public static void Alu(int op, byte v)");
            sb.AppendLine("{");
            sb.AppendLine("switch (op)");
            sb.AppendLine("{");
            sb.AppendLine("case 0: AddA(v); break;");
            sb.AppendLine("case 1: AdcA(v); break;");
            sb.AppendLine("case 2: SubA(v); break;");
            sb.AppendLine("case 3: SbcA(v); break;");
            sb.AppendLine("case 4: AndA(v); break;");
            sb.AppendLine("case 5: XorA(v); break;");
            sb.AppendLine("case 6: OrA(v); break;");
            sb.AppendLine("case 7: CpA(v); break;");
            sb.AppendLine("}");
            sb.AppendLine("}");

            sb.AppendLine("public static void Add16HL(ushort v)");
            sb.AppendLine("{");
            sb.AppendLine("int hl = HL; int res = hl + v;");
            sb.AppendLine("bool h = ((hl & 0x0FFF) + (v & 0x0FFF)) > 0x0FFF;");
            sb.AppendLine("bool c = res > 0xFFFF;");
            sb.AppendLine("HL = (ushort)res;");
            sb.AppendLine("F = (byte)((F & 0xC4) | (h ? 0x10 : 0) | (c ? 0x01 : 0));");
            sb.AppendLine("}");

            sb.AppendLine("public static void Adc16HL(ushort v)");
            sb.AppendLine("{");
            sb.AppendLine("int hl = HL; int carry = F & 1; int res = hl + v + carry;");
            sb.AppendLine("bool h = ((hl & 0x0FFF) + (v & 0x0FFF) + carry) > 0x0FFF;");
            sb.AppendLine("bool c = res > 0xFFFF;");
            sb.AppendLine("bool pv = ((~(hl ^ v) & (hl ^ res)) & 0x8000) != 0;");
            sb.AppendLine("HL = (ushort)res;");
            sb.AppendLine("F = (byte)(((res & 0x8000) != 0 ? 0x80 : 0) | ((ushort)res == 0 ? 0x40 : 0) | (h ? 0x10 : 0) | (pv ? 0x04 : 0) | (c ? 0x01 : 0));");
            sb.AppendLine("}");

            sb.AppendLine("public static void Sbc16HL(ushort v)");
            sb.AppendLine("{");
            sb.AppendLine("int hl = HL; int carry = F & 1; int res = hl - v - carry;");
            sb.AppendLine("bool c = res < 0;");
            sb.AppendLine("bool h = ((hl & 0x0FFF) - (v & 0x0FFF) - carry) < 0;");
            sb.AppendLine("bool pv = ((hl ^ v) & (hl ^ res) & 0x8000) != 0;");
            sb.AppendLine("HL = (ushort)res;");
            sb.AppendLine("F = (byte)(((res & 0x8000) != 0 ? 0x80 : 0) | ((ushort)res == 0 ? 0x40 : 0) | (h ? 0x10 : 0) | (pv ? 0x04 : 0) | 0x02 | (c ? 0x01 : 0));");
            sb.AppendLine("}");

            sb.AppendLine("public static void Add16Index(bool isIX, ushort v)");
            sb.AppendLine("{");
            sb.AppendLine("int reg = isIX ? IX : IY; int res = reg + v;");
            sb.AppendLine("bool h = ((reg & 0x0FFF) + (v & 0x0FFF)) > 0x0FFF;");
            sb.AppendLine("bool c = res > 0xFFFF;");
            sb.AppendLine("if (isIX) IX = (ushort)res; else IY = (ushort)res;");
            sb.AppendLine("F = (byte)((F & 0xC4) | (h ? 0x10 : 0) | (c ? 0x01 : 0));");
            sb.AppendLine("}");

            sb.AppendLine("public static void Rlca() { int c = (A >> 7) & 1; A = (byte)((A << 1) | c); F = (byte)((F & 0xC4) | c); }");
            sb.AppendLine("public static void Rrca() { int c = A & 1; A = (byte)((A >> 1) | (c << 7)); F = (byte)((F & 0xC4) | c); }");
            sb.AppendLine("public static void Rla() { int oldc = F & 1; int c = (A >> 7) & 1; A = (byte)((A << 1) | oldc); F = (byte)((F & 0xC4) | c); }");
            sb.AppendLine("public static void Rra() { int oldc = F & 1; int c = A & 1; A = (byte)((A >> 1) | (oldc << 7)); F = (byte)((F & 0xC4) | c); }");

            sb.AppendLine("public static byte RotateCB(int kind, byte v)");
            sb.AppendLine("{");
            sb.AppendLine("int c = 0;");
            sb.AppendLine("switch (kind)");
            sb.AppendLine("{");
            sb.AppendLine("case 0: c = (v >> 7) & 1; v = (byte)((v << 1) | c); break;");
            sb.AppendLine("case 1: c = v & 1; v = (byte)((v >> 1) | (c << 7)); break;");
            sb.AppendLine("case 2: c = (v >> 7) & 1; v = (byte)((v << 1) | (F & 1)); break;");
            sb.AppendLine("case 3: c = v & 1; v = (byte)((v >> 1) | ((F & 1) << 7)); break;");
            sb.AppendLine("case 4: c = (v >> 7) & 1; v = (byte)(v << 1); break;");
            sb.AppendLine("case 5: c = v & 1; v = (byte)((v >> 1) | (v & 0x80)); break;");
            sb.AppendLine("case 6: c = (v >> 7) & 1; v = (byte)((v << 1) | 1); break;");
            sb.AppendLine("case 7: c = v & 1; v = (byte)(v >> 1); break;");
            sb.AppendLine("}");
            sb.AppendLine("F = (byte)((v == 0 ? 0x40 : 0) | (v & 0x80) | Parity[v] | c);");
            sb.AppendLine("return v;");
            sb.AppendLine("}");

            sb.AppendLine("public static void BitValue(byte v, int bit)");
            sb.AppendLine("{");
            sb.AppendLine("bool set = (v & (1 << bit)) != 0;");
            sb.AppendLine("F = (byte)((F & 0x01) | 0x10 | (!set ? 0x40 : 0) | ((set && bit == 7) ? 0x80 : 0));");
            sb.AppendLine("}");

            sb.AppendLine("public static void Daa()");
            sb.AppendLine("{");
            sb.AppendLine("int correction = 0; int carry = F & 1;");
            sb.AppendLine("if ((F & 0x10) != 0 || (A & 0x0F) > 9) correction |= 0x06;");
            sb.AppendLine("if (carry != 0 || A > 0x99) { correction |= 0x60; carry = 1; }");
            sb.AppendLine("if ((F & 0x02) != 0) A = (byte)(A - correction); else A = (byte)(A + correction);");
            sb.AppendLine("F = (byte)((F & 0x02) | (carry != 0 ? 1 : 0) | (A == 0 ? 0x40 : 0) | (A & 0x80) | Parity[A] | (((F & 0x10) != 0) ? 0x10 : 0));");
            sb.AppendLine("}");

            sb.AppendLine("public static void NegA()");
            sb.AppendLine("{");
            sb.AppendLine("byte old = A; int res = -old;");
            sb.AppendLine("bool pv = old == 0x80;");
            sb.AppendLine("bool h = (old & 0x0F) != 0;");
            sb.AppendLine("bool c = old != 0;");
            sb.AppendLine("A = (byte)res;");
            sb.AppendLine("F = (byte)((A & 0x80) | (A == 0 ? 0x40 : 0) | (h ? 0x10 : 0) | (pv ? 0x04 : 0) | 0x02 | (c ? 0x01 : 0));");
            sb.AppendLine("}");

            sb.AppendLine("public static void Throttle()");
            sb.AppendLine("{");
            sb.AppendLine("if (!ThrottleEnabled) return;");
            sb.AppendLine("long freq = System.Diagnostics.Stopwatch.Frequency;");
            sb.AppendLine("long now = System.Diagnostics.Stopwatch.GetTimestamp();");
            sb.AppendLine("long ahead = InsCount - (now - ClockStart) * 3500000L / freq;");
            sb.AppendLine("if (ahead < -100000) { ClockStart = System.Diagnostics.Stopwatch.GetTimestamp(); return; }");
            sb.AppendLine("if (ahead <= 50000) return;");
            sb.AppendLine("for (int k = 0; k < 4; k++)");
            sb.AppendLine("{");
            sb.AppendLine("Thread.Sleep(1);");
            sb.AppendLine("long n2 = System.Diagnostics.Stopwatch.GetTimestamp();");
            sb.AppendLine("if (InsCount - (n2 - ClockStart) * 3500000L / freq <= 50000) return;");
            sb.AppendLine("}");
            sb.AppendLine("ClockStart = System.Diagnostics.Stopwatch.GetTimestamp();");
            sb.AppendLine("}");

            sb.AppendLine("public static bool CheckInterrupt(ushort pc)");
            sb.AppendLine("{");
            sb.AppendLine("if ((InsCount & 4095) == 0) Throttle();");
            sb.AppendLine("if (!InterruptPending || !IFF1) return false;");
            sb.AppendLine("InterruptPending = false;");
            sb.AppendLine("Halted = false;");
            sb.AppendLine("Push16(pc);");
            sb.AppendLine("IFF1 = false; IFF2 = false;");
            sb.AppendLine("if (IM == 2) { ushort v = (ushort)((I << 8) | 0xFF); DispatchTarget = Memory.Read16(v); }");
            sb.AppendLine("else DispatchTarget = 0x0038;");
            sb.AppendLine("return true;");
            sb.AppendLine("}");

            sb.AppendLine("public static void Halt()");
            sb.AppendLine("{");
            sb.AppendLine("Halted = true;");
            sb.AppendLine("while (Halted && !InterruptPending) { Throttle(); Thread.Sleep(1); }");
            sb.AppendLine("Halted = false;");
            sb.AppendLine("}");

            sb.AppendLine("public static byte ReadPort(ushort port)");
            sb.AppendLine("{");
            sb.AppendLine("if ((port & 1) == 0)");
            sb.AppendLine("{");
            sb.AppendLine("int high = port >> 8; int res = 0xFF;");
            sb.AppendLine("for (int i = 0; i < 8; i++) if ((high & (1 << i)) == 0) res &= KeyMatrix[i];");
            sb.AppendLine("return (byte)(res & 0xBF);");
            sb.AppendLine("}");
            sb.AppendLine("if (port == 0x7FFD || (port & 0x8002) == 0) return Port7FFD;");
            sb.AppendLine("if (port == 0x1FFD) return Port1FFD;");
            sb.AppendLine("if (port == 0xFFFD) return Ayu.Sel;");
            sb.AppendLine("if (port == 0xBFFD) return Ayu.Regs[Ayu.Sel];");
            sb.AppendLine("if ((port & 0x1F) == 0x1F) return Kempston;");
            sb.AppendLine("return 0xFF;");
            sb.AppendLine("}");

            sb.AppendLine("public static void WritePort(ushort port, byte value)");
            sb.AppendLine("{");
            sb.AppendLine("if ((port & 1) == 0) { Border = (byte)(value & 7); BeeperLevel = (value & 0x10) != 0; return; }");
            sb.AppendLine("if (port == 0x1FFD) { Port1FFD = value; return; }");
            sb.AppendLine("if (port == 0x7FFD || (port & 0x8002) == 0) { if (!PagingDisabled) Port7FFD = value; if ((value & 0x20) != 0) PagingDisabled = true; return; }");
            sb.AppendLine("if (port == 0xFFFD) { Ayu.Sel = (byte)(value & 15); return; }");
            sb.AppendLine("if (port == 0xBFFD) { Ayu.Regs[Ayu.Sel] = value; return; }");
            sb.AppendLine("if ((port & 0x1F) == 0x1F) { Kempston = value; return; }");
            sb.AppendLine("}");
        }

        void AppendExecMethods(StringBuilder sb)
        {
            sb.AppendLine("public static void ExecBase(byte op, ushort val)");
            sb.AppendLine("{");
            sb.AppendLine("byte n = (byte)val;");
            sb.AppendLine("switch (op)");
            sb.AppendLine("{");
            sb.AppendLine("case 0x00: return;");
            sb.AppendLine("case 0x01: C = n; B = (byte)(val >> 8); return;");
            sb.AppendLine("case 0x11: E = n; D = (byte)(val >> 8); return;");
            sb.AppendLine("case 0x21: L = n; H = (byte)(val >> 8); return;");
            sb.AppendLine("case 0x31: SP = val; return;");
            sb.AppendLine("case 0x02: Memory.Write(BC, A); return;");
            sb.AppendLine("case 0x12: Memory.Write(DE, A); return;");
            sb.AppendLine("case 0x0A: A = Memory.Read(BC); return;");
            sb.AppendLine("case 0x1A: A = Memory.Read(DE); return;");
            sb.AppendLine("case 0x22: Memory.Write16(val, HL); return;");
            sb.AppendLine("case 0x2A: HL = Memory.Read16(val); return;");
            sb.AppendLine("case 0x32: Memory.Write(val, A); return;");
            sb.AppendLine("case 0x3A: A = Memory.Read(val); return;");
            sb.AppendLine("case 0x07: Rlca(); return;");
            sb.AppendLine("case 0x0F: Rrca(); return;");
            sb.AppendLine("case 0x17: Rla(); return;");
            sb.AppendLine("case 0x1F: Rra(); return;");
            sb.AppendLine("case 0x08: { byte ta = A; A = A2; A2 = ta; byte tf = F; F = F2; F2 = tf; return; }");
            sb.AppendLine("case 0xD9: { byte tb = B; B = B2; B2 = tb; byte tc = C; C = C2; C2 = tc; byte td = D; D = D2; D2 = td; byte te = E; E = E2; E2 = te; byte th = H; H = H2; H2 = th; byte tl = L; L = L2; L2 = tl; return; }");
            sb.AppendLine("case 0xEB: { ushort t = DE; DE = HL; HL = t; return; }");
            sb.AppendLine("case 0xE3: { ushort v = Memory.Read16(SP); Memory.Write16(SP, HL); HL = v; return; }");
            sb.AppendLine("case 0x27: Daa(); return;");
            sb.AppendLine("case 0x2F: A = (byte)(~A); F = (byte)((F & 0xC5) | 0x12); return;");
            sb.AppendLine("case 0x37: F = (byte)((F & 0xC4) | 0x01); return;");
            sb.AppendLine("case 0x3F: { int c = F & 1; F = (byte)((F & 0xC4) | (c << 4) | (c ^ 1)); return; }");
            sb.AppendLine("case 0xF3: IFF1 = false; IFF2 = false; return;");
            sb.AppendLine("case 0xFB: IFF1 = true; IFF2 = true; return;");
            sb.AppendLine("case 0xF9: SP = HL; return;");
            sb.AppendLine("case 0xD3: WritePort((ushort)((val << 8) | A), A); return;");
            sb.AppendLine("case 0xDB: A = ReadPort((ushort)((val << 8) | A)); return;");
            sb.AppendLine("}");

            sb.AppendLine("if ((op & 0xC7) == 0x04) { int r = (op >> 3) & 7; SetR8(r, Inc8(GetR8(r))); return; }");
            sb.AppendLine("if ((op & 0xC7) == 0x05) { int r = (op >> 3) & 7; SetR8(r, Dec8(GetR8(r))); return; }");
            sb.AppendLine("if ((op & 0xC7) == 0x06) { SetR8((op >> 3) & 7, n); return; }");
            sb.AppendLine("if ((op & 0xCF) == 0x03) { int rp = (op >> 4) & 3; SetRP16Arith(rp, (ushort)(GetRP16Arith(rp) + 1)); return; }");
            sb.AppendLine("if ((op & 0xCF) == 0x0B) { int rp = (op >> 4) & 3; SetRP16Arith(rp, (ushort)(GetRP16Arith(rp) - 1)); return; }");
            sb.AppendLine("if ((op & 0xCF) == 0x09) { Add16HL(GetRP16Arith((op >> 4) & 3)); return; }");
            sb.AppendLine("if ((op & 0xC7) == 0xC5) { Push16(GetRP16((op >> 4) & 3)); return; }");
            sb.AppendLine("if ((op & 0xC7) == 0xC1) { SetRP16((op >> 4) & 3, Pop16()); return; }");
            sb.AppendLine("if ((op & 0xC7) == 0xC6) { Alu((op >> 3) & 7, n); return; }");
            sb.AppendLine("if (op >= 0x40 && op <= 0x7F && op != 0x76) { SetR8((op >> 3) & 7, GetR8(op & 7)); return; }");
            sb.AppendLine("if (op >= 0x80 && op <= 0xBF) { Alu((op >> 3) & 7, GetR8(op & 7)); return; }");
            sb.AppendLine("UnknownOpcode(op, LastPC);");
            sb.AppendLine("}");

            sb.AppendLine("public static void ExecCB(byte op)");
            sb.AppendLine("{");
            sb.AppendLine("int reg = op & 7; int kind = op >> 3;");
            sb.AppendLine("if (kind < 8) { byte v = GetR8(reg); v = RotateCB(kind, v); SetR8(reg, v); return; }");
            sb.AppendLine("if (kind < 16) { byte v = GetR8(reg); BitValue(v, kind - 8); return; }");
            sb.AppendLine("if (kind < 24) { int bit = kind - 16; byte v = GetR8(reg); v = (byte)(v & ~(1 << bit)); SetR8(reg, v); return; }");
            sb.AppendLine("int bit2 = kind - 24; byte v2 = GetR8(reg); v2 = (byte)(v2 | (1 << bit2)); SetR8(reg, v2);");
            sb.AppendLine("}");

            sb.AppendLine("public static void ExecEDNormal(byte op, ushort val)");
            sb.AppendLine("{");
            sb.AppendLine("if ((op & 0xC7) == 0x40) { int r = (op >> 3) & 7; byte v = ReadPort(BC); if (r != 6) SetR8(r, v); F = (byte)((F & 1) | (v == 0 ? 0x40 : 0) | (v & 0x80) | Parity[v]); return; }");
            sb.AppendLine("if ((op & 0xC7) == 0x41) { int r = (op >> 3) & 7; WritePort(BC, (r == 6) ? (byte)0 : GetR8(r)); return; }");
            sb.AppendLine("if ((op & 0xCF) == 0x42) { Sbc16HL(GetRP16Arith((op >> 4) & 3)); return; }");
            sb.AppendLine("if ((op & 0xCF) == 0x4A) { Adc16HL(GetRP16Arith((op >> 4) & 3)); return; }");
            sb.AppendLine("if ((op & 0xC7) == 0x43) { Memory.Write16(val, GetRP16Arith((op >> 4) & 3)); return; }");
            sb.AppendLine("if ((op & 0xC7) == 0x4B) { SetRP16Arith((op >> 4) & 3, Memory.Read16(val)); return; }");
            sb.AppendLine("if ((op & 0xC7) == 0x44) { NegA(); return; }");
            sb.AppendLine("if (op == 0x46 || op == 0x4E || op == 0x66 || op == 0x6E) { IM = 0; return; }");
            sb.AppendLine("if (op == 0x56 || op == 0x76) { IM = 1; return; }");
            sb.AppendLine("if (op == 0x5E || op == 0x7E) { IM = 2; return; }");
            sb.AppendLine("if (op == 0x47) { I = A; return; }");
            sb.AppendLine("if (op == 0x4F) { R = A; return; }");
            sb.AppendLine("if (op == 0x57) { A = I; SetSZ(A); F = (byte)((F & 1) | (IFF2 ? 4 : 0)); return; }");
            sb.AppendLine("if (op == 0x5F) { A = R; SetSZ(A); F = (byte)((F & 1) | (IFF2 ? 4 : 0)); return; }");
            sb.AppendLine("if (op == 0x67) { byte m = Memory.Read(HL); byte low = (byte)(m & 0x0F); m = (byte)((m >> 4) | (A << 4)); A = (byte)((A & 0xF0) | low); Memory.Write(HL, m); SetSZ(A); F = (byte)((F & 1) | Parity[A]); return; }");
            sb.AppendLine("if (op == 0x6F) { byte m = Memory.Read(HL); byte high = (byte)(m >> 4); m = (byte)((m << 4) | (A & 0x0F)); A = (byte)((A & 0xF0) | high); Memory.Write(HL, m); SetSZ(A); F = (byte)((F & 1) | Parity[A]); return; }");
            sb.AppendLine("if (op >= 0xA0 && op <= 0xBB) { Block(op); return; }");
            sb.AppendLine("}");

            sb.AppendLine("public static void Block(byte op)");
            sb.AppendLine("{");
            sb.AppendLine("switch (op)");
            sb.AppendLine("{");
            sb.AppendLine("case 0xA0: Ldi(); break;");
            sb.AppendLine("case 0xB0: while (BC != 0) Ldi(); break;");
            sb.AppendLine("case 0xA8: Ldd(); break;");
            sb.AppendLine("case 0xB8: while (BC != 0) Ldd(); break;");
            sb.AppendLine("case 0xA1: Cpi(); break;");
            sb.AppendLine("case 0xB1: while (BC != 0) { Cpi(); if ((F & 0x40) == 0) break; } break;");
            sb.AppendLine("case 0xA9: Cpd(); break;");
            sb.AppendLine("case 0xB9: while (BC != 0) { Cpd(); if ((F & 0x40) == 0) break; } break;");
            sb.AppendLine("case 0xA2: Ini(); break;");
            sb.AppendLine("case 0xB2: while (B != 0) Ini(); break;");
            sb.AppendLine("case 0xAA: Ind(); break;");
            sb.AppendLine("case 0xBA: while (B != 0) Ind(); break;");
            sb.AppendLine("case 0xA3: Outi(); break;");
            sb.AppendLine("case 0xB3: while (B != 0) Outi(); break;");
            sb.AppendLine("case 0xAB: Outd(); break;");
            sb.AppendLine("case 0xBB: while (B != 0) Outd(); break;");
            sb.AppendLine("}");
            sb.AppendLine("}");

            sb.AppendLine("static void Ldi() { byte v = Memory.Read(HL); Memory.Write(DE, v); HL = (ushort)(HL + 1); DE = (ushort)(DE + 1); BC = (ushort)(BC - 1); F = (byte)((F & 0xC1) | ((BC != 0) ? 0x04 : 0)); }");
            sb.AppendLine("static void Ldd() { byte v = Memory.Read(HL); Memory.Write(DE, v); HL = (ushort)(HL - 1); DE = (ushort)(DE - 1); BC = (ushort)(BC - 1); F = (byte)((F & 0xC1) | ((BC != 0) ? 0x04 : 0)); }");
            sb.AppendLine("static void Cpi() { byte v = Memory.Read(HL); int res = A - v; HL = (ushort)(HL + 1); BC = (ushort)(BC - 1); F = (byte)((F & 0x01) | ((byte)res == 0 ? 0x40 : 0) | ((byte)res & 0x80) | ((BC != 0) ? 0x04 : 0) | 0x02); }");
            sb.AppendLine("static void Cpd() { byte v = Memory.Read(HL); int res = A - v; HL = (ushort)(HL - 1); BC = (ushort)(BC - 1); F = (byte)((F & 0x01) | ((byte)res == 0 ? 0x40 : 0) | ((byte)res & 0x80) | ((BC != 0) ? 0x04 : 0) | 0x02); }");
            sb.AppendLine("static void Ini() { byte v = ReadPort(BC); Memory.Write(HL, v); HL = (ushort)(HL + 1); B--; SetSZ(B); F = (byte)((F & 1) | 0x02); }");
            sb.AppendLine("static void Ind() { byte v = ReadPort(BC); Memory.Write(HL, v); HL = (ushort)(HL - 1); B--; SetSZ(B); F = (byte)((F & 1) | 0x02); }");
            sb.AppendLine("static void Outi() { byte v = Memory.Read(HL); HL = (ushort)(HL + 1); B--; WritePort(BC, v); SetSZ(B); }");
            sb.AppendLine("static void Outd() { byte v = Memory.Read(HL); HL = (ushort)(HL - 1); B--; WritePort(BC, v); SetSZ(B); }");

            sb.AppendLine("public static byte GetIXH(bool isIX) { return (byte)((isIX ? IX : IY) >> 8); }");
            sb.AppendLine("public static byte GetIXL(bool isIX) { return (byte)((isIX ? IX : IY) & 0xFF); }");
            sb.AppendLine("public static void SetIXH(bool isIX, byte v) { if (isIX) IX = (ushort)((v << 8) | (IX & 0xFF)); else IY = (ushort)((v << 8) | (IY & 0xFF)); }");
            sb.AppendLine("public static void SetIXL(bool isIX, byte v) { if (isIX) IX = (ushort)((IX & 0xFF00) | v); else IY = (ushort)((IY & 0xFF00) | v); }");

            sb.AppendLine("public static void ExecPrefixed(int prefix, byte op, sbyte d, ushort val)");
            sb.AppendLine("{");
            sb.AppendLine("bool isIX = prefix == 0xDD;");
            sb.AppendLine("byte n = (byte)val;");

            sb.AppendLine("switch (op)");
            sb.AppendLine("{");
            sb.AppendLine("case 0x21: if (isIX) IX = val; else IY = val; return;");
            sb.AppendLine("case 0x22: Memory.Write16(val, isIX ? IX : IY); return;");
            sb.AppendLine("case 0x2A: { ushort v = Memory.Read16(val); if (isIX) IX = v; else IY = v; return; }");
            sb.AppendLine("case 0x23: if (isIX) IX = (ushort)(IX + 1); else IY = (ushort)(IY + 1); return;");
            sb.AppendLine("case 0x2B: if (isIX) IX = (ushort)(IX - 1); else IY = (ushort)(IY - 1); return;");
            sb.AppendLine("case 0x24: SetIXH(isIX, Inc8(GetIXH(isIX))); return;");
            sb.AppendLine("case 0x25: SetIXH(isIX, Dec8(GetIXH(isIX))); return;");
            sb.AppendLine("case 0x26: SetIXH(isIX, n); return;");
            sb.AppendLine("case 0x2C: SetIXL(isIX, Inc8(GetIXL(isIX))); return;");
            sb.AppendLine("case 0x2D: SetIXL(isIX, Dec8(GetIXL(isIX))); return;");
            sb.AppendLine("case 0x2E: SetIXL(isIX, n); return;");
            sb.AppendLine("case 0xE1: if (isIX) IX = Pop16(); else IY = Pop16(); return;");
            sb.AppendLine("case 0xE5: Push16(isIX ? IX : IY); return;");
            sb.AppendLine("case 0xE3: { ushort v = Memory.Read16(SP); Memory.Write16(SP, isIX ? IX : IY); if (isIX) IX = v; else IY = v; return; }");
            sb.AppendLine("case 0xF9: SP = isIX ? IX : IY; return;");
            sb.AppendLine("}");

            sb.AppendLine("if ((op & 0xCF) == 0x09) { int rp = (op >> 4) & 3; ushort v = (rp == 2) ? (isIX ? IX : IY) : GetRP16Arith(rp); Add16Index(isIX, v); return; }");

            sb.AppendLine("if (UsesIXYDisp[op] != 0)");
            sb.AppendLine("{");
            sb.AppendLine("ushort addr = (ushort)((isIX ? IX : IY) + d);");
            sb.AppendLine("if (op == 0x34) { Memory.Write(addr, Inc8(Memory.Read(addr))); return; }");
            sb.AppendLine("if (op == 0x35) { Memory.Write(addr, Dec8(Memory.Read(addr))); return; }");
            sb.AppendLine("if (op == 0x36) { Memory.Write(addr, n); return; }");
            sb.AppendLine("if (op >= 0x70 && op <= 0x77 && op != 0x76) { Memory.Write(addr, GetR8(op & 7)); return; }");
            sb.AppendLine("if ((op & 0xC0) == 0x40) { int dst = (op >> 3) & 7; if (dst != 6) SetR8(dst, Memory.Read(addr)); return; }");
            sb.AppendLine("if ((op & 0xC0) == 0x80) { Alu((op >> 3) & 7, Memory.Read(addr)); return; }");
            sb.AppendLine("}");

            sb.AppendLine("UnknownOpcode(op, LastPC);");
            sb.AppendLine("}");

            sb.AppendLine("public static void ExecPrefixedCB(int prefix, sbyte d, byte op)");
            sb.AppendLine("{");
            sb.AppendLine("bool isIX = prefix == 0xDD || prefix == 0xDDCB;");
            sb.AppendLine("ushort addr = (ushort)((isIX ? IX : IY) + d);");
            sb.AppendLine("int kind = op >> 3; int reg = op & 7;");
            sb.AppendLine("if (kind < 8) { byte v = Memory.Read(addr); v = RotateCB(kind, v); Memory.Write(addr, v); if (reg != 6) SetR8(reg, v); return; }");
            sb.AppendLine("if (kind < 16) { byte v = Memory.Read(addr); BitValue(v, kind - 8); return; }");
            sb.AppendLine("if (kind < 24) { int bit = kind - 16; byte v = Memory.Read(addr); v = (byte)(v & ~(1 << bit)); Memory.Write(addr, v); if (reg != 6) SetR8(reg, v); return; }");
            sb.AppendLine("int bit2 = kind - 24; byte v2 = Memory.Read(addr); v2 = (byte)(v2 | (1 << bit2)); Memory.Write(addr, v2); if (reg != 6) SetR8(reg, v2);");
            sb.AppendLine("}");
        }

        void AppendDynamicExecutor(StringBuilder sb)
        {
            sb.AppendLine("public static int GetLength(ushort pc)");
            sb.AppendLine("{");
            sb.AppendLine("byte op = Memory.Read(pc);");
            sb.AppendLine("if (op == 0xCB) return 2;");
            sb.AppendLine("if (op == 0xED) { byte op2 = Memory.Read((ushort)(pc + 1)); return EDLen[op2]; }");
            sb.AppendLine("if (op == 0xDD || op == 0xFD)");
            sb.AppendLine("{");
            sb.AppendLine("byte op2 = Memory.Read((ushort)(pc + 1));");
            sb.AppendLine("if (op2 == 0xCB) return 4;");
            sb.AppendLine("if (op2 == 0xDD || op2 == 0xFD || op2 == 0xED) return 1;");
            sb.AppendLine("if (UsesIXYDisp[op2] != 0) return (op2 == 0x36) ? 4 : 3;");
            sb.AppendLine("return BaseLen[op2] + 1;");
            sb.AppendLine("}");
            sb.AppendLine("return BaseLen[op];");
            sb.AppendLine("}");

            sb.AppendLine("public static bool ExecDynamicOne(ushort pc)");
            sb.AppendLine("{");
            sb.AppendLine("byte op = Memory.Read(pc);");

            sb.AppendLine("if (op == 0xCB) { ExecCB(Memory.Read((ushort)(pc + 1))); DispatchTarget = (ushort)(pc + 2); return true; }");

            sb.AppendLine("if (op == 0xED)");
            sb.AppendLine("{");
            sb.AppendLine("byte ed = Memory.Read((ushort)(pc + 1));");
            sb.AppendLine("int len = EDLen[ed];");
            sb.AppendLine("ushort nn = len == 4 ? Memory.Read16((ushort)(pc + 2)) : (ushort)0;");
            sb.AppendLine("if ((ed & 0xC7) == 0x45) { DispatchTarget = Pop16(); IFF1 = IFF2; return true; }");
            sb.AppendLine("ExecEDNormal(ed, nn);");
            sb.AppendLine("DispatchTarget = (ushort)(pc + len);");
            sb.AppendLine("return true;");
            sb.AppendLine("}");

            sb.AppendLine("if (op == 0xDD || op == 0xFD)");
            sb.AppendLine("{");
            sb.AppendLine("byte op2 = Memory.Read((ushort)(pc + 1));");
            sb.AppendLine("if (op2 == 0xCB) { sbyte d = (sbyte)Memory.Read((ushort)(pc + 2)); byte cb = Memory.Read((ushort)(pc + 3)); ExecPrefixedCB(op, d, cb); DispatchTarget = (ushort)(pc + 4); return true; }");
            sb.AppendLine("if (op2 == 0xE9) { DispatchTarget = (op == 0xDD) ? IX : IY; return true; }");
            sb.AppendLine("int len = GetLength(pc);");
            sb.AppendLine("sbyte d2 = 0; ushort nn2 = 0;");
            sb.AppendLine("if (UsesIXYDisp[op2] != 0) { d2 = (sbyte)Memory.Read((ushort)(pc + 2)); if (len == 4) nn2 = Memory.Read((ushort)(pc + 3)); }");
            sb.AppendLine("else if (len >= 3) { if (BaseLen[op2] == 3 || op2 == 0x21 || op2 == 0x22 || op2 == 0x2A || op2 == 0x2B) nn2 = Memory.Read16((ushort)(pc + 2)); else nn2 = Memory.Read((ushort)(pc + 2)); }");
            sb.AppendLine("ExecPrefixed(op, op2, d2, nn2);");
            sb.AppendLine("DispatchTarget = (ushort)(pc + len);");
            sb.AppendLine("return true;");
            sb.AppendLine("}");

            sb.AppendLine("int blen = BaseLen[op];");
            sb.AppendLine("ushort operand = blen >= 2 ? Memory.Read((ushort)(pc + 1)) : (ushort)0;");
            sb.AppendLine("if (blen == 3) operand |= (ushort)(Memory.Read((ushort)(pc + 2)) << 8);");

            sb.AppendLine("switch (op)");
            sb.AppendLine("{");
            sb.AppendLine("case 0xC3: DispatchTarget = operand; return true;");
            sb.AppendLine("case 0xC2: case 0xCA: case 0xD2: case 0xDA: case 0xE2: case 0xEA: case 0xF2: case 0xFA:");
            sb.AppendLine("if (Conditional((op >> 3) & 7)) DispatchTarget = operand; else DispatchTarget = (ushort)(pc + 3); return true;");
            sb.AppendLine("case 0x18: DispatchTarget = (ushort)(pc + 2 + (sbyte)operand); return true;");
            sb.AppendLine("case 0x10: B = (byte)(B - 1); if (B != 0) DispatchTarget = (ushort)(pc + 2 + (sbyte)operand); else DispatchTarget = (ushort)(pc + 2); return true;");
            sb.AppendLine("case 0x20: case 0x28: case 0x30: case 0x38:");
            sb.AppendLine("if (Conditional((op >> 3) & 3)) DispatchTarget = (ushort)(pc + 2 + (sbyte)operand); else DispatchTarget = (ushort)(pc + 2); return true;");
            sb.AppendLine("case 0xCD: Push16((ushort)(pc + 3)); DispatchTarget = operand; return true;");
            sb.AppendLine("case 0xC4: case 0xCC: case 0xD4: case 0xDC: case 0xE4: case 0xEC: case 0xF4: case 0xFC:");
            sb.AppendLine("if (Conditional((op >> 3) & 7)) { Push16((ushort)(pc + 3)); DispatchTarget = operand; } else DispatchTarget = (ushort)(pc + 3); return true;");
            sb.AppendLine("case 0xC9: DispatchTarget = Pop16(); return true;");
            sb.AppendLine("case 0xC0: case 0xC8: case 0xD0: case 0xD8: case 0xE0: case 0xE8: case 0xF0: case 0xF8:");
            sb.AppendLine("if (Conditional((op >> 3) & 7)) DispatchTarget = Pop16(); else DispatchTarget = (ushort)(pc + 1); return true;");
            sb.AppendLine("case 0xC7: case 0xCF: case 0xD7: case 0xDF: case 0xE7: case 0xEF: case 0xF7: case 0xFF:");
            sb.AppendLine("Push16((ushort)(pc + 1)); DispatchTarget = (ushort)(op & 0x38); return true;");
            sb.AppendLine("case 0xE9: DispatchTarget = HL; return true;");
            sb.AppendLine("case 0x76: Halt(); DispatchTarget = (ushort)(pc + 1); return true;");
            sb.AppendLine("}");

            sb.AppendLine("ExecBase(op, operand);");
            sb.AppendLine("DispatchTarget = (ushort)(pc + blen);");
            sb.AppendLine("return true;");
            sb.AppendLine("}");
        }

        void AppendMemory(StringBuilder sb)
        {
            sb.AppendLine("static class Memory");
            sb.AppendLine("{");

            sb.AppendLine("public static void Reset()");
            sb.AppendLine("{");
            sb.AppendLine("for (int i = 0; i < 8; i++)");
            sb.AppendLine("{");
            sb.AppendLine("if (Runtime.InitialRam[i] != null)");
            sb.AppendLine("Runtime.RamBanks[i] = (byte[])Runtime.InitialRam[i].Clone();");
            sb.AppendLine("else");
            sb.AppendLine("Runtime.RamBanks[i] = new byte[16384];");
            sb.AppendLine("}");
            sb.AppendLine("}");

            sb.AppendLine("public static byte Read(ushort addr)");
            sb.AppendLine("{");
            sb.AppendLine("if (addr < 0x4000)");
            sb.AppendLine("{");
            sb.AppendLine("if (Runtime.Model >= 128 && (Runtime.Port7FFD & 0x10) != 0) return Runtime.Rom1[addr];");
            sb.AppendLine("return Runtime.Rom0[addr];");
            sb.AppendLine("}");
            sb.AppendLine("if (addr < 0x8000) return Runtime.RamBanks[5][addr - 0x4000];");
            sb.AppendLine("if (addr < 0xC000) return Runtime.RamBanks[2][addr - 0x8000];");
            sb.AppendLine("int bank = (Runtime.Model >= 128) ? (Runtime.Port7FFD & 7) : 0;");
            sb.AppendLine("return Runtime.RamBanks[bank][addr - 0xC000];");
            sb.AppendLine("}");

            sb.AppendLine("public static void Write(ushort addr, byte value)");
            sb.AppendLine("{");
            sb.AppendLine("if (addr < 0x4000) return;");
            sb.AppendLine("if (addr < 0x8000) { Runtime.RamBanks[5][addr - 0x4000] = value; return; }");
            sb.AppendLine("if (addr < 0xC000) { Runtime.RamBanks[2][addr - 0x8000] = value; return; }");
            sb.AppendLine("int bank = (Runtime.Model >= 128) ? (Runtime.Port7FFD & 7) : 0;");
            sb.AppendLine("Runtime.RamBanks[bank][addr - 0xC000] = value;");
            sb.AppendLine("}");

            sb.AppendLine("public static ushort Read16(ushort addr)");
            sb.AppendLine("{");
            sb.AppendLine("return (ushort)(Read(addr) | (Read((ushort)(addr + 1)) << 8));");
            sb.AppendLine("}");

            sb.AppendLine("public static void Write16(ushort addr, ushort value)");
            sb.AppendLine("{");
            sb.AppendLine("Write(addr, (byte)(value & 0xFF));");
            sb.AppendLine("Write((ushort)(addr + 1), (byte)(value >> 8));");
            sb.AppendLine("}");

            sb.AppendLine("}");
            sb.AppendLine();
        }

        void AppendUla(StringBuilder sb)
        {
            sb.AppendLine("static class Ula");
            sb.AppendLine("{");
            sb.AppendLine("public static int FrameCount;");
            sb.AppendLine("public static byte[] Screen = new byte[320 * 240 * 4];");
            sb.AppendLine("public static int[] Palette = new int[16]");
            sb.AppendLine("{");
            sb.AppendLine("0x000000, 0x0000D7, 0xD70000, 0xD700D7, 0x00D700, 0x00D7D7, 0xD7D700, 0xD7D7D7,");
            sb.AppendLine("0x000000, 0x0000FF, 0xFF0000, 0xFF00FF, 0x00FF00, 0x00FFFF, 0xFFFF00, 0xFFFFFF");
            sb.AppendLine("};");

            sb.AppendLine("public static void Reset()");
            sb.AppendLine("{");
            sb.AppendLine("FrameCount = 0;");
            sb.AppendLine("Array.Clear(Screen, 0, Screen.Length);");
            sb.AppendLine("}");

            sb.AppendLine("public static void RenderToBitmap(Bitmap target)");
            sb.AppendLine("{");
            sb.AppendLine("lock (Runtime.FrameLock)");
            sb.AppendLine("{");
            sb.AppendLine("RenderScreen();");
            sb.AppendLine("Rectangle rc = new Rectangle(0, 0, 320, 240);");
            sb.AppendLine("BitmapData data = target.LockBits(rc, ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);");
            sb.AppendLine("if (data.Stride == 320 * 4)");
            sb.AppendLine("{");
            sb.AppendLine("Marshal.Copy(Screen, 0, data.Scan0, Screen.Length);");
            sb.AppendLine("}");
            sb.AppendLine("else");
            sb.AppendLine("{");
            sb.AppendLine("for (int y = 0; y < 240; y++)");
            sb.AppendLine("{");
            sb.AppendLine("IntPtr dst = new IntPtr(data.Scan0.ToInt64() + y * data.Stride);");
            sb.AppendLine("Marshal.Copy(Screen, y * 320 * 4, dst, 320 * 4);");
            sb.AppendLine("}");
            sb.AppendLine("}");
            sb.AppendLine("target.UnlockBits(data);");
            sb.AppendLine("}");
            sb.AppendLine("}");

            sb.AppendLine("static void RenderScreen()");
            sb.AppendLine("{");
            sb.AppendLine("int border = Runtime.Border & 7;");
            sb.AppendLine("Fill(border);");
            sb.AppendLine("int screenBank = (Runtime.Model >= 128 && (Runtime.Port7FFD & 8) != 0) ? 7 : 5;");
            sb.AppendLine("byte[] bank = Runtime.RamBanks[screenBank];");
            sb.AppendLine("bool flashOn = (FrameCount & 16) != 0;");
            sb.AppendLine();
            sb.AppendLine("for (int y = 0; y < 192; y++)");
            sb.AppendLine("{");
            sb.AppendLine("for (int x = 0; x < 256; x++)");
            sb.AppendLine("{");
            sb.AppendLine("int addr = 0x4000 + ((y & 0xC0) << 5) + ((y & 0x07) << 8) + ((y & 0x38) << 2) + (x >> 3);");
            sb.AppendLine("byte pixels = bank[addr & 0x3FFF];");
            sb.AppendLine("byte attr = bank[(0x5800 + (y >> 3) * 32 + (x >> 3)) & 0x3FFF];");
            sb.AppendLine("int bit = 7 - (x & 7);");
            sb.AppendLine("bool pix = ((pixels >> bit) & 1) != 0;");
            sb.AppendLine("int ink = attr & 7;");
            sb.AppendLine("int paper = (attr >> 3) & 7;");
            sb.AppendLine("if ((attr & 0x40) != 0) { ink += 8; paper += 8; }");
            sb.AppendLine("if ((attr & 0x80) != 0 && flashOn) { int t = ink; ink = paper; paper = t; }");
            sb.AppendLine("SetPixel(32 + x, 24 + y, pix ? ink : paper);");
            sb.AppendLine("}");
            sb.AppendLine("}");
            sb.AppendLine("}");

            sb.AppendLine("static void Fill(int colorIndex)");
            sb.AppendLine("{");
            sb.AppendLine("int rgb = Palette[colorIndex & 15];");
            sb.AppendLine("byte b = (byte)(rgb & 0xFF);");
            sb.AppendLine("byte g = (byte)((rgb >> 8) & 0xFF);");
            sb.AppendLine("byte r = (byte)((rgb >> 16) & 0xFF);");
            sb.AppendLine("for (int i = 0; i < Screen.Length; i += 4)");
            sb.AppendLine("{");
            sb.AppendLine("Screen[i + 0] = b;");
            sb.AppendLine("Screen[i + 1] = g;");
            sb.AppendLine("Screen[i + 2] = r;");
            sb.AppendLine("Screen[i + 3] = 0xFF;");
            sb.AppendLine("}");
            sb.AppendLine("}");

            sb.AppendLine("static void SetPixel(int x, int y, int colorIndex)");
            sb.AppendLine("{");
            sb.AppendLine("if ((uint)x >= 320 || (uint)y >= 240) return;");
            sb.AppendLine("int rgb = Palette[colorIndex & 15];");
            sb.AppendLine("int off = (y * 320 + x) * 4;");
            sb.AppendLine("Screen[off + 0] = (byte)(rgb & 0xFF);");
            sb.AppendLine("Screen[off + 1] = (byte)((rgb >> 8) & 0xFF);");
            sb.AppendLine("Screen[off + 2] = (byte)((rgb >> 16) & 0xFF);");
            sb.AppendLine("Screen[off + 3] = 0xFF;");
            sb.AppendLine("}");

            sb.AppendLine("}");
            sb.AppendLine();
        }

        void AppendAyu(StringBuilder sb)
        {
            sb.AppendLine("static class Ayu");
            sb.AppendLine("{");
            sb.AppendLine("public static byte Sel;");
            sb.AppendLine("public static byte[] Regs = new byte[16];");
            sb.AppendLine("}");
            sb.AppendLine();
        }

        void AppendAudio(StringBuilder sb)
        {
            sb.AppendLine("static class Audio");
            sb.AppendLine("{");
            sb.AppendLine("[StructLayout(LayoutKind.Sequential)]");
            sb.AppendLine("struct WAVEFORMATEX { public ushort wFormatTag; public ushort nChannels; public uint nSamplesPerSec; public uint nAvgBytesPerSec; public ushort nBlockAlign; public ushort wBitsPerSample; public ushort cbSize; }");
            sb.AppendLine("[StructLayout(LayoutKind.Sequential)]");
            sb.AppendLine("struct WAVEHDR { public IntPtr lpData; public uint dwBufferLength; public uint dwBytesRecorded; public IntPtr dwUser; public uint dwFlags; public uint dwLoops; public IntPtr lpNext; public IntPtr reserved; }");
            sb.AppendLine("[DllImport(\"winmm.dll\")] static extern int waveOutOpen(out IntPtr h, int dev, ref WAVEFORMATEX fmt, IntPtr cb, IntPtr inst, int flags);");
            sb.AppendLine("[DllImport(\"winmm.dll\")] static extern int waveOutPrepareHeader(IntPtr h, ref WAVEHDR hdr, int size);");
            sb.AppendLine("[DllImport(\"winmm.dll\")] static extern int waveOutWrite(IntPtr h, ref WAVEHDR hdr, int size);");
            sb.AppendLine("[DllImport(\"winmm.dll\")] static extern int waveOutUnprepareHeader(IntPtr h, ref WAVEHDR hdr, int size);");
            sb.AppendLine("[DllImport(\"winmm.dll\")] static extern int waveOutReset(IntPtr h);");
            sb.AppendLine("[DllImport(\"winmm.dll\")] static extern int waveOutClose(IntPtr h);");

            sb.AppendLine("const int SR = 22050;");
            sb.AppendLine("const int BLOCK = 2048;");
            sb.AppendLine("static IntPtr _hwo;");
            sb.AppendLine("static bool _open;");
            sb.AppendLine("static Thread _thread;");
            sb.AppendLine("static volatile bool _run;");
            sb.AppendLine("static byte[][] _buf = new byte[2][];");
            sb.AppendLine("static WAVEHDR[] _hdr = new WAVEHDR[2];");
            sb.AppendLine("static GCHandle[] _pin = new GCHandle[2];");
            sb.AppendLine("static bool[] _queued = new bool[2];");
            sb.AppendLine("static float[] _ph = new float[3];");
            sb.AppendLine("static int _noise = 1;");
            sb.AppendLine("static int _noiseCounter;");

            sb.AppendLine("public static void TryStart()");
            sb.AppendLine("{");
            sb.AppendLine("try");
            sb.AppendLine("{");
            sb.AppendLine("if (_open) return;");
            sb.AppendLine("WAVEFORMATEX fmt = new WAVEFORMATEX();");
            sb.AppendLine("fmt.wFormatTag = 1; fmt.nChannels = 1; fmt.nSamplesPerSec = SR; fmt.wBitsPerSample = 8; fmt.nBlockAlign = 1; fmt.nAvgBytesPerSec = SR; fmt.cbSize = 0;");
            sb.AppendLine("int r = waveOutOpen(out _hwo, -1, ref fmt, IntPtr.Zero, IntPtr.Zero, 0);");
            sb.AppendLine("if (r != 0) return;");
            sb.AppendLine("_open = true;");
            sb.AppendLine("int hdrSize = Marshal.SizeOf(typeof(WAVEHDR));");
            sb.AppendLine("for (int i = 0; i < 2; i++)");
            sb.AppendLine("{");
            sb.AppendLine("_buf[i] = new byte[BLOCK];");
            sb.AppendLine("_pin[i] = GCHandle.Alloc(_buf[i], GCHandleType.Pinned);");
            sb.AppendLine("_hdr[i] = new WAVEHDR();");
            sb.AppendLine("_hdr[i].lpData = _pin[i].AddrOfPinnedObject();");
            sb.AppendLine("_hdr[i].dwBufferLength = BLOCK;");
            sb.AppendLine("waveOutPrepareHeader(_hwo, ref _hdr[i], hdrSize);");
            sb.AppendLine("}");
            sb.AppendLine("_run = true;");
            sb.AppendLine("_thread = new Thread(new ThreadStart(ThreadProc));");
            sb.AppendLine("_thread.IsBackground = true;");
            sb.AppendLine("_thread.Start();");
            sb.AppendLine("}");
            sb.AppendLine("catch { _open = false; }");
            sb.AppendLine("}");

            sb.AppendLine("public static void TryStop()");
            sb.AppendLine("{");
            sb.AppendLine("try");
            sb.AppendLine("{");
            sb.AppendLine("_run = false;");
            sb.AppendLine("if (_open)");
            sb.AppendLine("{");
            sb.AppendLine("waveOutReset(_hwo);");
            sb.AppendLine("int hdrSize = Marshal.SizeOf(typeof(WAVEHDR));");
            sb.AppendLine("for (int i = 0; i < 2; i++) { try { waveOutUnprepareHeader(_hwo, ref _hdr[i], hdrSize); } catch { } if (_pin[i].IsAllocated) _pin[i].Free(); }");
            sb.AppendLine("waveOutClose(_hwo);");
            sb.AppendLine("_open = false;");
            sb.AppendLine("}");
            sb.AppendLine("}");
            sb.AppendLine("catch { }");
            sb.AppendLine("}");

            sb.AppendLine("static void ThreadProc()");
            sb.AppendLine("{");
            sb.AppendLine("int hdrSize; try { hdrSize = Marshal.SizeOf(typeof(WAVEHDR)); } catch { hdrSize = 32; }");
            sb.AppendLine("int idx = 0;");
            sb.AppendLine("while (_run)");
            sb.AppendLine("{");
            sb.AppendLine("try");
            sb.AppendLine("{");
            sb.AppendLine("if (_queued[idx])");
            sb.AppendLine("{");
            sb.AppendLine("while (_run && (_hdr[idx].dwFlags & 1) == 0) Thread.Sleep(1);");
            sb.AppendLine("if (!_run) break;");
            sb.AppendLine("}");
            sb.AppendLine("Fill(_buf[idx]);");
            sb.AppendLine("int wr = waveOutWrite(_hwo, ref _hdr[idx], hdrSize);");
            sb.AppendLine("if (wr == 0) { _queued[idx] = true; idx = 1 - idx; }");
            sb.AppendLine("else Thread.Sleep(2);");
            sb.AppendLine("}");
            sb.AppendLine("catch { Thread.Sleep(5); }");
            sb.AppendLine("}");
            sb.AppendLine("}");

            sb.AppendLine("static void Fill(byte[] b)");
            sb.AppendLine("{");
            sb.AppendLine("for (int i = 0; i < b.Length; i++)");
            sb.AppendLine("{");
            sb.AppendLine("int s = 0;");
            sb.AppendLine("if (Runtime.BeeperLevel) s += 25;");
            sb.AppendLine("for (int ch = 0; ch < 3; ch++)");
            sb.AppendLine("{");
            sb.AppendLine("int period = Ayu.Regs[ch * 2] | ((Ayu.Regs[ch * 2 + 1] & 15) << 8);");
            sb.AppendLine("int vol = Ayu.Regs[8 + ch] & 15;");
            sb.AppendLine("if (vol > 0 && period > 8)");
            sb.AppendLine("{");
            sb.AppendLine("float freq = 1789773f / (16f * period);");
            sb.AppendLine("_ph[ch] += freq / SR;");
            sb.AppendLine("if (_ph[ch] >= 1f) _ph[ch] -= 1f;");
            sb.AppendLine("if (_ph[ch] < 0.5f) s += vol * 2;");
            sb.AppendLine("}");
            sb.AppendLine("}");
            sb.AppendLine("int sample = 128 + s;");
            sb.AppendLine("if (sample < 0) sample = 0;");
            sb.AppendLine("if (sample > 255) sample = 255;");
            sb.AppendLine("b[i] = (byte)sample;");
            sb.AppendLine("}");
            sb.AppendLine("}");

            sb.AppendLine("}");
            sb.AppendLine();
        }

        void AppendForm(StringBuilder sb)
        {
            sb.AppendLine("class UlaForm : Form");
            sb.AppendLine("{");
            sb.AppendLine("private System.Windows.Forms.Timer _timer;");
            sb.AppendLine("private Bitmap _back;");

            sb.AppendLine("public UlaForm()");
            sb.AppendLine("{");
            sb.AppendLine("Text = \"ZX Lifted: \" + Runtime.GameName;");
            sb.AppendLine("ClientSize = new Size(320, 240);");
            sb.AppendLine("FormBorderStyle = FormBorderStyle.FixedSingle;");
            sb.AppendLine("MaximizeBox = false;");
            sb.AppendLine("StartPosition = FormStartPosition.CenterScreen;");
            sb.AppendLine("SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer, true);");
            sb.AppendLine("_back = new Bitmap(320, 240, PixelFormat.Format32bppArgb);");
            sb.AppendLine("_timer = new System.Windows.Forms.Timer();");
            sb.AppendLine("_timer.Interval = 20;");
            sb.AppendLine("_timer.Tick += delegate(object sender, EventArgs e)");
            sb.AppendLine("{");
            sb.AppendLine("Ula.FrameCount++;");
            sb.AppendLine("Runtime.InterruptPending = true;");
            sb.AppendLine("Ula.RenderToBitmap(_back);");
            sb.AppendLine("Invalidate();");
            sb.AppendLine("};");
            sb.AppendLine("_timer.Start();");
            sb.AppendLine("KeyPreview = true;");
            sb.AppendLine("KeyDown += delegate(object s, KeyEventArgs ke) { SetKey(ke.KeyCode, true); ke.SuppressKeyPress = true; };");
            sb.AppendLine("KeyUp += delegate(object s, KeyEventArgs ke) { SetKey(ke.KeyCode, false); };");
            sb.AppendLine("}");

            sb.AppendLine("protected override void OnPaint(PaintEventArgs e)");
            sb.AppendLine("{");
            sb.AppendLine("base.OnPaint(e);");
            sb.AppendLine("if (_back != null) e.Graphics.DrawImageUnscaled(_back, 0, 0);");
            sb.AppendLine("string line1 = string.Format(\"F:{0} I:{1} PC:{2:X4} T:{3}\", Ula.FrameCount, Runtime.InsCount, Runtime.LastPC, Runtime.TrapCount);");
            sb.AppendLine("string line2 = string.Format(\"CPU:{0} A:{1:X2} F:{2:X2} SP:{3:X4} IM:{4}\", Runtime.CpuState, Runtime.A, Runtime.F, Runtime.SP, Runtime.IM);");
            sb.AppendLine("e.Graphics.FillRectangle(Brushes.Black, 0, 0, 320, 32);");
            sb.AppendLine("e.Graphics.DrawString(line1, SystemFonts.DefaultFont, Brushes.Yellow, 2, 2);");
            sb.AppendLine("e.Graphics.DrawString(line2, SystemFonts.DefaultFont, Brushes.Yellow, 2, 16);");
            sb.AppendLine("if (!string.IsNullOrEmpty(Runtime.LastError))");
            sb.AppendLine("{");
            sb.AppendLine("string msg = Runtime.LastError;");
            sb.AppendLine("if (msg.Length > 64) msg = msg.Substring(0, 64);");
            sb.AppendLine("e.Graphics.FillRectangle(Brushes.Black, 0, 224, 320, 16);");
            sb.AppendLine("e.Graphics.DrawString(msg, SystemFonts.DefaultFont, Brushes.Red, 2, 226);");
            sb.AppendLine("}");
            sb.AppendLine("}");

            sb.AppendLine("static void SetKey(Keys k, bool down)");
            sb.AppendLine("{");
            sb.AppendLine("int row = -1, bit = -1;");
            sb.AppendLine("switch (k)");
            sb.AppendLine("{");
            sb.AppendLine("case Keys.ShiftKey: case Keys.LShiftKey: case Keys.RShiftKey: row = 0; bit = 0; break;");
            sb.AppendLine("case Keys.Z: row = 0; bit = 1; break;");
            sb.AppendLine("case Keys.X: row = 0; bit = 2; break;");
            sb.AppendLine("case Keys.C: row = 0; bit = 3; break;");
            sb.AppendLine("case Keys.V: row = 0; bit = 4; break;");
            sb.AppendLine("case Keys.A: row = 1; bit = 0; break;");
            sb.AppendLine("case Keys.S: row = 1; bit = 1; break;");
            sb.AppendLine("case Keys.D: row = 1; bit = 2; break;");
            sb.AppendLine("case Keys.F: row = 1; bit = 3; break;");
            sb.AppendLine("case Keys.G: row = 1; bit = 4; break;");
            sb.AppendLine("case Keys.Q: row = 2; bit = 0; break;");
            sb.AppendLine("case Keys.W: row = 2; bit = 1; break;");
            sb.AppendLine("case Keys.E: row = 2; bit = 2; break;");
            sb.AppendLine("case Keys.R: row = 2; bit = 3; break;");
            sb.AppendLine("case Keys.T: row = 2; bit = 4; break;");
            sb.AppendLine("case Keys.D1: row = 3; bit = 0; break;");
            sb.AppendLine("case Keys.D2: row = 3; bit = 1; break;");
            sb.AppendLine("case Keys.D3: row = 3; bit = 2; break;");
            sb.AppendLine("case Keys.D4: row = 3; bit = 3; break;");
            sb.AppendLine("case Keys.D5: row = 3; bit = 4; break;");
            sb.AppendLine("case Keys.D0: row = 4; bit = 0; break;");
            sb.AppendLine("case Keys.D9: row = 4; bit = 1; break;");
            sb.AppendLine("case Keys.D8: row = 4; bit = 2; break;");
            sb.AppendLine("case Keys.D7: row = 4; bit = 3; break;");
            sb.AppendLine("case Keys.D6: row = 4; bit = 4; break;");
            sb.AppendLine("case Keys.P: row = 5; bit = 0; break;");
            sb.AppendLine("case Keys.O: row = 5; bit = 1; break;");
            sb.AppendLine("case Keys.I: row = 5; bit = 2; break;");
            sb.AppendLine("case Keys.U: row = 5; bit = 3; break;");
            sb.AppendLine("case Keys.Y: row = 5; bit = 4; break;");
            sb.AppendLine("case Keys.Enter: row = 6; bit = 0; break;");
            sb.AppendLine("case Keys.L: row = 6; bit = 1; break;");
            sb.AppendLine("case Keys.K: row = 6; bit = 2; break;");
            sb.AppendLine("case Keys.J: row = 6; bit = 3; break;");
            sb.AppendLine("case Keys.H: row = 6; bit = 4; break;");
            sb.AppendLine("case Keys.Space: row = 7; bit = 0; break;");
            sb.AppendLine("case Keys.ControlKey: case Keys.LControlKey: case Keys.RControlKey: row = 7; bit = 1; break;");
            sb.AppendLine("case Keys.M: row = 7; bit = 2; break;");
            sb.AppendLine("case Keys.N: row = 7; bit = 3; break;");
            sb.AppendLine("case Keys.B: row = 7; bit = 4; break;");
            sb.AppendLine("case Keys.Up: row = 2; bit = 0; break;");
            sb.AppendLine("case Keys.Down: row = 1; bit = 0; break;");
            sb.AppendLine("case Keys.Left: row = 5; bit = 1; break;");
            sb.AppendLine("case Keys.Right: row = 5; bit = 0; break;");
            sb.AppendLine("}");
            sb.AppendLine("if (row >= 0)");
            sb.AppendLine("{");
            sb.AppendLine("if (down) Runtime.KeyMatrix[row] = (byte)(Runtime.KeyMatrix[row] & ~(1 << bit));");
            sb.AppendLine("else Runtime.KeyMatrix[row] = (byte)(Runtime.KeyMatrix[row] | (1 << bit));");
            sb.AppendLine("}");
            sb.AppendLine("}");

            sb.AppendLine("protected override void OnFormClosing(FormClosingEventArgs e)");
            sb.AppendLine("{");
            sb.AppendLine("base.OnFormClosing(e);");
            sb.AppendLine("_timer.Stop();");
            sb.AppendLine("Audio.TryStop();");
            sb.AppendLine("if (_back != null) { _back.Dispose(); _back = null; }");
            sb.AppendLine("}");

            sb.AppendLine("}");
        }

        void EmitInstruction(StringBuilder sb, InstructionZ80 inst)
        {
            if (inst.Control == OpControlZ80.Invalid || !inst.Supported)
            {
                EmitDynamic(sb, inst.Address);
                return;
            }

            switch (inst.Control)
            {
                case OpControlZ80.Branch:
                    EmitBranch(sb, inst);
                    return;

                case OpControlZ80.Jmp:
                    EmitJmp(sb, inst);
                    return;

                case OpControlZ80.JmpInd:
                    EmitJmpInd(sb, inst);
                    return;

                case OpControlZ80.Call:
                    EmitCall(sb, inst);
                    return;

                case OpControlZ80.Ret:
                    EmitRet(sb, inst);
                    return;

                case OpControlZ80.Rst:
                    EmitRst(sb, inst);
                    return;

                case OpControlZ80.Halt:
                    Line(sb, "Runtime.Halt();");
                    if (inst.HasFallthrough)
                        EmitGoto(sb, inst.Fallthrough);
                    else
                        Line(sb, "Runtime.Trap(\"HALT without fallthrough.\"); return 0;");
                    return;
            }

            if (inst.Prefix != 0 && inst.Prefix != 0xCB && inst.Prefix != 0xED)
            {
                EmitDynamic(sb, inst.Address);
                return;
            }

            EmitNormalExec(sb, inst);

            if (inst.HasFallthrough)
                EmitGoto(sb, inst.Fallthrough);
            else
                Line(sb, "Runtime.Trap(\"No fallthrough.\"); return 0;");
        }

        void EmitDynamic(StringBuilder sb, ushort addr)
        {
            Line(sb, "Runtime.DispatchTarget = 0x" + addr.ToString("X4") + ";");
            Line(sb, "if (!Runtime.ExecDynamicOne(Runtime.DispatchTarget)) { Runtime.Trap(\"Dynamic execution failed at $" + addr.ToString("X4") + "\"); return 0; }");
            Line(sb, "return Runtime.DispatchTarget;");
        }

        void EmitBranch(StringBuilder sb, InstructionZ80 inst)
        {
            if (inst.IsDJNZ)
            {
                Line(sb, "Runtime.B = (byte)(Runtime.B - 1);");
                Line(sb,
                    "return (ushort)(Runtime.B != 0 ? 0x" +
                    inst.Target.ToString("X4") +
                    " : 0x" +
                    inst.Fallthrough.ToString("X4") +
                    ");");
            }
            else
            {
                Line(sb,
                    "return (ushort)(Runtime.Conditional(" +
                    inst.Condition.ToString(CultureInfo.InvariantCulture) +
                    ") ? 0x" +
                    inst.Target.ToString("X4") +
                    " : 0x" +
                    inst.Fallthrough.ToString("X4") +
                    ");");
            }
        }

        void EmitJmp(StringBuilder sb, InstructionZ80 inst)
        {
            if (inst.Condition < 0)
            {
                EmitGoto(sb, inst.Target);
            }
            else
            {
                Line(sb,
                    "return (ushort)(Runtime.Conditional(" +
                    inst.Condition.ToString(CultureInfo.InvariantCulture) +
                    ") ? 0x" +
                    inst.Target.ToString("X4") +
                    " : 0x" +
                    inst.Fallthrough.ToString("X4") +
                    ");");
            }
        }

        void EmitJmpInd(StringBuilder sb, InstructionZ80 inst)
        {
            Line(sb, "return Runtime.HL;");
        }

        void EmitCall(StringBuilder sb, InstructionZ80 inst)
        {
            string push = "Runtime.Push16(0x" + inst.Fallthrough.ToString("X4") + ");";

            if (inst.Condition < 0)
            {
                Line(sb, push);
                EmitGoto(sb, inst.Target);
            }
            else
            {
                Line(sb, "if (Runtime.Conditional(" + inst.Condition.ToString(CultureInfo.InvariantCulture) + "))");
                Line(sb, "{");
                Line(sb, push);
                EmitGoto(sb, inst.Target);
                Line(sb, "}");
                Line(sb, "else");
                Line(sb, "{");
                EmitGoto(sb, inst.Fallthrough);
                Line(sb, "}");
            }
        }

        void EmitRet(StringBuilder sb, InstructionZ80 inst)
        {
            if (inst.Condition < 0)
            {
                Line(sb, "return Runtime.Pop16();");
            }
            else
            {
                Line(sb, "if (Runtime.Conditional(" + inst.Condition.ToString(CultureInfo.InvariantCulture) + ")) return Runtime.Pop16();");
                EmitGoto(sb, inst.Fallthrough);
            }
        }

        void EmitRst(StringBuilder sb, InstructionZ80 inst)
        {
            Line(sb, "Runtime.Push16(0x" + ((ushort)(inst.Address + 1)).ToString("X4") + ");");
            EmitGoto(sb, inst.Target);
        }

        void EmitNormalExec(StringBuilder sb, InstructionZ80 inst)
        {
            if (inst.Prefix == 0)
            {
                Line(sb, "Runtime.ExecBase(0x" + inst.Opcode.ToString("X2") + ", 0x" + inst.Operand.ToString("X4") + ");");
            }
            else if (inst.Prefix == 0xCB)
            {
                Line(sb, "Runtime.ExecCB(0x" + inst.Opcode.ToString("X2") + ");");
            }
            else if (inst.Prefix == 0xED)
            {
                Line(sb, "Runtime.ExecEDNormal(0x" + inst.Opcode.ToString("X2") + ", 0x" + inst.Operand.ToString("X4") + ");");
            }
        }

        void EmitGoto(StringBuilder sb, ushort addr)
        {
            Line(sb, "return 0x" + addr.ToString("X4") + ";");
        }

        void Line(StringBuilder sb, string text)
        {
            sb.AppendLine("    " + text);
        }
    }
}