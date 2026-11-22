using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Runtime.InteropServices;

namespace LiteBrowser;

static class Theme
{
    // Цвета меняются при смене темы (ApplyPalette), поэтому они не readonly
    public static Color Frame = Color.FromArgb(22, 23, 28);      // полоса вкладок и заголовок окна
    public static Color Toolbar = Color.FromArgb(36, 38, 46);    // активная вкладка и панель
    public static Color Field = Color.FromArgb(24, 25, 31);      // адресная строка
    public static Color TabHover = Color.FromArgb(32, 34, 41);
    public static Color Hover = Color.FromArgb(56, 59, 70);
    public static Color Pressed = Color.FromArgb(70, 74, 88);
    public static Color Line = Color.FromArgb(48, 50, 60);
    public static Color Text = Color.FromArgb(232, 234, 242);
    public static Color TextDim = Color.FromArgb(148, 153, 170);
    public static Color Accent = Color.FromArgb(124, 156, 255);
    public static Color Accent2 = Color.FromArgb(176, 124, 255);
    public static Color Menu = Color.FromArgb(32, 34, 41);
    public static Color Secure = Color.FromArgb(110, 207, 150);
    public static Color Danger = Color.FromArgb(255, 120, 120);
    public static bool IsLight { get; private set; }
    public const string IconFont = "Segoe Fluent Icons";

    /// <summary>Цвета акцента: основной и второй (для градиентов).</summary>
    public static readonly Dictionary<string, (Color a, Color b, string name)> Accents = new()
    {
        ["blue"] = (Color.FromArgb(124, 156, 255), Color.FromArgb(176, 124, 255), "Синий"),
        ["purple"] = (Color.FromArgb(176, 124, 255), Color.FromArgb(255, 111, 181), "Фиолетовый"),
        ["green"] = (Color.FromArgb(79, 209, 139), Color.FromArgb(63, 193, 201), "Зелёный"),
        ["orange"] = (Color.FromArgb(255, 159, 74), Color.FromArgb(255, 107, 107), "Оранжевый"),
        ["pink"] = (Color.FromArgb(255, 111, 181), Color.FromArgb(176, 124, 255), "Розовый"),
        ["red"] = (Color.FromArgb(255, 107, 107), Color.FromArgb(255, 159, 74), "Красный"),
        ["system"] = SystemAccent(),
    };

    /// <summary>Цвет акцента Windows (Параметры → Персонализация → Цвета).</summary>
    static (Color, Color, string) SystemAccent()
    {
        var c = Color.FromArgb(0, 120, 215);
        try
        {
            using var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\DWM");
            if (k?.GetValue("AccentColor") is int v) c = Color.FromArgb(v & 0xFF, (v >> 8) & 0xFF, (v >> 16) & 0xFF); // в реестре ABGR
        }
        catch { }
        // Слишком тёмный акцент на тёмном фоне не виден — осветляем
        if (c.GetBrightness() < 0.45f) c = Color.FromArgb((c.R + 255) / 2, (c.G + 255) / 2, (c.B + 255) / 2);
        var b = Color.FromArgb(Math.Min(255, c.R + 50), Math.Max(0, c.G - 20), Math.Min(255, c.B + 40));
        return (c, b, "Как в Windows");
    }

    /// <summary>Цветовые схемы помимо стандартных тёмной и светлой.</summary>
    public static readonly string[] ExtraThemes = { "black", "nord", "forest", "sepia" };
    static string pageBg = "";

    static Color Darken(Color c, float k) => Color.FromArgb((int)(c.R * (1 - k)), (int)(c.G * (1 - k)), (int)(c.B * (1 - k)));

    public static bool WindowsUsesLight()
    {
        try
        {
            using var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return k?.GetValue("AppsUseLightTheme") is int v && v == 1;
        }
        catch { return false; }
    }

