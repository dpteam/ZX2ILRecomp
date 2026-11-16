using System.Diagnostics;
using System.Globalization;
using ZX2ILRecomp.Compilation;

namespace ZX2ILRecomp.Host;

/// <summary>
/// Native WinForms front end.
///
/// Deliberately built out of stock controls: the project has no third-party
/// dependencies, so there is no web view, no UI framework and no CSS — just a
/// dark panel, a log view and the same pipeline the CLI runs. Everything the
/// window does is also reachable from the command line, so the GUI can never
/// drift out of feature parity.
/// </summary>
public sealed class GuiHost : Form
{
    private static readonly Color Background = Color.FromArgb(24, 26, 32);
    private static readonly Color Panel = Color.FromArgb(32, 35, 43);
    private static readonly Color Border = Color.FromArgb(56, 62, 76);
    private static readonly Color Ink = Color.FromArgb(232, 232, 238);
    private static readonly Color Muted = Color.FromArgb(140, 148, 164);
    private static readonly Color Accent = Color.FromArgb(230, 70, 70);
    private static readonly Color AccentHover = Color.FromArgb(245, 96, 96);

    private readonly TextBox _input = new();
    private readonly TextBox _output = new();
    private readonly CheckBox _recursive = new();
    private readonly CheckBox _keepSource = new();
    private readonly ComboBox _model = new();
    private readonly ComboBox _package = new();
    private readonly NumericUpDown _capture = new();
    private readonly Button _lift = new();
    private readonly Button _selfTest = new();
    private readonly Button _open = new();
    private readonly RichTextBox _log = new();
    private readonly Label _status = new();
    private readonly ProgressBar _progress = new();

    private Pipeline? _pipeline;
    private bool _running;

    public GuiHost(CliOptions options)
    {
        Text = "ZX2ILRecomp - static ZX Spectrum recompiler";
        ClientSize = new Size(1100, 700);
        MinimumSize = new Size(900, 560);
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Background;
        ForeColor = Ink;
        Font = new Font("Segoe UI", 9f);
        Icon = LoadAppIcon();

        BuildLayout(options);

        Log.Written += OnLogEntry;

        foreach (LogEntry entry in Log.History)
        {
            AppendEntry(entry);
        }

        FormClosing += (_, _) => Log.Written -= OnLogEntry;
    }

    private static Icon? LoadAppIcon()
    {
        try
        {
            string path = Path.Combine(AppContext.BaseDirectory, "assets", "icon.ico");

            if (File.Exists(path))
            {
                return new Icon(path);
            }
        }
        catch
        {
            // A missing icon must not stop the window from opening.
        }

        return null;
    }

