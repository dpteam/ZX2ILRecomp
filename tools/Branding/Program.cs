// ZX2ILRecomp branding asset generator.
//
// Draws the project mark, logo, application icon and GitHub social card with
// GDI+ only, then writes:
//
//   assets/logo.png            horizontal logo (README hero)
//   assets/logo-mark.png       square mark, 512 px
//   assets/icon.png            square mark, 512 px (PNG copy of the icon)
//   assets/icon.ico            multi-resolution Windows icon
//   assets/social-preview.png  GitHub social preview card (1280x640)
//   assets/logo.svg            hand-maintained vector copy of the mark
//
// Run from the repository root:
//   dotnet run --project tools/Branding
//
// The palette is the ZX Spectrum's own: the four rainbow stripes and the
// black paper, so the icon reads as "Spectrum" before it reads as a word.

using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Runtime.InteropServices;

namespace ZX2ILRecomp.Branding;

internal static class Program
{
    private static readonly Color InkTop = Hex("#191C24");
    private static readonly Color InkBottom = Hex("#07080B");
    private static readonly Color Border = Hex("#2B3242");
    private static readonly Color Grid = Hex("#7F93B8");
    private static readonly Color Cream = Hex("#F4EFE2");
    private static readonly Color Red = Hex("#E8413C");
    private static readonly Color Yellow = Hex("#F2C11F");
    private static readonly Color Green = Hex("#35C759");
    private static readonly Color Cyan = Hex("#29B6F6");
    private static readonly Color Muted = Hex("#93A0B8");

    private static readonly Color[] Rainbow = { Red, Yellow, Green, Cyan };

    private static int Main()
    {
        string root = FindRepositoryRoot();
        string assets = Path.Combine(root, "assets");
        Directory.CreateDirectory(assets);

        SavePng(DrawMark(512), Path.Combine(assets, "logo-mark.png"));
        SavePng(DrawMark(512), Path.Combine(assets, "icon.png"));
        SavePng(DrawLogo(1240, 400), Path.Combine(assets, "logo.png"));
        SavePng(DrawSocial(1280, 640), Path.Combine(assets, "social-preview.png"));
        File.WriteAllText(Path.Combine(assets, "logo.svg"), BuildSvg());

        int[] iconSizes = { 16, 20, 24, 32, 40, 48, 64, 128, 256 };
        WriteIco(Path.Combine(assets, "icon.ico"), iconSizes);

        Console.WriteLine("Branding assets written to " + assets);
        return 0;
    }

    // -----------------------------------------------------------------
    // Mark
    // -----------------------------------------------------------------

    private static Bitmap DrawMark(int size)
    {
        Bitmap bitmap = new(size, size, PixelFormat.Format32bppArgb);

        using Graphics g = Graphics.FromImage(bitmap);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.CompositingQuality = CompositingQuality.HighQuality;

        float scale = size / 512f;
        g.ScaleTransform(scale, scale);

        DrawBadge(g);
        DrawRainbow(g);
        DrawMonogram(g);

        return bitmap;
    }

    private static void DrawBadge(Graphics g)
    {
        const float Size = 512f;

        using GraphicsPath path = RoundedRect(new RectangleF(8, 8, Size - 16, Size - 16), Size * 0.20f);
        using LinearGradientBrush background = new(
            new PointF(0, 0), new PointF(Size, Size), InkTop, InkBottom);

        g.FillPath(background, path);

        using Pen border = new(Border, Size * 0.012f);
        g.DrawPath(border, path);

        // Subtle 8-bit dot grid.
        using SolidBrush dot = new(Color.FromArgb(18, Grid));
        float step = Size / 16f;

        for (int y = 1; y < 16; y++)
        {
            for (int x = 1; x < 16; x++)
            {
                float px = x * step;
                float py = y * step;

                if (px < 56 || px > Size - 56 || py < 56 || py > Size - 56)
                {
                    continue;
                }

                g.FillRectangle(dot, px - 1.5f, py - 1.5f, 3f, 3f);
            }
        }

        // The badge is already a diagonal gradient with a dot grid; an extra
        // top highlight only added a visible seam where the gradient ended, so
        // there isn't one.
    }