    /// <summary>Переключить палитру: тёмная/светлая/как в Windows + цвет акцента.</summary>
    public static void ApplyPalette(string ui, string accent)
    {
        IsLight = ui == "light" || ui == "sepia" || (ui == "auto" && WindowsUsesLight());
        pageBg = "";
        var (a, b, _) = Accents.TryGetValue(accent, out var x) ? x : Accents["blue"];
        if (IsLight)
        {
            Frame = Color.FromArgb(222, 225, 233); Toolbar = Color.FromArgb(248, 249, 252); Field = Color.FromArgb(236, 238, 244);
            TabHover = Color.FromArgb(212, 215, 225); Hover = Color.FromArgb(222, 225, 234); Pressed = Color.FromArgb(205, 209, 220);
            Line = Color.FromArgb(205, 208, 218); Text = Color.FromArgb(28, 30, 36); TextDim = Color.FromArgb(96, 101, 116);
            Menu = Color.FromArgb(250, 250, 253);
            Accent = Darken(a, 0.22f); Accent2 = Darken(b, 0.22f);
            Secure = Color.FromArgb(30, 150, 80); Danger = Color.FromArgb(210, 50, 50);
            if (ui == "sepia")
            {
                // Тёплая: бумажные тона, мягче для глаз вечером
                Frame = Color.FromArgb(228, 218, 198); Toolbar = Color.FromArgb(246, 240, 227); Field = Color.FromArgb(236, 228, 212);
                TabHover = Color.FromArgb(218, 207, 186); Hover = Color.FromArgb(230, 220, 200); Pressed = Color.FromArgb(214, 202, 180);
                Line = Color.FromArgb(214, 202, 180); Text = Color.FromArgb(54, 44, 32); TextDim = Color.FromArgb(120, 104, 84);
                Menu = Color.FromArgb(250, 245, 235);
                pageBg = ":root{--bg:#f4ecdc;--card:rgba(255,252,245,.75);--text:#362c20;--dim:#78684f;--line:rgba(80,60,30,.14)}body{background:#f4ecdc}";
            }
        }
        else
        {
            Frame = Color.FromArgb(22, 23, 28); Toolbar = Color.FromArgb(36, 38, 46); Field = Color.FromArgb(24, 25, 31);
            TabHover = Color.FromArgb(32, 34, 41); Hover = Color.FromArgb(56, 59, 70); Pressed = Color.FromArgb(70, 74, 88);
            Line = Color.FromArgb(48, 50, 60); Text = Color.FromArgb(232, 234, 242); TextDim = Color.FromArgb(148, 153, 170);
            Menu = Color.FromArgb(32, 34, 41);
            Accent = a; Accent2 = b;
            Secure = Color.FromArgb(110, 207, 150); Danger = Color.FromArgb(255, 120, 120);
            switch (ui)
            {
                case "black": // чёрная — для OLED-экранов
                    Frame = Color.FromArgb(0, 0, 0); Toolbar = Color.FromArgb(16, 16, 18); Field = Color.FromArgb(8, 8, 10);
                    TabHover = Color.FromArgb(12, 12, 14); Hover = Color.FromArgb(34, 34, 38); Pressed = Color.FromArgb(48, 48, 54);
                    Line = Color.FromArgb(30, 30, 34); Menu = Color.FromArgb(14, 14, 16);
                    pageBg = ":root{--bg:#000;--card:rgba(255,255,255,.05)}";
                    break;
                case "nord": // полночь — холодные сине-серые тона
                    Frame = Color.FromArgb(36, 41, 51); Toolbar = Color.FromArgb(46, 52, 64); Field = Color.FromArgb(38, 43, 54);
                    TabHover = Color.FromArgb(42, 48, 59); Hover = Color.FromArgb(59, 66, 82); Pressed = Color.FromArgb(67, 76, 94);
                    Line = Color.FromArgb(59, 66, 82); Menu = Color.FromArgb(46, 52, 64);
                    Text = Color.FromArgb(236, 239, 244); TextDim = Color.FromArgb(160, 170, 190);
                    pageBg = ":root{--bg:#242933;--card:rgba(236,239,244,.05);--text:#eceff4;--dim:#a0aabe}";
                    break;
                case "forest": // лес — тёмно-зелёная
                    Frame = Color.FromArgb(18, 26, 22); Toolbar = Color.FromArgb(28, 40, 34); Field = Color.FromArgb(20, 30, 25);
                    TabHover = Color.FromArgb(24, 35, 29); Hover = Color.FromArgb(44, 60, 52); Pressed = Color.FromArgb(56, 76, 66);
                    Line = Color.FromArgb(40, 56, 48); Menu = Color.FromArgb(26, 38, 32);
                    Text = Color.FromArgb(228, 238, 232); TextDim = Color.FromArgb(146, 166, 155);
                    pageBg = ":root{--bg:#121a16;--card:rgba(220,255,235,.045);--text:#e4eee8;--dim:#92a69b}";
                    break;
            }
        }
    }

    /// <summary>CSS-переменные для своих страниц: цвет акцента и светлая тема.</summary>
    public static string PageCss()
    {
        static string H(Color c) => $"#{c.R:x2}{c.G:x2}{c.B:x2}";
        static string Rgb(Color c) => $"{c.R},{c.G},{c.B}";
        var css = $"\n:root{{--accent:{H(Accent)};--accent2:{H(Accent2)};--accent-rgb:{Rgb(Accent)}}}\n";
        if (IsLight)
            css += ":root{--bg:#f3f4f8;--card:rgba(255,255,255,.78);--card2:rgba(0,0,0,.05);--line:rgba(0,0,0,.1);--text:#1c1e24;--dim:#5f6476;--ok:#1e9650;--bad:#d23232;--warn:#c27012}\n" +
                   ".glow{opacity:.45}.dialog{background:#fff!important}.toast{background:#fff!important;color:#1c1e24}" +
                   "input.text,textarea.text{background:rgba(0,0,0,.035)!important}.slider{background:#c9ccd6}" +
                   ".time{background:linear-gradient(180deg,#1c1e24,#4a5070)!important;-webkit-background-clip:text!important}" +
                   ".search{background:rgba(255,255,255,.8)!important}.engine,.chip,.store,.seg,.filters{background:rgba(0,0,0,.04)}\n";
        return css + pageBg;
    }

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

