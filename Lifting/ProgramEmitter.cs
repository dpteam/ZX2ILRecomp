namespace ZX2ILRecomp.Lifting;

/// <summary>
/// Emits the generated game's entry point: windowed mode, headless mode and the
/// console hand-off that lets a WinExe print to a redirected stdout.
/// </summary>
internal static class ProgramEmitter
{
    public static void Emit(EmitWriter w, LifterContext ctx)
    {
        w.Open("static class Program");
        w.Line("[STAThread]");
        w.Open("static int Main(string[] args)");
        w.Line("int headlessFrames = 0;");
        w.Line("int captureSeconds = " + ctx.CaptureIconSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture) + ";");
        w.Line("string screenshotPath = null;");
        w.Blank();
        w.Open("for (int i = 0; i < args.Length; i++)");
        w.Line("string a = args[i];");
        w.Blank();
        w.Line("if (a == \"--headless\")");
        w.Line("{");
        w.Line("    headlessFrames = 60;");
        w.Line("    if (i + 1 < args.Length && int.TryParse(args[i + 1], out int frames)) { headlessFrames = frames; i++; }");
        w.Line("    continue;");
        w.Line("}");
        w.Blank();
        w.Line("if (a == \"--screenshot\")");
        w.Line("{");
        w.Line("    if (i + 1 < args.Length) screenshotPath = args[++i];");
        w.Line("    if (headlessFrames == 0) headlessFrames = 60;");
        w.Line("    continue;");
        w.Line("}");
        w.Blank();
        w.Line("if (a == \"--capture-icon\")");
        w.Line("{");
        w.Line("    captureSeconds = 5;");
        w.Line("    if (i + 1 < args.Length && int.TryParse(args[i + 1], out int seconds)) { captureSeconds = seconds; i++; }");
        w.Line("    continue;");
        w.Line("}");
        w.Blank();
        w.Line("if (a == \"--no-capture-icon\") { captureSeconds = 0; continue; }");
        w.Line("if (a == \"--help\" || a == \"-h\") { PrintHelp(); return 0; }");
        w.Close();
        w.Blank();
        w.Line("if (headlessFrames > 0) return Headless.Run(headlessFrames, screenshotPath);");
        w.Blank();
        w.Open("try");
        w.Line("Application.EnableVisualStyles();");
        w.Line("Application.SetCompatibleTextRenderingDefault(false);");
        w.Line("Runtime.Init();");
        w.Line("UlaForm form = new UlaForm(captureSeconds);");
        w.Line("Thread cpuThread = new Thread(new ThreadStart(Runtime.CpuThread));");
        w.Line("cpuThread.IsBackground = true;");
        w.Line("cpuThread.Priority = ThreadPriority.BelowNormal;");
        w.Line("cpuThread.Start();");
        w.Line("Application.Run(form);");
        w.Line("return 0;");
        w.Close();
        w.Open("catch (Exception ex)");
        w.Line("try { File.WriteAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, \"fatal.log\"), ex.ToString()); } catch { }");
        w.Line("MessageBox.Show(ex.ToString(), \"ZX Lifted: fatal error\", MessageBoxButtons.OK, MessageBoxIcon.Error);");
        w.Line("return 1;");
        w.Close();
        w.Close();
        w.Blank();

        EmitHelp(w, ctx);
        w.Blank();

        w.Open("static class Headless");
        w.Open("public static int Run(int frames, string screenshotPath)");
        w.Line("ConsoleAttach.Try();");
        w.Line("Runtime.HeadlessMode = true;");
        w.Line("Runtime.ThrottleEnabled = false;");
        w.Line("Runtime.FrameLimit = frames;");
        w.Line("Runtime.Init();");
        w.Blank();
        w.Line("Thread cpuThread = new Thread(new ThreadStart(Runtime.CpuThread));");
        w.Line("cpuThread.IsBackground = true;");
        w.Line("cpuThread.Start();");
        w.Blank();
        w.Line("System.Diagnostics.Stopwatch clock = System.Diagnostics.Stopwatch.StartNew();");
        w.Blank();
        w.Open("while (cpuThread.IsAlive)");
        w.Line("if (clock.ElapsedMilliseconds > 180000) break;");
        w.Line("Thread.Sleep(1);");
        w.Close();
        w.Blank();
        w.Line("Console.WriteLine(\"headless: frames=\" + Runtime.Frames + \" instructions=\" + Runtime.InsCount +");
        w.Line("                  \" traps=\" + Runtime.TrapCount + \" tstates=\" + Runtime.TStates);");
        w.Blank();
        w.Open("if (Runtime.TrapCount > 0)");
        w.Line("Console.WriteLine(\"trap: \" + Runtime.LastError);");
        w.Line("return 3;");
        w.Close();
        w.Blank();
        w.Open("if (screenshotPath != null)");
        w.Line("try");
        w.Line("{");
        w.Line("    using (Bitmap frame = new Bitmap(Ula.Width, Ula.Height, PixelFormat.Format32bppArgb))");
        w.Line("    {");
        w.Line("        Ula.RenderToBitmap(frame);");
        w.Line("        frame.Save(screenshotPath, ImageFormat.Png);");
        w.Line("    }");
        w.Line("    Console.WriteLine(\"screenshot: \" + screenshotPath);");
        w.Line("}");
        w.Line("catch (Exception ex) { Console.WriteLine(\"screenshot failed: \" + ex.Message); }");
        w.Close();
        w.Blank();
        w.Line("return Runtime.Frames >= frames ? 0 : 4;");
        w.Close();
        w.Close();
        w.Blank();

        w.Open("static class ConsoleAttach");
        w.Line("[DllImport(\"kernel32.dll\", SetLastError = true)]");
        w.Line("static extern bool AttachConsole(int processId);");
        w.Blank();
        w.Open("public static void Try()");
        w.Open("try");
        w.Line("if (!AttachConsole(-1)) return;");
        w.Line("StreamWriter stdout = new StreamWriter(Console.OpenStandardOutput());");
        w.Line("stdout.AutoFlush = true;");
        w.Line("Console.SetOut(stdout);");
        w.Close();
        w.Line("catch { }");
        w.Close();
        w.Close();
        w.Blank();

        // Close the Program class itself: everything above is nested in it.
        w.Close();
        w.Blank();
    }

    private static void EmitHelp(EmitWriter w, LifterContext ctx)
    {
        w.Open("static void PrintHelp()");
        w.Line("Console.WriteLine();");
        w.Line("Console.WriteLine(\"ZX Lifted: " + EmitWriter.Escape(ctx.DisplayName) + "\");");
        w.Line("Console.WriteLine(\"Static recompilation of a ZX Spectrum snapshot.\");");
        w.Line("Console.WriteLine();");
        w.Line("Console.WriteLine(\"Usage:\");");
        w.Line("Console.WriteLine(\"  <game>.exe                       play in a window\");");
        w.Line("Console.WriteLine(\"  <game>.exe --headless 600        run 600 frames with no window\");");
        w.Line("Console.WriteLine(\"  <game>.exe --capture-icon 5      screenshot itself into an .ico\");");
        w.Line("Console.WriteLine(\"  <game>.exe --no-capture-icon     never capture an icon\");");
        w.Line("Console.WriteLine();");
        w.Line("Console.WriteLine(\"In game: F1 toggles the diagnostics overlay, F2 toggles sound.\");");
        w.Line("Console.WriteLine();");
        w.Close();
    }
}
