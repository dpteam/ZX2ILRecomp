using System.Globalization;

namespace ZX2ILRecomp.Lifting;

/// <summary>
/// Emits the WinForms window: double-buffered 2x blit of the ULA frame, the
/// Spectrum keyboard matrix, the diagnostics overlay and icon self-capture.
/// </summary>
internal static class FormEmitter
{
    public static void Emit(EmitWriter w, LifterContext ctx)
    {
        w.Open("sealed class UlaForm : Form");
        w.Line("const int Scale = 2;");
        w.Line("readonly System.Windows.Forms.Timer _timer;");
        w.Line("Bitmap _frame;");
        w.Line("readonly int _captureSeconds;");
        w.Line("int _elapsedSeconds;");
        w.Line("bool _iconSaved;");
        w.Blank();

        w.Open("public UlaForm(int captureSeconds)");
        w.Line("_captureSeconds = captureSeconds;");
        w.Line("Text = \"ZX Lifted: \" + Runtime.GameName;");
        w.Line("ClientSize = new Size(Ula.Width * Scale, Ula.Height * Scale);");
        w.Line("MinimumSize = ClientSize;");
        w.Line("FormBorderStyle = FormBorderStyle.FixedSingle;");
        w.Line("MaximizeBox = false;");
        w.Line("StartPosition = FormStartPosition.CenterScreen;");
        w.Line("BackColor = Color.Black;");
        w.Line("KeyPreview = true;");
        w.Line("SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer, true);");
        w.Blank();
        w.Line("_frame = new Bitmap(Ula.Width, Ula.Height, PixelFormat.Format32bppArgb);");
        w.Blank();
        w.Line("_timer = new System.Windows.Forms.Timer();");
        w.Line("_timer.Interval = 20;   // 50 Hz, one Spectrum frame");
        w.Line("_timer.Tick += OnTick;");
        w.Line("_timer.Start();");
        w.Blank();
        w.Line("KeyDown += delegate(object sender, KeyEventArgs e) { Runtime.LastKey = (int)e.KeyCode; SetKey(e.KeyCode, true); e.SuppressKeyPress = true; };");
        w.Line("KeyUp += delegate(object sender, KeyEventArgs e) { Runtime.LastKey = -1; SetKey(e.KeyCode, false); };");
        w.Line("Load += delegate(object sender, EventArgs e) { Activate(); Focus(); };");
        w.Close();
        w.Blank();

        w.Open("void OnTick(object sender, EventArgs e)");
        w.Line("Ula.FrameCount++;");
        w.Blank();
        w.Open("if (_captureSeconds > 0 && !_iconSaved)");
        w.Line("_elapsedSeconds++;");
        w.Open("if (_elapsedSeconds >= _captureSeconds * 50)");
        w.Line("_iconSaved = true;");
        w.Line("TrySaveIcon();");
        w.Close();
        w.Close();
        w.Blank();
        w.Line("Invalidate();");
        w.Close();
        w.Blank();

        w.Open("protected override void OnPaint(PaintEventArgs e)");
        w.Line("base.OnPaint(e);");
        w.Blank();
        w.Line("if (_frame == null) return;");
        w.Line("Ula.RenderToBitmap(_frame);");
        w.Blank();
        w.Line("e.Graphics.InterpolationMode = InterpolationMode.NearestNeighbor;");
        w.Line("e.Graphics.PixelOffsetMode = PixelOffsetMode.Half;");
        w.Line("e.Graphics.DrawImage(_frame, ClientRectangle);");
        w.Blank();
        w.Open("if (Runtime.ShowDebug)");
        w.Line("DrawOverlay(e.Graphics);");
        w.Close();
        w.Blank();
        w.Open("if (Runtime.LastError != null)");
        w.Line("using (Font font = new Font(FontFamily.GenericMonospace, 8f))");
        w.Line("{");
        w.Line("    e.Graphics.FillRectangle(Brushes.Black, 0, ClientSize.Height - 18, ClientSize.Width, 18);");
        w.Line("    e.Graphics.DrawString(Trim(Runtime.LastError, 96), font, Brushes.Red, 4, ClientSize.Height - 17);");
        w.Line("}");
        w.Close();
        w.Close();
        w.Blank();

        w.Open("void DrawOverlay(Graphics g)");
        w.Line("using (Font font = new Font(FontFamily.GenericMonospace, 8f))");
        w.Line("{");
        w.Line("    g.FillRectangle(Brushes.Black, 0, 0, ClientSize.Width, 62);");
        w.Line("    string line1 = string.Format(\"frame {0}  ins {1}  pc {2:X4}  traps {3}\", Ula.FrameCount, Runtime.InsCount, Runtime.LastPC, Runtime.TrapCount);");
        w.Line("    string line2 = string.Format(\"AF {0:X4}  BC {1:X4}  DE {2:X4}  HL {3:X4}  SP {4:X4}\", Runtime.AF, Runtime.BC, Runtime.DE, Runtime.HL, Runtime.SP);");
        w.Line("    string line3 = string.Format(\"state {0}  IM {1}  IFF1 {2}  border {3}  key {4:X4}/{5:X2}\", Runtime.CpuState, Runtime.IM, Runtime.IFF1 ? 1 : 0, Runtime.Border, Runtime.LastKeyPort, Runtime.LastKeyRead);");
        w.Line("    g.DrawString(line1, font, Brushes.Lime, 4, 2);");
        w.Line("    g.DrawString(line2, font, Brushes.Lime, 4, 20);");
        w.Line("    g.DrawString(line3, font, Brushes.Lime, 4, 38);");
        w.Line("}");
        w.Close();
        w.Blank();

        w.Open("void TrySaveIcon()");
        w.Open("try");
        w.Line("if (_frame == null) return;");
        w.Line("string name = System.Reflection.Assembly.GetEntryAssembly().GetName().Name;");
        w.Line("string path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, name + \".icon.ico\");");
        w.Line("SaveIco(_frame, path);");
        w.Line("Console.WriteLine(\"icon: \" + path);");
        w.Close();
        w.Line("catch (Exception ex) { Console.WriteLine(\"icon capture failed: \" + ex.Message); }");
        w.Close();
        w.Blank();

        EmitIcoWriter(w);

        EmitKeyMapping(w);

        w.Open("protected override void OnFormClosing(FormClosingEventArgs e)");
        w.Line("base.OnFormClosing(e);");
        w.Line("_timer.Stop();");
        w.Line("_timer.Dispose();");
        w.Line("Audio.TryStop();");
        w.Line("if (_frame != null) { _frame.Dispose(); _frame = null; }");
        w.Close();
        w.Blank();

        w.Line("static string Trim(string text, int max)");
        w.Line("{");
        w.Line("    if (text == null) return string.Empty;");
        w.Line("    return text.Length <= max ? text : text.Substring(0, max) + \"...\";");
        w.Line("}");
        w.Blank();

        w.Close();
        w.Blank();
    }