    /// <summary>Заголовок окна в цвет полосы вкладок (Windows 11).</summary>
    public static void StyleTitleBar(IntPtr hwnd, Color frame)
    {
        bool darkFrame = frame.GetBrightness() < 0.5f;
        int on = darkFrame ? 1 : 0, caption = ColorTranslator.ToWin32(frame), text = ColorTranslator.ToWin32(TitleText(frame));
        DwmSetWindowAttribute(hwnd, 20, ref on, 4);       // тёмный режим
        DwmSetWindowAttribute(hwnd, 34, ref caption, 4);  // цвет рамки
        DwmSetWindowAttribute(hwnd, 35, ref caption, 4);  // цвет заголовка
        DwmSetWindowAttribute(hwnd, 36, ref text, 4);     // цвет текста заголовка
    }

    /// <summary>Цвет текста заголовка окна: тёмный на светлой рамке.</summary>
    static Color TitleText(Color frame) => frame.GetBrightness() < 0.5f ? Color.FromArgb(232, 234, 242) : Color.FromArgb(28, 30, 36);

    /// <summary>Скруглённые углы и тёмная рамка для всплывающих окон.</summary>
    public static void StylePopup(IntPtr hwnd)
    {
        int round = 2, on = IsLight ? 0 : 1, border = ColorTranslator.ToWin32(Line);
        DwmSetWindowAttribute(hwnd, 20, ref on, 4);
        DwmSetWindowAttribute(hwnd, 33, ref round, 4);
        DwmSetWindowAttribute(hwnd, 34, ref border, 4);
    }

    static readonly List<ContextMenuStrip> openMenus = new();

    public static ContextMenuStrip NewMenu()
    {
        var m = new ContextMenuStrip
        {
            Renderer = new DarkMenuRenderer(),
            ShowImageMargin = false,
            Font = GetFont("Segoe UI", 9.5f),
            BackColor = Menu,
            ForeColor = Text,
            Padding = new Padding(4),
        };
        m.Opened += (_, _) => { if (!openMenus.Contains(m)) openMenus.Add(m); };
        m.Closed += (_, _) => openMenus.Remove(m);
        return m;
    }

    /// <summary>Закрыть открытые меню. Клик внутри страницы (WebView2 — другой процесс) сам их не закрывает.</summary>
    public static void CloseMenus()
    {
        foreach (var m in openMenus.ToList())
            try { m.Close(ToolStripDropDownCloseReason.AppClicked); } catch { }
        openMenus.Clear();
    }

    static readonly Dictionary<(string, int), Bitmap> glyphCache = new();

    /// <summary>Значок из шрифта Segoe Fluent Icons как картинка для пункта меню.</summary>
    public static Bitmap Glyph(string glyph, Color? color = null)
    {
        var c = color ?? TextDim;
        var key = (glyph, c.ToArgb() ^ S(16));
        if (glyphCache.TryGetValue(key, out var b)) return b;
        int size = S(16);
        b = new Bitmap(size, size);
        using (var g = Graphics.FromImage(b))
        {
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
            TextRenderer.DrawText(g, glyph, GetFont(IconFont, 10.5f), new Rectangle(0, 0, size, size), c, Center | TextFormatFlags.NoPadding);
        }
        glyphCache[key] = b;
        return b;
    }

    public static ToolStripMenuItem Item(string text, Action click, string? keys = null, string? glyph = null)
    {
        var item = new ToolStripMenuItem(L.T(text)) { ForeColor = Text, Padding = new Padding(4, 4, 6, 4) };
        if (glyph != null) item.Image = Glyph(glyph);
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

    // Поле под значками — того же цвета, что и меню
    protected override void OnRenderImageMargin(ToolStripRenderEventArgs e) { }

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

    InputDialog(string title, string value, bool password = false)
    {
        box.UseSystemPasswordChar = password;
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

    public static string? AskPassword(IWin32Window owner, string title)
    {
        using var d = new InputDialog(title, "", password: true);
        return d.ShowDialog(owner) == DialogResult.OK ? d.box.Text : null;
    }
}

/// <summary>Панель без мерцания.</summary>
sealed class BufferedPanel : Panel
{
    public BufferedPanel() { DoubleBuffered = true; ResizeRedraw = true; }
}
