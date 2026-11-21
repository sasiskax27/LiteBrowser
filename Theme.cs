using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Runtime.InteropServices;

namespace LiteBrowser;

static class Theme
{
    public static readonly Color Frame = Color.FromArgb(22, 23, 28);      // полоса вкладок и заголовок окна
    public static readonly Color Toolbar = Color.FromArgb(36, 38, 46);    // активная вкладка и панель
    public static readonly Color Field = Color.FromArgb(24, 25, 31);      // адресная строка
    public static readonly Color TabHover = Color.FromArgb(32, 34, 41);
    public static readonly Color Hover = Color.FromArgb(56, 59, 70);
    public static readonly Color Pressed = Color.FromArgb(70, 74, 88);
    public static readonly Color Line = Color.FromArgb(48, 50, 60);
    public static readonly Color Text = Color.FromArgb(232, 234, 242);
    public static readonly Color TextDim = Color.FromArgb(148, 153, 170);
    public static readonly Color Accent = Color.FromArgb(124, 156, 255);
    public static readonly Color Accent2 = Color.FromArgb(176, 124, 255);
    public static readonly Color Secure = Color.FromArgb(110, 207, 150);
    public static readonly Color Danger = Color.FromArgb(255, 120, 120);
    public const string IconFont = "Segoe Fluent Icons";

    public static float Dpi = 1f;
    public static int S(float v) => (int)Math.Round(v * Dpi);

    public static GraphicsPath Round(RectangleF r, float rad)
    {
        var p = new GraphicsPath();
        float d = rad * 2;
        if (d <= 0) { p.AddRectangle(r); return p; }
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    /// <summary>Форма вкладки: скруглённый верх и плавные «крылья» снизу, перетекающие в панель.</summary>
    public static GraphicsPath TabShape(Rectangle r, float rad)
    {
        var p = new GraphicsPath();
        float d = rad * 2, b = r.Bottom;
        p.AddArc(r.X - d, b - d, d, d, 90, -90);
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right, b - d, d, d, 180, -90);
        p.CloseFigure();
        return p;
    }

    [DllImport("dwmapi.dll")]
    static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    /// <summary>Тёмный заголовок окна в цвет полосы вкладок (Windows 11).</summary>
    public static void StyleTitleBar(IntPtr hwnd)
    {
        int on = 1, caption = ColorTranslator.ToWin32(Frame), text = ColorTranslator.ToWin32(Text);
        DwmSetWindowAttribute(hwnd, 20, ref on, 4);       // тёмный режим
        DwmSetWindowAttribute(hwnd, 34, ref caption, 4);  // цвет рамки
        DwmSetWindowAttribute(hwnd, 35, ref caption, 4);  // цвет заголовка
        DwmSetWindowAttribute(hwnd, 36, ref text, 4);     // цвет текста заголовка
    }

    public static Icon MakeAppIcon()
    {
        using var bmp = new Bitmap(64, 64);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            using var br = new LinearGradientBrush(new Rectangle(0, 0, 64, 64), Accent, Accent2, 45f);
            g.FillEllipse(br, 2, 2, 60, 60);
            using var f = new Font("Segoe UI", 34, FontStyle.Bold, GraphicsUnit.Pixel);
            using var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
            g.DrawString("L", f, Brushes.White, new RectangleF(0, 1, 64, 64), sf);
        }
        return Icon.FromHandle(bmp.GetHicon());
    }

    public const TextFormatFlags Center =
        TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.SingleLine;
}

/// <summary>Панель без мерцания.</summary>
sealed class BufferedPanel : Panel
{
    public BufferedPanel() { DoubleBuffered = true; ResizeRedraw = true; }
}