    private void BuildLayout(CliOptions options)
    {
        Panel side = new()
        {
            Dock = DockStyle.Left,
            Width = 372,
            BackColor = Panel,
            Padding = new Padding(20, 18, 20, 18)
        };

        // ---- header -----------------------------------------------------
        Label title = new()
        {
            Text = "ZX2IL",
            Font = new Font("Segoe UI Semibold", 22f),
            ForeColor = Ink,
            AutoSize = true,
            Location = new Point(20, 16)
        };

        Label titleAccent = new()
        {
            Text = "Recomp",
            Font = new Font("Segoe UI Semibold", 22f),
            ForeColor = Accent,
            AutoSize = true,
            Location = new Point(20 + 78, 16)
        };

        Label subtitle = new()
        {
            Text = "Z80 in, readable C# out",
            Font = new Font("Segoe UI", 9f),
            ForeColor = Muted,
            AutoSize = true,
            Location = new Point(22, 58)
        };

        // ---- input / output --------------------------------------------
        int y = 100;

        side.Controls.Add(Label("Snapshot or folder", 22, y));
        y += 22;

        StyleField(_input, options.InputPath);
        _input.Location = new Point(22, y);
        _input.Width = 250;
        side.Controls.Add(_input);

        Button browseInput = SmallButton("...", 280, y, 68);
        browseInput.Click += (_, _) => BrowseInput();
        side.Controls.Add(browseInput);

        y += 44;
        side.Controls.Add(Label("Output folder", 22, y));
        y += 22;

        StyleField(_output, options.OutputPath);
        _output.Location = new Point(22, y);
        _output.Width = 250;
        side.Controls.Add(_output);

        Button browseOutput = SmallButton("...", 280, y, 68);
        browseOutput.Click += (_, _) => BrowseOutput();
        side.Controls.Add(browseOutput);

        // ---- options ----------------------------------------------------
        y += 46;

        StyleCheck(_recursive, "Scan sub-folders", options.Recursive);
        _recursive.Location = new Point(20, y);
        side.Controls.Add(_recursive);

        y += 28;

        StyleCheck(_keepSource, "Keep generated C#", options.SaveSource);
        _keepSource.Location = new Point(20, y);
        side.Controls.Add(_keepSource);

        y += 36;
        side.Controls.Add(Label("Spectrum model", 22, y));
        y += 20;

        StyleCombo(_model, 22, y, 326);
        _model.Items.AddRange(new object[] { "Auto-detect", "48K", "128K" });
        _model.SelectedIndex = options.Model switch { 48 => 1, 128 => 2, _ => 0 };
        side.Controls.Add(_model);

        y += 44;
        side.Controls.Add(Label("Package", 22, y));
        y += 20;

        StyleCombo(_package, 22, y, 326);
        _package.Items.AddRange(new object[]
        {
            "Game.exe + Game.dll (needs .NET)",
            "Game.dll only (dotnet Game.dll)",
            "Single self-contained .exe"
        });
        _package.SelectedIndex = options.PackageMode switch
        {
            PackageMode.AssemblyOnly => 1,
            PackageMode.SelfContained => 2,
            _ => 0
        };
        side.Controls.Add(_package);

        y += 44;
        side.Controls.Add(Label("Capture icon after", 22, y));

        _capture.Location = new Point(190, y - 4);
        _capture.Width = 74;
        _capture.Minimum = 0;
        _capture.Maximum = 60;
        _capture.Value = Math.Clamp(options.CaptureIconSeconds, 0, 60);
        _capture.BackColor = Background;
        _capture.ForeColor = Ink;
        _capture.BorderStyle = BorderStyle.FixedSingle;
        side.Controls.Add(_capture);

        Label seconds = Label("seconds (0 = off)", 268, y);
        side.Controls.Add(seconds);

        // ---- actions ----------------------------------------------------
        y += 44;

        StylePrimary(_lift, "Lift");
        _lift.Location = new Point(22, y);
        _lift.Width = 150;
        _lift.Click += (_, _) => StartLift();
        side.Controls.Add(_lift);

        StyleSecondary(_selfTest, "Self-test");
        _selfTest.Location = new Point(182, y);
        _selfTest.Width = 166;
        _selfTest.Click += (_, _) => StartSelfTest();
        side.Controls.Add(_selfTest);

        y += 44;

        StyleSecondary(_open, "Open output folder");
        _open.Location = new Point(22, y);
        _open.Width = 326;
        _open.Click += (_, _) => OpenOutput();
        side.Controls.Add(_open);

        // ---- status -----------------------------------------------------
        _progress.Dock = DockStyle.Bottom;
        _progress.Height = 6;
        _progress.Style = ProgressBarStyle.Continuous;
        _progress.Maximum = 1000;
        _progress.BackColor = Background;

        _status.Dock = DockStyle.Bottom;
        _status.Height = 30;
        _status.TextAlign = ContentAlignment.MiddleLeft;
        _status.Padding = new Padding(22, 0, 0, 0);
        _status.ForeColor = Muted;
        _status.Text = "Idle";

        side.Controls.Add(_status);
        side.Controls.Add(_progress);

        // ---- log --------------------------------------------------------
        _log.Dock = DockStyle.Fill;
        _log.BackColor = Color.FromArgb(16, 17, 21);
        _log.ForeColor = Ink;
        _log.BorderStyle = BorderStyle.None;
        _log.Font = new Font("Cascadia Mono", 9f);
        _log.ReadOnly = true;
        _log.WordWrap = false;
        _log.DetectUrls = false;
        _log.ScrollBars = RichTextBoxScrollBars.Both;
        _log.HideSelection = false;

        Panel logHost = new()
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(14, 14, 14, 14),
            BackColor = Background
        };

