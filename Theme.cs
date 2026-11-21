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

    /// <summary>Масштаб экрана Windows.</summary>
    public static float SystemDpi = 1f;
    /// <summary>Размер интерфейса браузера из настроек (0.8 – 1.25).</summary>
    public static float UiScale = 1f;
    public static float Dpi => SystemDpi * UiScale;
    public static int S(float v) => (int)Math.Round(v * Dpi);

    static readonly Dictionary<(string, float, FontStyle), Font> fonts = new();

    /// <summary>Шрифт с учётом размера интерфейса (кэшируется).</summary>
    public static Font GetFont(string family, float points, FontStyle style = FontStyle.Regular)
    {
        var key = (family, (float)Math.Round(points * UiScale, 2), style);
        if (!fonts.TryGetValue(key, out var f)) fonts[key] = f = new Font(family, key.Item2, style);
        return f;
    }

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

    public static readonly Color IncognitoFrame = Color.FromArgb(34, 24, 50);
    public static readonly Color Menu = Color.FromArgb(32, 34, 41);

    /// <summary>Тёмный заголовок окна в цвет полосы вкладок (Windows 11).</summary>
    public static void StyleTitleBar(IntPtr hwnd, Color frame)
    {
        int on = 1, caption = ColorTranslator.ToWin32(frame), text = ColorTranslator.ToWin32(Text);
        DwmSetWindowAttribute(hwnd, 20, ref on, 4);       // тёмный режим
        DwmSetWindowAttribute(hwnd, 34, ref caption, 4);  // цвет рамки
        DwmSetWindowAttribute(hwnd, 35, ref caption, 4);  // цвет заголовка
        DwmSetWindowAttribute(hwnd, 36, ref text, 4);     // цвет текста заголовка
    }

    /// <summary>Скруглённые углы и тёмная рамка для всплывающих окон.</summary>
    public static void StylePopup(IntPtr hwnd)
    {
        int round = 2, on = 1, border = ColorTranslator.ToWin32(Line);
        DwmSetWindowAttribute(hwnd, 20, ref on, 4);
        DwmSetWindowAttribute(hwnd, 33, ref round, 4);
        DwmSetWindowAttribute(hwnd, 34, ref border, 4);
    }

    public static ContextMenuStrip NewMenu()
    {
        return new ContextMenuStrip
        {
            Renderer = new DarkMenuRenderer(),
            ShowImageMargin = false,
            Font = GetFont("Segoe UI", 9.5f),
            BackColor = Menu,
            ForeColor = Text,
            Padding = new Padding(4),
        };
    }

    public static ToolStripMenuItem Item(string text, Action click, string? keys = null)
    {
        var item = new ToolStripMenuItem(text) { ForeColor = Text, Padding = new Padding(6, 5, 6, 5) };
        if (keys != null) item.ShortcutKeyDisplayString = keys;
        item.Click += (_, _) => click();
        return item;
    }

    public static string FormatSize(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} Б",
        < 1024 * 1024 => $"{bytes / 1024.0:0} КБ",
        < 1024L * 1024 * 1024 => $"{bytes / 1048576.0:0.0} МБ",
        _ => $"{bytes / 1073741824.0:0.00} ГБ",
    };

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

sealed class DarkColors : ProfessionalColorTable
{
    public override Color ToolStripDropDownBackground => Theme.Menu;
    public override Color MenuBorder => Theme.Line;
    public override Color MenuItemBorder => Theme.Hover;
    public override Color MenuItemSelected => Theme.Hover;
    public override Color MenuItemSelectedGradientBegin => Theme.Hover;
    public override Color MenuItemSelectedGradientEnd => Theme.Hover;
    public override Color MenuItemPressedGradientBegin => Theme.Pressed;
    public override Color MenuItemPressedGradientEnd => Theme.Pressed;
    public override Color ImageMarginGradientBegin => Theme.Menu;
    public override Color ImageMarginGradientMiddle => Theme.Menu;
    public override Color ImageMarginGradientEnd => Theme.Menu;
    public override Color SeparatorDark => Theme.Line;
    public override Color SeparatorLight => Theme.Line;
}

sealed class DarkMenuRenderer : ToolStripProfessionalRenderer
{
    public DarkMenuRenderer() : base(new DarkColors()) { RoundedEdges = false; }

    protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
    {
        e.TextColor = e.Item.Enabled ? Theme.Text : Theme.TextDim;
        base.OnRenderItemText(e);
    }

    protected override void OnRenderArrow(ToolStripArrowRenderEventArgs e)
    {
        e.ArrowColor = Theme.Text;
        base.OnRenderArrow(e);
    }
}

/// <summary>Тёмное окно ввода текста.</summary>
sealed class InputDialog : Form
{
    readonly TextBox box = new() { BorderStyle = BorderStyle.FixedSingle };

    InputDialog(string title, string value)
    {
        Text = title;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        BackColor = Theme.Toolbar;
        ForeColor = Theme.Text;
        Font = new Font("Segoe UI", 10f);
        ClientSize = new Size(Theme.S(380), Theme.S(110));
        box.Text = value;
        box.BackColor = Theme.Field;
        box.ForeColor = Theme.Text;
        box.SetBounds(Theme.S(16), Theme.S(18), Theme.S(348), Theme.S(28));
        var ok = new Button { Text = "Сохранить", DialogResult = DialogResult.OK, FlatStyle = FlatStyle.Flat, BackColor = Theme.Accent, ForeColor = Color.FromArgb(13, 16, 32) };
        var cancel = new Button { Text = "Отмена", DialogResult = DialogResult.Cancel, FlatStyle = FlatStyle.Flat, BackColor = Theme.Hover, ForeColor = Theme.Text };
        ok.FlatAppearance.BorderSize = cancel.FlatAppearance.BorderSize = 0;
        ok.SetBounds(Theme.S(160), Theme.S(64), Theme.S(110), Theme.S(32));
        cancel.SetBounds(Theme.S(278), Theme.S(64), Theme.S(86), Theme.S(32));
        Controls.AddRange(new Control[] { box, ok, cancel });
        AcceptButton = ok;
        CancelButton = cancel;
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        try { Theme.StyleTitleBar(Handle, Theme.Toolbar); } catch { }
    }

    public static string? Ask(IWin32Window owner, string title, string value)
    {
        using var d = new InputDialog(title, value);
        return d.ShowDialog(owner) == DialogResult.OK ? d.box.Text.Trim() : null;
    }
}

/// <summary>Панель без мерцания.</summary>
sealed class BufferedPanel : Panel
{
    public BufferedPanel() { DoubleBuffered = true; ResizeRedraw = true; }
}
