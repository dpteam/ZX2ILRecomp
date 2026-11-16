using System.Globalization;

namespace ZX2ILRecomp.Lifting;

/// <summary>
/// Emits the sound path: a 1-bit beeper plus the three AY tone channels, mixed
/// into a 22.05 kHz unsigned 8-bit stream and pushed to <c>winmm</c> with two
/// rotating buffers.
///
/// No third-party audio stack is involved, which is why it is a small ring of
/// <c>waveOut</c> calls rather than a mixer graph.
/// </summary>
internal static class AudioEmitter
{
    public static void Emit(EmitWriter w, LifterContext ctx)
    {
        w.Open("static class Audio");

        w.Line("[StructLayout(LayoutKind.Sequential)]");
        w.Line("struct WAVEFORMATEX { public ushort wFormatTag; public ushort nChannels; public uint nSamplesPerSec; public uint nAvgBytesPerSec; public ushort nBlockAlign; public ushort wBitsPerSample; public ushort cbSize; }");
        w.Line("[StructLayout(LayoutKind.Sequential)]");
        w.Line("struct WAVEHDR { public IntPtr lpData; public uint dwBufferLength; public uint dwBytesRecorded; public IntPtr dwUser; public uint dwFlags; public uint dwLoops; public IntPtr lpNext; public IntPtr reserved; }");
        w.Blank();
        w.Line("[DllImport(\"winmm.dll\")] static extern int waveOutOpen(out IntPtr handle, int device, ref WAVEFORMATEX format, IntPtr callback, IntPtr instance, int flags);");
        w.Line("[DllImport(\"winmm.dll\")] static extern int waveOutPrepareHeader(IntPtr handle, ref WAVEHDR header, int size);");
        w.Line("[DllImport(\"winmm.dll\")] static extern int waveOutWrite(IntPtr handle, ref WAVEHDR header, int size);");
        w.Line("[DllImport(\"winmm.dll\")] static extern int waveOutUnprepareHeader(IntPtr handle, ref WAVEHDR header, int size);");
        w.Line("[DllImport(\"winmm.dll\")] static extern int waveOutReset(IntPtr handle);");
        w.Line("[DllImport(\"winmm.dll\")] static extern int waveOutClose(IntPtr handle);");
        w.Blank();

        w.Line("const int SampleRate = 22050;");
        w.Line("const int BlockSize = 2048;");
        w.Blank();
        w.Line("static IntPtr _handle;");
        w.Line("static bool _open;");
        w.Line("static Thread _thread;");
        w.Line("static volatile bool _run;");
        w.Line("static readonly byte[][] _buffers = new byte[2][];");
        w.Line("static readonly WAVEHDR[] _headers = new WAVEHDR[2];");
        w.Line("static readonly GCHandle[] _pins = new GCHandle[2];");
        w.Line("static readonly bool[] _queued = new bool[2];");
        w.Line("static readonly float[] _phase = new float[3];");
        w.Blank();
        w.Line("public static volatile bool Enabled = true;");
        w.Blank();

        w.Open("public static void TryStart()");
        w.Open("try");
        w.Line("if (_open || !Enabled) return;");
        w.Line("WAVEFORMATEX format = new WAVEFORMATEX();");
        w.Line("format.wFormatTag = 1;");
        w.Line("format.nChannels = 1;");
        w.Line("format.nSamplesPerSec = SampleRate;");
        w.Line("format.wBitsPerSample = 8;");
        w.Line("format.nBlockAlign = 1;");
        w.Line("format.nAvgBytesPerSec = SampleRate;");
        w.Line("format.cbSize = 0;");
        w.Blank();
        w.Line("if (waveOutOpen(out _handle, -1, ref format, IntPtr.Zero, IntPtr.Zero, 0) != 0) return;");
        w.Line("_open = true;");
        w.Blank();
        w.Line("int headerSize = Marshal.SizeOf(typeof(WAVEHDR));");
        w.Open("for (int i = 0; i < 2; i++)");
        w.Line("_buffers[i] = new byte[BlockSize];");
        w.Line("_pins[i] = GCHandle.Alloc(_buffers[i], GCHandleType.Pinned);");
        w.Line("_headers[i] = new WAVEHDR();");
        w.Line("_headers[i].lpData = _pins[i].AddrOfPinnedObject();");
        w.Line("_headers[i].dwBufferLength = BlockSize;");
        w.Line("waveOutPrepareHeader(_handle, ref _headers[i], headerSize);");
        w.Close();
        w.Blank();
        w.Line("_run = true;");
        w.Line("_thread = new Thread(new ThreadStart(ThreadProc));");
        w.Line("_thread.IsBackground = true;");
        w.Line("_thread.Start();");
        w.Close();
        w.Line("catch { _open = false; }");
        w.Close();
        w.Blank();

        w.Open("public static void TryStop()");
        w.Open("try");
        w.Line("_run = false;");
        w.Open("if (_open)");
        w.Line("waveOutReset(_handle);");
        w.Line("int headerSize = Marshal.SizeOf(typeof(WAVEHDR));");
        w.Open("for (int i = 0; i < 2; i++)");
        w.Line("try { waveOutUnprepareHeader(_handle, ref _headers[i], headerSize); } catch { }");
        w.Line("if (_pins[i].IsAllocated) _pins[i].Free();");
        w.Close();
        w.Line("waveOutClose(_handle);");
        w.Line("_open = false;");
        w.Close();
        w.Close();
        w.Line("catch { }");
        w.Close();
        w.Blank();

        w.Open("static void ThreadProc()");
        w.Line("int headerSize;");
        w.Line("try { headerSize = Marshal.SizeOf(typeof(WAVEHDR)); } catch { headerSize = 32; }");
        w.Line("int index = 0;");
        w.Open("while (_run)");
        w.Open("try");
        w.Open("if (_queued[index])");
        w.Line("while (_run && (_headers[index].dwFlags & 1) == 0) Thread.Sleep(1);");
        w.Line("if (!_run) break;");
        w.Close();
        w.Line("Fill(_buffers[index]);");
        w.Line("if (waveOutWrite(_handle, ref _headers[index], headerSize) == 0) { _queued[index] = true; index = 1 - index; }");
        w.Line("else Thread.Sleep(2);");
        w.Close();
        w.Line("catch { Thread.Sleep(5); }");
        w.Close();
        w.Close();
        w.Blank();

        w.Open("static void Fill(byte[] buffer)");
        w.Open("for (int i = 0; i < buffer.Length; i++)");
        w.Line("int sample = 0;");
        w.Line("if (Runtime.BeeperLevel) sample += 26;");
        w.Blank();
        w.Open("for (int channel = 0; channel < 3; channel++)");
        w.Line("int period = Ayu.Regs[channel * 2] | ((Ayu.Regs[channel * 2 + 1] & 15) << 8);");
        w.Line("int volume = Ayu.Regs[8 + channel] & 15;");
        w.Line("if (volume == 0 || period < 8) continue;");
        w.Blank();
        w.Line("// AY tone: master clock / (16 * period).");
        w.Line("float frequency = 1773400f / (16f * period);");
        w.Line("_phase[channel] += frequency / SampleRate;");
        w.Line("if (_phase[channel] >= 1f) _phase[channel] -= 1f;");
        w.Line("if (_phase[channel] < 0.5f) sample += volume * 2;");
        w.Close();
        w.Blank();
        w.Line("int value = 128 + sample;");
        w.Line("if (value < 0) value = 0;");
        w.Line("if (value > 255) value = 255;");
        w.Line("buffer[i] = (byte)value;");
        w.Close();
        w.Close();
        w.Blank();

        w.Close();
        w.Blank();
    }
}