    /// <summary>
    /// The four Spectrum stripes, drawn as one rotated bar and clipped to the
    /// badge so the mark keeps its rounded silhouette.
    /// </summary>
    private static void DrawRainbow(Graphics g)
    {
        const float Size = 512f;

        using GraphicsPath clip = RoundedRect(new RectangleF(8, 8, Size - 16, Size - 16), Size * 0.20f);
        GraphicsState state = g.Save();
        g.SetClip(clip, CombineMode.Intersect);

        GraphicsState rotated = g.Save();
        g.TranslateTransform(Size / 2f, Size / 2f);
        g.RotateTransform(-38f);
        g.TranslateTransform(-Size / 2f, -Size / 2f);

        const float StripeHeight = 34f;
        const float BandTop = 300f;
        const float BandLeft = -140f;
        const float BandWidth = Size + 280f;

        for (int i = 0; i < Rainbow.Length; i++)
        {
            float top = BandTop + i * StripeHeight;
            using SolidBrush brush = new(Rainbow[i]);
            g.FillRectangle(brush, BandLeft, top, BandWidth, StripeHeight);
        }

        g.Restore(rotated);
        g.Restore(state);
    }

    private static void DrawMonogram(Graphics g)
    {
        using FontFamily family = PickFamily();
        using Font font = new(family, 190f, FontStyle.Bold, GraphicsUnit.Pixel);

        StringFormat format = StringFormat.GenericTypographic;
        format.FormatFlags |= StringFormatFlags.NoWrap;

        const string Text = "ZX";
        SizeF measured = g.MeasureString(Text, font, PointF.Empty, format);

        float x = (512f - measured.Width) / 2f;
        float y = 78f;

        // A soft drop shadow keeps the monogram readable over the stripes.
        using SolidBrush shadow = new(Color.FromArgb(70, 0, 0, 0));
        g.DrawString(Text, font, shadow, new PointF(x + 4f, y + 5f), format);

        using SolidBrush cream = new(Cream);
        g.DrawString(Text, font, cream, new PointF(x, y), format);
    }

    // -----------------------------------------------------------------
    // Logo and social card
    // -----------------------------------------------------------------

    private static Bitmap DrawLogo(int width, int height)
    {
        Bitmap bitmap = new(width, height, PixelFormat.Format32bppArgb);

        using Graphics g = Graphics.FromImage(bitmap);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        g.Clear(Color.Transparent);

        int markSize = (int)(height * 0.88f);

        using (Bitmap mark = DrawMark(markSize))
        {
            g.DrawImage(mark, 8, (height - markSize) / 2);
        }

        float textLeft = markSize + 56;
        float scale = height / 400f;

        using FontFamily family = PickFamily();
        using Font subtitle = new(family, 34f * scale, FontStyle.Regular, GraphicsUnit.Pixel);

        StringFormat format = StringFormat.GenericTypographic;
        format.FormatFlags |= StringFormatFlags.NoWrap;

        const string First = "ZX2IL";
        const string Second = "Recomp";

        // Fit the title into the space the mark leaves, instead of assuming a
        // font size: the family changes per machine and a clipped wordmark is
        // worse than a smaller one.
        float available = width - textLeft - 24f;
        Font title = FitFont(g, family, First + Second, available, 140f * scale, format, out SizeF firstSize, out SizeF secondSize);

        float titleTop = height * 0.17f;

        using SolidBrush cream = new(Cream);
        using SolidBrush red = new(Red);

        using (title)
        {
            g.DrawString(First, title, cream, new PointF(textLeft, titleTop), format);
            g.DrawString(Second, title, red, new PointF(textLeft + firstSize.Width, titleTop), format);
        }

        float underlineY = titleTop + firstSize.Height * 1.12f;
        float underlineWidth = firstSize.Width + secondSize.Width;

        using Pen underline = new(Hex("#33405A"), Math.Max(2f, 4f * scale));
        g.DrawLine(underline, textLeft + 4, underlineY, textLeft + underlineWidth - 4, underlineY);

        using SolidBrush muted = new(Muted);
        const string Tagline = "STATIC Z80  \u2192  .NET RECOMPILER";
        g.DrawString(Tagline, subtitle, muted, new PointF(textLeft + 6, underlineY + 30 * scale), format);

        return bitmap;
    }