    /// <summary>
    /// A minimal ICO writer: one 256x256 PNG-compressed entry, which every
    /// Windows version since Vista accepts. Kept inline so a generated game
    /// stays a single source file with no helper library.
    /// </summary>
    private static void EmitIcoWriter(EmitWriter w)
    {
        w.Open("static void SaveIco(Bitmap source, string path)");
        w.Line("using (Bitmap sized = new Bitmap(256, 256, PixelFormat.Format32bppArgb))");
        w.Line("{");
        w.Line("    using (Graphics g = Graphics.FromImage(sized))");
        w.Line("    {");
        w.Line("        g.InterpolationMode = InterpolationMode.HighQualityBicubic;");
        w.Line("        g.PixelOffsetMode = PixelOffsetMode.HighQuality;");
        w.Line("        g.DrawImage(source, 0, 0, 256, 256);");
        w.Line("    }");
        w.Blank();
        w.Line("    byte[] png;");
        w.Line("    using (MemoryStream memory = new MemoryStream())");
        w.Line("    {");
        w.Line("        sized.Save(memory, ImageFormat.Png);");
        w.Line("        png = memory.ToArray();");
        w.Line("    }");
        w.Blank();
        w.Line("    using (FileStream file = new FileStream(path, FileMode.Create, FileAccess.Write))");
        w.Line("    using (BinaryWriter writer = new BinaryWriter(file))");
        w.Line("    {");
        w.Line("        writer.Write((ushort)0);          // reserved");
        w.Line("        writer.Write((ushort)1);          // type: icon");
        w.Line("        writer.Write((ushort)1);          // one image");
        w.Line("        writer.Write((byte)0);            // width 256 is encoded as 0");
        w.Line("        writer.Write((byte)0);            // height 256 is encoded as 0");
        w.Line("        writer.Write((byte)0);            // palette size");
        w.Line("        writer.Write((byte)0);            // reserved");
        w.Line("        writer.Write((ushort)1);          // colour planes");
        w.Line("        writer.Write((ushort)32);         // bits per pixel");
        w.Line("        writer.Write(png.Length);");
        w.Line("        writer.Write((uint)22);           // 6-byte header + 16-byte entry");
        w.Line("        writer.Write(png);");
        w.Line("    }");
        w.Line("}");
        w.Close();
        w.Blank();
    }