        logHost.Controls.Add(_log);

        Controls.Add(logHost);
        Controls.Add(side);

        side.Controls.Add(title);
        side.Controls.Add(titleAccent);
        side.Controls.Add(subtitle);

        // Keep the header above the panels that were added earlier.
        title.BringToFront();
        titleAccent.BringToFront();
        subtitle.BringToFront();
    }

    private static Label Label(string text, int x, int y)
    {
        return new Label
        {
            Text = text,
            ForeColor = Muted,
            AutoSize = true,
            Location = new Point(x, y)
        };
    }

    private static void StyleField(TextBox box, string value)
    {
        box.Text = value;
        box.BorderStyle = BorderStyle.FixedSingle;
        box.BackColor = Background;
        box.ForeColor = Ink;
    }

    private static void StyleCheck(CheckBox box, string text, bool value)
    {
        box.Text = text;
        box.Checked = value;
        box.ForeColor = Ink;
        box.AutoSize = true;
        box.FlatStyle = FlatStyle.Flat;
    }

    private static void StyleCombo(ComboBox box, int x, int y, int width)
    {
        box.DropDownStyle = ComboBoxStyle.DropDownList;
        box.FlatStyle = FlatStyle.Flat;
        box.BackColor = Background;
        box.ForeColor = Ink;
        box.Location = new Point(x, y);
        box.Width = width;
    }

    private static Button SmallButton(string text, int x, int y, int width)
    {
        Button button = new()
        {
            Text = text,
            Location = new Point(x, y),
            Width = width,
            Height = 26,
            FlatStyle = FlatStyle.Flat,
            BackColor = Color.FromArgb(44, 48, 58),
            ForeColor = Ink
        };

        button.FlatAppearance.BorderColor = Border;
        return button;
    }

    private static void StylePrimary(Button button, string text)
    {
        button.Text = text;
        button.Height = 36;
        button.FlatStyle = FlatStyle.Flat;
        button.BackColor = Accent;
        button.ForeColor = Color.White;
        button.Font = new Font("Segoe UI Semibold", 10f);
        button.FlatAppearance.BorderSize = 0;
        button.FlatAppearance.MouseOverBackColor = AccentHover;
    }

    private static void StyleSecondary(Button button, string text)
    {
        button.Text = text;
        button.Height = 36;
        button.FlatStyle = FlatStyle.Flat;
        button.BackColor = Color.FromArgb(44, 48, 58);
        button.ForeColor = Ink;
        button.FlatAppearance.BorderColor = Border;
        button.FlatAppearance.MouseOverBackColor = Color.FromArgb(56, 62, 76);
    }

    // -----------------------------------------------------------------
    // Actions
    // -----------------------------------------------------------------

    private void BrowseInput()
    {
        using FolderBrowserDialog folder = new()
        {
            Description = "Folder with .z80 / .sna / .szx / .tap / .tzx files"
        };

        if (folder.ShowDialog(this) == DialogResult.OK)
        {
            _input.Text = folder.SelectedPath;
        }
    }

    private void BrowseOutput()
    {
        using FolderBrowserDialog folder = new()
        {
            Description = "Where lifted games are written"
        };

        if (folder.ShowDialog(this) == DialogResult.OK)
        {
            _output.Text = folder.SelectedPath;
        }
    }

    private void OpenOutput()
    {
        try
        {
            Directory.CreateDirectory(_output.Text);
            Process.Start(new ProcessStartInfo
            {
                FileName = _output.Text,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            Log.Warn("Could not open the output folder: " + ex.Message);
        }
    }

    private void StartLift()
    {
        if (_running)
        {
            _pipeline!.CancelRequested = true;
            _status.Text = "Stopping after the current snapshot...";
            return;
        }

        CliOptions options = BuildOptions();

        if (string.IsNullOrWhiteSpace(options.InputPath))
        {
            MessageBox.Show(this, "Pick a snapshot or a folder first.", "ZX2ILRecomp",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        RunBatch(options, runSelfTest: false);
    }

    private void StartSelfTest()
    {
        if (_running)
        {
            return;
        }

        CliOptions options = BuildOptions();
        RunBatch(options, runSelfTest: true);
    }

    private CliOptions BuildOptions()
    {
        CliOptions options = new()
        {
            InputPath = _input.Text.Trim(),
            OutputPath = _output.Text.Trim(),
            Recursive = _recursive.Checked,
            RecursiveSpecified = true,
            SaveSource = _keepSource.Checked,
            Model = _model.SelectedIndex switch { 1 => 48, 2 => 128, _ => 0 },
            PackageMode = _package.SelectedIndex switch
            {
                1 => PackageMode.AssemblyOnly,
                2 => PackageMode.SelfContained,
                _ => PackageMode.AppHost
            },
            CaptureIconSeconds = (int)_capture.Value,
            Fresh = false
        };

        if (string.IsNullOrWhiteSpace(options.OutputPath))
        {
            options.OutputPath = Path.Combine(Environment.CurrentDirectory, "zx_lifted_output");
            _output.Text = options.OutputPath;
        }

        return options;
    }

    private void RunBatch(CliOptions options, bool runSelfTest)
    {
        _running = true;
        _lift.Text = "Stop";
        _selfTest.Enabled = false;
        _progress.Value = 0;
        _status.Text = runSelfTest ? "Running self-test..." : "Lifting...";
        Log.Clear();
        _log.Clear();

        Task.Run(() =>
        {
            int exitCode;

            try
            {
                if (runSelfTest)
                {
                    exitCode = new SelfTest(options).Run();
                }
                else
                {
                    Pipeline pipeline = new(options)
                    {
                        OnStatus = name => BeginInvoke(() => _status.Text = "Lifting " + name),
                        OnProgress = (done, total) => BeginInvoke(() =>
                        {
                            _progress.Value = total <= 0
                                ? 0
                                : Math.Clamp((int)(1000L * done / total), 0, 1000);
                        })
                    };

                    _pipeline = pipeline;
                    exitCode = pipeline.Run();
                }
            }
            catch (Exception ex)
            {
                Log.Error("Fatal: " + ex.Message);
                Log.Debug(ex.ToString());
                exitCode = 1;
            }

            BeginInvoke(() =>
            {
                _running = false;
                _pipeline = null;
                _lift.Text = "Lift";
                _selfTest.Enabled = true;
                _progress.Value = exitCode == 0 ? 1000 : 0;
                _status.Text = exitCode switch
                {
                    0 => "Done",
                    2 => "Done with failures - see the log",
                    _ => "Stopped with an error - see the log"
                };
            });
        });
    }

    // -----------------------------------------------------------------
    // Log plumbing
    // -----------------------------------------------------------------

    private void OnLogEntry(LogEntry entry)
    {
        if (IsDisposed)
        {
            return;
        }

        try
        {
            if (InvokeRequired)
            {
                BeginInvoke(() => AppendEntry(entry));
                return;
            }

            AppendEntry(entry);
        }
        catch
        {
            // The window is closing; dropping a log line is fine.
        }
    }

    private void AppendEntry(LogEntry entry)
    {
        if (_log.IsDisposed)
        {
            return;
        }

        Color color = entry.Level switch
        {
            LogLevel.Error => Color.FromArgb(255, 120, 120),
            LogLevel.Warn => Color.FromArgb(240, 200, 100),
            LogLevel.Ok => Color.FromArgb(130, 220, 140),
            LogLevel.Step => Color.FromArgb(120, 200, 255),
            LogLevel.Debug => Color.FromArgb(120, 126, 140),
            _ => Ink
        };

        _log.SelectionStart = _log.TextLength;
        _log.SelectionLength = 0;
        _log.SelectionColor = color;
        _log.AppendText(entry.ToString() + Environment.NewLine);
        _log.SelectionColor = _log.ForeColor;
        _log.ScrollToCaret();
    }

    /// <summary>Formats a byte count for the status line.</summary>
    internal static string Bytes(long value)
    {
        return value.ToString("N0", CultureInfo.InvariantCulture) + " B";
    }
}
