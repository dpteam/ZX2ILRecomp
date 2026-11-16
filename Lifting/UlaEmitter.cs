using System.Globalization;

namespace ZX2ILRecomp.Lifting;

/// <summary>
/// Emits the ULA: screen decoding, palette and the 320x240 RGBA frame buffer
/// that the form blits.
///
/// The buffer is 320x240 so the 256x192 picture sits inside a 32/24 pixel
/// border, which is what the real machine shows and what makes a border-colour
/// effect visible instead of clipped.
/// </summary>
internal static class UlaEmitter
{
    public const int Width = 320;
    public const int Height = 240;
    public const int ScreenWidth = 256;
    public const int ScreenHeight = 192;
    public const int OffsetX = 32;
    public const int OffsetY = 24;

    public static void Emit(EmitWriter w, LifterContext ctx)
    {
        w.Open("static class Ula");
        w.Line("public const int Width = " + Width.ToString(CultureInfo.InvariantCulture) + ";");
        w.Line("public const int Height = " + Height.ToString(CultureInfo.InvariantCulture) + ";");
        w.Line("public const int ScreenWidth = " + ScreenWidth.ToString(CultureInfo.InvariantCulture) + ";");
        w.Line("public const int ScreenHeight = " + ScreenHeight.ToString(CultureInfo.InvariantCulture) + ";");
        w.Line("public const int OffsetX = " + OffsetX.ToString(CultureInfo.InvariantCulture) + ";");
        w.Line("public const int OffsetY = " + OffsetY.ToString(CultureInfo.InvariantCulture) + ";");
        w.Blank();
        w.Line("public static int FrameCount;");
        w.Line("public static byte[] Screen = new byte[Width * Height * 4];");
        w.Blank();

        w.Line("// Classic Spectrum palette: 8 normal then 8 bright colours.");
        w.Line("public static readonly int[] Palette = new int[16]");
        w.Line("{");
        w.Line("    0x000000, 0x0000D7, 0xD70000, 0xD700D7, 0x00D700, 0x00D7D7, 0xD7D700, 0xD7D7D7,");
        w.Line("    0x000000, 0x0000FF, 0xFF0000, 0xFF00FF, 0x00FF00, 0x00FFFF, 0xFFFF00, 0xFFFFFF");
        w.Line("};");
        w.Blank();

        w.Open("public static void Reset()");
        w.Line("FrameCount = 0;");
        w.Line("Array.Clear(Screen, 0, Screen.Length);");
        w.Close();
        w.Blank();

        w.Open("public static void RenderToBitmap(Bitmap target)");
        w.Open("lock (Runtime.FrameLock)");
        w.Line("RenderScreen();");
        w.Line("Rectangle bounds = new Rectangle(0, 0, Width, Height);");
        w.Line("BitmapData data = target.LockBits(bounds, ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);");
        w.Open("if (data.Stride == Width * 4)");
        w.Line("Marshal.Copy(Screen, 0, data.Scan0, Screen.Length);");
        w.Close();
        w.Open("else");
        w.Open("for (int y = 0; y < Height; y++)");
        w.Line("IntPtr destination = new IntPtr(data.Scan0.ToInt64() + (long)y * data.Stride);");
        w.Line("Marshal.Copy(Screen, y * Width * 4, destination, Width * 4);");
        w.Close();
        w.Close();
        w.Line("target.UnlockBits(data);");
        w.Close();
        w.Close();
        w.Blank();

        w.Open("static void RenderScreen()");
        w.Line("Fill(Runtime.Border & 7);");
        w.Blank();
        w.Line("// On a 128K the second screen lives in bank 7 when port 0x7FFD bit 3 is set.");
        w.Line("int screenBank = (Runtime.Model >= 128 && (Runtime.Port7FFD & 8) != 0) ? 7 : 5;");
        w.Line("byte[] bank = Runtime.RamBanks[screenBank];");
        w.Line("bool flashOn = (FrameCount & 16) != 0;");
        w.Blank();
        w.Open("for (int y = 0; y < ScreenHeight; y++)");
        w.Open("for (int x = 0; x < ScreenWidth; x++)");
        w.Line("// The Spectrum bitmap is stored in three interleaved 64-line thirds.");
        w.Line("int address = ((y & 0xC0) << 5) + ((y & 0x07) << 8) + ((y & 0x38) << 2) + (x >> 3);");
        w.Line("byte pixels = bank[address & 0x3FFF];");
        w.Line("byte attr = bank[(0x1800 + (y >> 3) * 32 + (x >> 3)) & 0x3FFF];");
        w.Blank();
        w.Line("int bit = 7 - (x & 7);");
        w.Line("bool pixelSet = ((pixels >> bit) & 1) != 0;");
        w.Line("int ink = attr & 7;");
        w.Line("int paper = (attr >> 3) & 7;");
        w.Line("if ((attr & 0x40) != 0) { ink += 8; paper += 8; }");
        w.Line("if ((attr & 0x80) != 0 && flashOn) { int swap = ink; ink = paper; paper = swap; }");
        w.Line("SetPixel(OffsetX + x, OffsetY + y, pixelSet ? ink : paper);");
        w.Close();
        w.Close();
        w.Close();
        w.Blank();

        w.Open("static void Fill(int colorIndex)");
        w.Line("int rgb = Palette[colorIndex & 15];");
        w.Line("byte b = (byte)(rgb & 0xFF);");
        w.Line("byte g = (byte)((rgb >> 8) & 0xFF);");
        w.Line("byte r = (byte)((rgb >> 16) & 0xFF);");
        w.Open("for (int i = 0; i < Screen.Length; i += 4)");
        w.Line("Screen[i + 0] = b;");
        w.Line("Screen[i + 1] = g;");
        w.Line("Screen[i + 2] = r;");
        w.Line("Screen[i + 3] = 0xFF;");
        w.Close();
        w.Close();
        w.Blank();

        w.Open("static void SetPixel(int x, int y, int colorIndex)");
        w.Line("if ((uint)x >= Width || (uint)y >= Height) return;");
        w.Line("int rgb = Palette[colorIndex & 15];");
        w.Line("int offset = (y * Width + x) * 4;");
        w.Line("Screen[offset + 0] = (byte)(rgb & 0xFF);");
        w.Line("Screen[offset + 1] = (byte)((rgb >> 8) & 0xFF);");
        w.Line("Screen[offset + 2] = (byte)((rgb >> 16) & 0xFF);");
        w.Line("Screen[offset + 3] = 0xFF;");
        w.Close();
        w.Blank();

        w.Close();
        w.Blank();
    }
}