    /// <summary>
    /// Shrinks <paramref name="startSize"/> until the whole wordmark fits into
    /// <paramref name="available"/> pixels, and reports the two halves' widths.
    /// </summary>
    private static Font FitFont(
        Graphics g,
        FontFamily family,
        string combined,
        float available,
        float startSize,
        StringFormat format,
        out SizeF firstSize,
        out SizeF secondSize)
    {
        float size = startSize;

        while (true)
        {
            Font candidate = new(family, size, FontStyle.Bold, GraphicsUnit.Pixel);

            firstSize = g.MeasureString("ZX2IL", candidate, PointF.Empty, format);
            secondSize = g.MeasureString("Recomp", candidate, PointF.Empty, format);

            float total = firstSize.Width + secondSize.Width;

            if (total <= available || size <= 24f)
            {
                return candidate;
            }

            candidate.Dispose();
            size *= 0.94f;
        }
    }

    private static Bitmap DrawSocial(int width, int height)
    {
        Bitmap bitmap = new(width, height, PixelFormat.Format32bppArgb);

        using Graphics g = Graphics.FromImage(bitmap);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;

        using (LinearGradientBrush background = new(
            new PointF(0, 0), new PointF(width, height), InkTop, InkBottom))
        {
            g.FillRectangle(background, 0, 0, width, height);
        }

        using (SolidBrush dot = new(Color.FromArgb(16, Grid)))
        {
            for (int y = 24; y < height; y += 32)
            {
                for (int x = 24; x < width; x += 32)
                {
                    g.FillRectangle(dot, x, y, 3, 3);
                }
            }
        }

        const int MarkSize = 380;

        using (Bitmap mark = DrawMark(MarkSize))
        {
            g.DrawImage(mark, 92, (height - MarkSize) / 2);
        }

        using FontFamily family = PickFamily();
        using Font subtitle = new(family, 32f, FontStyle.Regular, GraphicsUnit.Pixel);
        using Font small = new(family, 25f, FontStyle.Regular, GraphicsUnit.Pixel);

        StringFormat format = StringFormat.GenericTypographic;
        format.FormatFlags |= StringFormatFlags.NoWrap;

        using SolidBrush cream = new(Cream);
        using SolidBrush red = new(Red);
        using SolidBrush muted = new(Muted);

        const float Left = 540f;
        float available = width - Left - 60f;

        using Font title = FitFont(g, family, "ZX2ILRecomp", available, 104f, format, out SizeF first, out SizeF second);

        g.DrawString("ZX2IL", title, cream, new PointF(Left, 168), format);
        g.DrawString("Recomp", title, red, new PointF(Left + first.Width, 168), format);

        float underlineWidth = first.Width + second.Width;

        using Pen underline = new(Hex("#33405A"), 4f);
        g.DrawLine(underline, Left + 4, 300, Left + underlineWidth - 4, 300);

        g.DrawString("Static Z80 \u2192 .NET recompiler", subtitle, muted, new PointF(Left + 4, 326), format);
        g.DrawString("Manic Miner in, readable C# out", small, muted, new PointF(Left + 4, 382), format);

        return bitmap;
    }

    private static FontFamily PickFamily()
    {
        string[] candidates =
        {
            "Segoe UI Variable Display",
            "Segoe UI",
            "Inter",
            "Arial",
            "Tahoma"
        };

        foreach (string name in candidates)
        {
            try
            {
                FontFamily family = new(name);

                if (family.IsStyleAvailable(FontStyle.Bold))
                {
                    return family;
                }

                family.Dispose();
            }
            catch
            {
                // Font is not installed, try the next one.
            }
        }

        return FontFamily.GenericSansSerif;
    }

    // -----------------------------------------------------------------
    // Encoding helpers
    // -----------------------------------------------------------------