    private static void EmitKeyMapping(EmitWriter w)
    {
        w.Open("void SetKey(Keys key, bool down)");
        w.Line("int row = -1, bit = -1;");
        w.Blank();
        w.Line("// Spectrum matrix: row 0 is CAPS SHIFT..V, row 7 is SPACE..B.");
        w.Open("switch (key)");
        w.Line("case Keys.ShiftKey: case Keys.LShiftKey: case Keys.RShiftKey: row = 0; bit = 0; break;");
        w.Line("case Keys.Z: row = 0; bit = 1; break;");
        w.Line("case Keys.X: row = 0; bit = 2; break;");
        w.Line("case Keys.C: row = 0; bit = 3; break;");
        w.Line("case Keys.V: row = 0; bit = 4; break;");
        w.Blank();
        w.Line("case Keys.A: row = 1; bit = 0; break;");
        w.Line("case Keys.S: row = 1; bit = 1; break;");
        w.Line("case Keys.D: row = 1; bit = 2; break;");
        w.Line("case Keys.F: row = 1; bit = 3; break;");
        w.Line("case Keys.G: row = 1; bit = 4; break;");
        w.Blank();
        w.Line("case Keys.Q: row = 2; bit = 0; break;");
        w.Line("case Keys.W: row = 2; bit = 1; break;");
        w.Line("case Keys.E: row = 2; bit = 2; break;");
        w.Line("case Keys.R: row = 2; bit = 3; break;");
        w.Line("case Keys.T: row = 2; bit = 4; break;");
        w.Blank();
        w.Line("case Keys.D1: row = 3; bit = 0; break;");
        w.Line("case Keys.D2: row = 3; bit = 1; break;");
        w.Line("case Keys.D3: row = 3; bit = 2; break;");
        w.Line("case Keys.D4: row = 3; bit = 3; break;");
        w.Line("case Keys.D5: row = 3; bit = 4; break;");
        w.Blank();
        w.Line("case Keys.D0: row = 4; bit = 0; break;");
        w.Line("case Keys.D9: row = 4; bit = 1; break;");
        w.Line("case Keys.D8: row = 4; bit = 2; break;");
        w.Line("case Keys.D7: row = 4; bit = 3; break;");
        w.Line("case Keys.D6: row = 4; bit = 4; break;");
        w.Blank();
        w.Line("case Keys.P: row = 5; bit = 0; break;");
        w.Line("case Keys.O: row = 5; bit = 1; break;");
        w.Line("case Keys.I: row = 5; bit = 2; break;");
        w.Line("case Keys.U: row = 5; bit = 3; break;");
        w.Line("case Keys.Y: row = 5; bit = 4; break;");
        w.Blank();
        w.Line("case Keys.Enter: row = 6; bit = 0; break;");
        w.Line("case Keys.L: row = 6; bit = 1; break;");
        w.Line("case Keys.K: row = 6; bit = 2; break;");
        w.Line("case Keys.J: row = 6; bit = 3; break;");
        w.Line("case Keys.H: row = 6; bit = 4; break;");
        w.Blank();
        w.Line("case Keys.Space: row = 7; bit = 0; break;");
        w.Line("case Keys.ControlKey: case Keys.LControlKey: case Keys.RControlKey: row = 7; bit = 1; break;");
        w.Line("case Keys.M: row = 7; bit = 2; break;");
        w.Line("case Keys.N: row = 7; bit = 3; break;");
        w.Line("case Keys.B: row = 7; bit = 4; break;");
        w.Blank();
        w.Line("// Cursor keys duplicate the classic QAOP + SPACE layout.");
        w.Line("case Keys.Up: row = 2; bit = 0; break;");
        w.Line("case Keys.Down: row = 1; bit = 0; break;");
        w.Line("case Keys.Left: row = 5; bit = 1; break;");
        w.Line("case Keys.Right: row = 5; bit = 0; break;");
        w.Blank();
        w.Line("// Emulator-style hotkeys.");
        w.Line("case Keys.F1: if (down) Runtime.ShowDebug = !Runtime.ShowDebug; break;");
        w.Line("case Keys.F2: if (down) { Audio.Enabled = !Audio.Enabled; if (Audio.Enabled) Audio.TryStart(); } break;");
        w.Line("case Keys.Escape: if (down) Close(); break;");
        w.Close();
        w.Blank();
        w.Open("if (row >= 0)");
        w.Line("if (down) Runtime.KeyMatrix[row] = (byte)(Runtime.KeyMatrix[row] & ~(1 << bit));");
        w.Line("else Runtime.KeyMatrix[row] = (byte)(Runtime.KeyMatrix[row] | (1 << bit));");
        w.Close();
        w.Close();
        w.Blank();
    }
}