    private static GraphicsPath RoundedRect(RectangleF rect, float radius)
    {
        float diameter = radius * 2;
        GraphicsPath path = new();
        path.AddArc(rect.Left, rect.Top, diameter, diameter, 180, 90);
        path.AddArc(rect.Right - diameter, rect.Top, diameter, diameter, 270, 90);
        path.AddArc(rect.Right - diameter, rect.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(rect.Left, rect.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        return path;
    }

    private static void SavePng(Bitmap bitmap, string path)
    {
        using (bitmap)
        {
            bitmap.Save(path, ImageFormat.Png);
        }

        Console.WriteLine("  " + Path.GetFileName(path));
    }

    private static void WriteIco(string path, int[] sizes)
    {
        List<(int Size, byte[] Data)> images = new();

        foreach (int size in sizes)
        {
            using Bitmap bitmap = DrawMark(size);
            images.Add((size, size >= 128 ? EncodePng(bitmap) : EncodeDib(bitmap)));
        }

        using FileStream stream = new(path, FileMode.Create, FileAccess.Write);
        using BinaryWriter writer = new(stream);

        writer.Write((ushort)0);            // reserved
        writer.Write((ushort)1);            // type: icon
        writer.Write((ushort)images.Count);

        int offset = 6 + 16 * images.Count;

        foreach ((int size, byte[] data) in images)
        {
            writer.Write((byte)(size >= 256 ? 0 : size));
            writer.Write((byte)(size >= 256 ? 0 : size));
            writer.Write((byte)0);          // palette size
            writer.Write((byte)0);          // reserved
            writer.Write((ushort)1);        // colour planes
            writer.Write((ushort)32);       // bits per pixel
            writer.Write(data.Length);
            writer.Write(offset);
            offset += data.Length;
        }

        foreach ((_, byte[] data) in images)
        {
            writer.Write(data);
        }

        Console.WriteLine("  " + Path.GetFileName(path));
    }

    private static byte[] EncodePng(Bitmap bitmap)
    {
        using MemoryStream memory = new();
        bitmap.Save(memory, ImageFormat.Png);
        return memory.ToArray();
    }

    private static byte[] EncodeDib(Bitmap bitmap)
    {
        int width = bitmap.Width;
        int height = bitmap.Height;
        int xorSize = width * height * 4;
        int andStride = (width + 31) / 32 * 4;
        int andSize = andStride * height;

        using MemoryStream memory = new();
        using BinaryWriter writer = new(memory);

        writer.Write(40);                     // biSize
        writer.Write(width);                  // biWidth
        writer.Write(height * 2);             // biHeight (XOR + AND)
        writer.Write((ushort)1);              // biPlanes
        writer.Write((ushort)32);             // biBitCount
        writer.Write(0);                      // biCompression (BI_RGB)
        writer.Write(xorSize + andSize);      // biSizeImage
        writer.Write(0);                      // biXPelsPerMeter
        writer.Write(0);                      // biYPelsPerMeter
        writer.Write(0);                      // biClrUsed
        writer.Write(0);                      // biClrImportant

        BitmapData data = bitmap.LockBits(
            new Rectangle(0, 0, width, height),
            ImageLockMode.ReadOnly,
            PixelFormat.Format32bppArgb);

        try
        {
            byte[] row = new byte[width * 4];

            for (int y = height - 1; y >= 0; y--)
            {
                Marshal.Copy(IntPtr.Add(data.Scan0, y * data.Stride), row, 0, row.Length);
                writer.Write(row);
            }
        }
        finally
        {
            bitmap.UnlockBits(data);
        }

        writer.Write(new byte[andSize]);
        writer.Flush();

        return memory.ToArray();
    }

    /// <summary>
    /// Hand-written SVG twin of the raster mark, so the README hero stays sharp
    /// at any width. Kept in sync with the geometry above by hand.
    /// </summary>
    private static string BuildSvg()
    {
        return """
            <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 512 512" width="512" height="512" role="img" aria-label="ZX2ILRecomp mark">
              <defs>
                <linearGradient id="bg" x1="0" y1="0" x2="1" y2="1">
                  <stop offset="0" stop-color="#191C24"/>
                  <stop offset="1" stop-color="#07080B"/>
                </linearGradient>
                <clipPath id="badge">
                  <rect x="8" y="8" width="496" height="496" rx="102"/>
                </clipPath>
              </defs>
              <rect x="8" y="8" width="496" height="496" rx="102" fill="url(#bg)" stroke="#2B3242" stroke-width="6"/>
              <g clip-path="url(#badge)">
                <g transform="rotate(-38 256 256)">
                  <rect x="-140" y="300" width="792" height="34" fill="#E8413C"/>
                  <rect x="-140" y="334" width="792" height="34" fill="#F2C11F"/>
                  <rect x="-140" y="368" width="792" height="34" fill="#35C759"/>
                  <rect x="-140" y="402" width="792" height="34" fill="#29B6F6"/>
                </g>
              </g>
              <text x="256" y="270" text-anchor="middle" font-family="Segoe UI, Arial, sans-serif" font-size="190" font-weight="700" fill="#F4EFE2">ZX</text>
            </svg>

            """;
    }

    private static Color Hex(string value) => ColorTranslator.FromHtml(value);

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);

        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "ZX2ILRecomp.csproj")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        return Directory.GetCurrentDirectory();
    }
}
