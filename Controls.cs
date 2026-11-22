using System.Drawing.Drawing2D;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace LiteBrowser;

/// <summary>Одна вкладка браузера.</summary>
sealed class BrowserTab
{
    public required WebView2 View { get; init; }
    public CoreWebView2? Core => View.CoreWebView2;
    public Task? InitTask { get; set; }
    public string? PendingUrl { get; set; }   // вкладка ещё не загружена (ленивая загрузка)
    public string Title { get; set; } = "Новая вкладка";
    public Image? Icon { get; set; }
    public bool Loading { get; set; }
    public bool IsHome { get; set; }
    public int Blocked { get; set; }
    public bool Audio { get; set; }
    public bool Muted { get; set; }
    public bool Frozen { get; set; }
    public DateTime LastActive { get; set; } = DateTime.Now;
    public string FilterMode { get; set; } = "none";
    public HistoryEntry? HistoryEntry { get; set; }
    public bool HasLoginForm { get; set; }
    public bool Pinned { get; set; }
    public TabGroup? Group { get; set; }
    public string PageHost { get; set; } = "";
    public Dictionary<string, int> BlockedHosts { get; } = new();
    public int PopupsBlocked { get; set; }
    public string? AdScriptId { get; set; }
    public string? YtScriptId { get; set; }
    public string? CookieScriptId { get; set; }
    public int Volume { get; set; } = 100;   // громкость вкладки, %
    public string AdSig { get; set; } = "";
    public (string origin, string user)? LastUser { get; set; }
    public DevMonitor? Dev { get; set; }
    public BrowserTab? SplitWith { get; set; }
    public int ExpectedZoom { get; set; } = 100;     // масштаб, который поставил сам браузер (для запоминания по сайтам)
    public string DarkSig { get; set; } = "";
    public Image? Thumb { get; set; }   // уменьшенный снимок страницы для превью
    public string? DarkScriptId { get; set; }   // пара в разделённом экране
}

/// <summary>Круглая кнопка-иконка; умеет показывать кольцо прогресса.</summary>
sealed class IconButton : Control
{
    bool hover, down;
    string glyph;
    float progress = -1;
    Color? glyphColor;
    static Font SmallFont => Theme.GetFont(Theme.IconFont, 8.8f);

    public IconButton(string glyph, string tip, ToolTip tips)
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                 ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        SetStyle(ControlStyles.Selectable, false);
        // Клик считаем сами: стандартный Click теряется, если между нажатием и отпусканием
        // захват мыши ушёл (например, к движку WebView2 в другом процессе)
        SetStyle(ControlStyles.StandardClick | ControlStyles.StandardDoubleClick, false);
        this.glyph = glyph;
        Font = new Font(Theme.IconFont, 11f);
        BackColor = Theme.Toolbar;
        Cursor = Cursors.Hand;
        tips.SetToolTip(this, L.T(tip));
    }

    public string Glyph { get => glyph; set { if (glyph != value) { glyph = value; Invalidate(); } } }
    /// <summary>Показывать обычный текст (например «100%») вместо значка.</summary>
    public bool TextMode { get; set; }
    public Color? GlyphColor { get => glyphColor; set { if (glyphColor != value) { glyphColor = value; Invalidate(); } } }
    /// <summary>-1 — без кольца, 0..1 — прогресс.</summary>
    public float Progress { get => progress; set { if (Math.Abs(progress - value) > 0.003f) { progress = value; Invalidate(); } } }

    protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { hover = down = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnMouseDown(MouseEventArgs e) { down = true; Invalidate(); base.OnMouseDown(e); }
    protected override void OnMouseUp(MouseEventArgs e)
    {
        bool click = down && e.Button == MouseButtons.Left && ClientRectangle.Contains(e.Location);
        down = false;
        Invalidate();
        base.OnMouseUp(e);
        if (click && Enabled) OnClick(EventArgs.Empty);
    }
    protected override void OnEnabledChanged(EventArgs e) { Invalidate(); base.OnEnabledChanged(e); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(BackColor);
        if (Enabled && (hover || down))
        {
            using var b = new SolidBrush(down ? Theme.Pressed : Theme.Hover);
            if (TextMode)
            {
                using var p = Theme.Round(new RectangleF(1, 1, Width - 3, Height - 3), (Height - 3) / 2f);
                g.FillPath(b, p);
            }
            else g.FillEllipse(b, 1, 1, Width - 3, Height - 3);
        }
        if (progress >= 0)
        {
            float w = Theme.S(2.2f), pad = Theme.S(4);
            var r = new RectangleF(pad, pad, Width - pad * 2 - 1, Height - pad * 2 - 1);
            using var track = new Pen(Color.FromArgb(60, Theme.Accent), w);
            g.DrawEllipse(track, r);
            using var pen = new Pen(Theme.Accent, w) { StartCap = LineCap.Round, EndCap = LineCap.Round };
            g.DrawArc(pen, r, -90, Math.Max(8, 360 * progress));
        }
        var color = !Enabled ? Color.FromArgb(78, 82, 96) : glyphColor ?? Theme.Text;
        var font = TextMode ? Theme.GetFont("Segoe UI", 9f, FontStyle.Bold) : progress >= 0 ? SmallFont : Theme.GetFont(Theme.IconFont, 11f);
        TextRenderer.DrawText(g, glyph, font, ClientRectangle, color, Theme.Center);
    }
}

/// <summary>Адресная строка в виде «таблетки» с замком, звездой закладки и счётчиком блокировок.</summary>
sealed class AddressBox : Control
{
    public readonly TextBox Box = new() { BorderStyle = BorderStyle.None };
    Font iconFont => Theme.GetFont(Theme.IconFont, 9.5f);
    Font starFont => Theme.GetFont(Theme.IconFont, 11f);
    Font smallFont => Theme.GetFont("Segoe UI", 8.5f, FontStyle.Bold);
    string lockGlyph = "";
    Color lockColor = Theme.TextDim;
    int blocked;
    bool starVisible, starred, starHover;
    bool keyVisible, keyActive, keyHover;

    public event Action? StarClicked;
    public event Action? KeyClicked;
    public event Action? ShieldClicked;
    public event Action? LockClicked;
    bool lockHover;
    Rectangle LockRect => new(Theme.S(7), (Height - Theme.S(28)) / 2, Theme.S(32), Theme.S(28));
    /// <summary>Положение значка замка на экране (для панели сайта).</summary>
    public Rectangle LockScreenRect => RectangleToScreen(LockRect);
    bool shieldOff, pillHover;

    public AddressBox()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                 ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        BackColor = Theme.Toolbar;
        Box.BackColor = Theme.Field;
        Box.ForeColor = Theme.Text;
        Box.Font = Theme.GetFont("Segoe UI", 10.5f);
        Controls.Add(Box);
        Box.GotFocus += (_, _) => Invalidate();
        Box.LostFocus += (_, _) => Invalidate();
        Cursor = Cursors.IBeam;
    }

    bool PillVisible => blocked > 0 || shieldOff;
    string PillText => shieldOff ? "выкл" : blocked.ToString();

    int PillWidth
    {
        get
        {
            if (!PillVisible) return 0;
            int tw = TextRenderer.MeasureText(PillText, smallFont, Size.Empty, TextFormatFlags.NoPadding).Width;
            return tw + Theme.S(30);
        }
    }

    Rectangle PillRect
    {
        get
        {
            int w = PillWidth, h = Theme.S(22);
            return new Rectangle(Width - w - Theme.S(7), (Height - h) / 2, w, h);
        }
    }

    int IconsRight => Width - Theme.S(6) - (PillVisible ? PillWidth + Theme.S(4) : 0);

    Rectangle StarRect
    {
        get
        {
            int sz = Theme.S(28);
            return new Rectangle(IconsRight - sz, (Height - sz) / 2, sz, sz);
        }
    }

    Rectangle KeyRect
    {
        get
        {
            int sz = Theme.S(28), right = starVisible ? StarRect.X - Theme.S(2) : IconsRight;
            return new Rectangle(right - sz, (Height - sz) / 2, sz, sz);
        }
    }

    /// <summary>Левая граница значков справа — до неё тянется поле ввода.</summary>
    int IconsLeft => keyVisible ? KeyRect.X : starVisible ? StarRect.X : IconsRight + (PillVisible ? Theme.S(4) : Theme.S(6));

    protected override void OnMouseMove(MouseEventArgs e)
    {
        bool h = starVisible && StarRect.Contains(e.Location);
        bool k = keyVisible && KeyRect.Contains(e.Location);
        bool p = PillVisible && PillRect.Contains(e.Location);
        bool l = lockGlyph.Length > 0 && LockRect.Contains(e.Location);
        Cursor = h || k || p || l ? Cursors.Hand : Cursors.IBeam;
        if (h != starHover || k != keyHover || p != pillHover || l != lockHover) { starHover = h; keyHover = k; pillHover = p; lockHover = l; Invalidate(); }
        base.OnMouseMove(e);
    }

    protected override void OnMouseLeave(EventArgs e) { starHover = keyHover = pillHover = lockHover = false; Invalidate(); base.OnMouseLeave(e); }

    /// <summary>Положение значка щита на экране (для меню).</summary>
    public Rectangle PillScreenRect => RectangleToScreen(PillVisible ? PillRect : new Rectangle(Width - Theme.S(30), 0, Theme.S(24), Height));

    protected override void OnMouseDown(MouseEventArgs e)
    {
        if (lockGlyph.Length > 0 && LockRect.Contains(e.Location)) LockClicked?.Invoke();
        else if (PillVisible && PillRect.Contains(e.Location)) ShieldClicked?.Invoke();
        else if (starVisible && StarRect.Contains(e.Location)) StarClicked?.Invoke();
        else if (keyVisible && KeyRect.Contains(e.Location)) KeyClicked?.Invoke();
        else { Box.Focus(); Box.SelectAll(); }
        base.OnMouseDown(e);
    }

    /// <summary>Положение кнопки-ключа на экране (для всплывающих окон).</summary>
    public Rectangle KeyScreenRect => RectangleToScreen(KeyRect);

    public void SetKey(bool visible, bool active)
    {
        if (visible == keyVisible && active == keyActive) return;
        keyVisible = visible; keyActive = active; PerformLayout(); Invalidate();
    }

    public void ApplyScale()
    {
        Box.Font = Theme.GetFont("Segoe UI", 10.5f);
        PerformLayout();
        Invalidate();
    }

    public void SetLock(string glyph, Color color)
    {
        if (glyph == lockGlyph && color == lockColor) return;
        lockGlyph = glyph; lockColor = color; Invalidate();
    }

    public void SetBlocked(int n, bool off = false)
    {
        if (n == blocked && off == shieldOff) return;
        blocked = n; shieldOff = off; PerformLayout(); Invalidate();
    }

    public void SetStar(bool visible, bool filled)
    {
        if (visible == starVisible && filled == starred) return;
        starVisible = visible; starred = filled; PerformLayout(); Invalidate();
    }

    protected override void OnLayout(LayoutEventArgs e)
    {
        base.OnLayout(e);
        int x = Theme.S(40), bh = Box.PreferredHeight;
        int right = (keyVisible || starVisible ? IconsLeft : PillVisible ? PillRect.X : Width) - Theme.S(8);
        Box.SetBounds(x, (Height - bh) / 2 + Theme.S(1), Math.Max(10, right - x), bh);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(BackColor);
        var r = new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f);
        using (var path = Theme.Round(r, r.Height / 2))
        {
            using var fill = new SolidBrush(Theme.Field);
            g.FillPath(fill, path);
            using var pen = Box.Focused ? new Pen(Theme.Accent, Theme.S(1.6f)) : new Pen(Theme.Line, 1f);
            g.DrawPath(pen, path);
        }

        if (lockGlyph.Length > 0)
        {
            if (lockHover)
            {
                using var lb = new SolidBrush(Theme.Hover);
                using var lp = Theme.Round(LockRect, LockRect.Height / 2f);
                g.FillPath(lb, lp);
            }
            TextRenderer.DrawText(g, lockGlyph, iconFont, new Rectangle(Theme.S(12), 0, Theme.S(22), Height), lockColor, Theme.Center);
        }

        if (starVisible)
        {
            var sr = StarRect;
            if (starHover)
            {
                using var b = new SolidBrush(Theme.Hover);
                g.FillEllipse(b, sr);
            }
            TextRenderer.DrawText(g, starred ? "" : "", starFont, sr, starred ? Theme.Accent : Theme.TextDim, Theme.Center);
        }

        if (keyVisible)
        {
            var kr = KeyRect;
            if (keyHover || keyActive)
            {
                using var b = new SolidBrush(keyActive && !keyHover ? Color.FromArgb(44, 124, 156, 255) : Theme.Hover);
                g.FillEllipse(b, kr);
            }
            TextRenderer.DrawText(g, "", starFont, kr, keyActive ? Theme.Accent : Theme.TextDim, Theme.Center);
        }

        if (PillVisible)
        {
            var text = PillText;
            var pill = PillRect;
            int w = pill.Width, h = pill.Height;
            var color = shieldOff ? Theme.TextDim : Theme.Accent;
            using var path = Theme.Round(pill, h / 2f);
            using var b = new SolidBrush(shieldOff ? Color.FromArgb(pillHover ? 50 : 30, 255, 255, 255) : Color.FromArgb(pillHover ? 80 : 44, 124, 156, 255));
            g.FillPath(b, path);
            TextRenderer.DrawText(g, "", iconFont, new Rectangle(pill.X + Theme.S(6), pill.Y, Theme.S(16), h), color, Theme.Center);
            TextRenderer.DrawText(g, text, smallFont, new Rectangle(pill.X + Theme.S(23), pill.Y, w - Theme.S(23), h), color,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
        }
    }
}

/// <summary>Группа вкладок: название, цвет, можно свернуть.</summary>
sealed class TabGroup
{
    public static readonly Color[] Palette =
    {
        Color.FromArgb(124, 156, 255), Color.FromArgb(255, 107, 107), Color.FromArgb(245, 200, 76), Color.FromArgb(95, 211, 141),
        Color.FromArgb(255, 111, 181), Color.FromArgb(176, 124, 255), Color.FromArgb(63, 193, 201), Color.FromArgb(255, 159, 74),
    };
    public string Name { get; set; } = "";
    public Color Color { get; set; } = Palette[0];
    public bool Collapsed { get; set; }
}

/// <summary>Полоса вкладок: рисуется вручную, вкладки можно перетаскивать, закреплять и объединять в группы.</summary>
sealed class TabStrip : Control
{
    sealed record Slot(Rectangle R, BrowserTab? Tab, TabGroup? Label);

    readonly List<BrowserTab> tabs;
    Font titleFont => Theme.GetFont("Segoe UI", 9f);
    Font iconFont => Theme.GetFont(Theme.IconFont, 10f);
    Font smallIcon => Theme.GetFont(Theme.IconFont, 7f);
    Font audioFont => Theme.GetFont(Theme.IconFont, 8.5f);
    Font badgeFont => Theme.GetFont("Segoe UI", 8.5f, FontStyle.Bold);
    Font groupFont => Theme.GetFont("Segoe UI", 8.5f, FontStyle.Bold);
    BrowserTab? selected, dragTab;
    Point dragStart;
    bool dragging;
    BrowserTab? hoverTab;
    TabGroup? hoverLabel;
    bool hoverClose, hoverPlus, hoverAudio;
    List<Slot> slots = new();
    Rectangle plusRect, collapseRect;
    bool hoverCollapse;
    int scroll;

    /// <summary>Вертикальные вкладки (слева), Compact — только значки.</summary>
    public bool Vertical { get; set; }
    public bool Compact { get; set; }
    public event Action? CompactToggled;
    /// <summary>Мышь над вкладкой (для превью): вкладка и её прямоугольник на экране; null — ушла.</summary>
    public event Action<BrowserTab?, Rectangle>? HoverChanged;
    /// <summary>Кнопка «⌄» справа — поиск по вкладкам.</summary>
    public event Action? TabSearch;
    Rectangle searchRect;
    bool hoverSearch;

    // ---- Свои кнопки окна (заголовок Windows убран, вкладки на его месте) ----
    public bool CaptionButtons { get; set; }
    public bool WindowMaximized { get; set; }
    public event Action? MinimizeClicked, CloseClicked;
    bool hoverMin, hoverCloseWin, hoverMaxNc;
    /// <summary>Наведение на «развернуть» приходит от окна (это неклиентская кнопка — ради раскладок Windows 11).</summary>
    public bool HoverMax { get => hoverMaxNc; set { if (hoverMaxNc != value) { hoverMaxNc = value; Invalidate(); } } }
    int CapW => CaptionButtons && !Vertical ? S(46) * 3 : 0;
    Rectangle MinRect => new(Width - CapW, 0, S(46), Height);
    public Rectangle MaxRect => new(Width - CapW + S(46), 0, S(46), Height);
    Rectangle CloseWinRect => new(Width - S(46), 0, S(46), Height);

    /// <summary>Что под точкой для окна: 0 — вкладки/кнопки, 1 — пустое место (тянуть окно), 2 — «развернуть», 3 — верхний край (менять размер).</summary>
    public int NcHit(Point p)
    {
        if (!CaptionButtons || Vertical) return 0;
        Relayout();
        if (CapW > 0 && MaxRect.Contains(p)) return 2;
        if (CapW > 0 && (MinRect.Contains(p) || CloseWinRect.Contains(p))) return 0;
        if (!WindowMaximized && p.Y < S(5)) return 3;
        if (slots.Any(s => s.R.Contains(p)) || plusRect.Contains(p) || searchRect.Contains(p)) return 0;
        return 1;
    }

    protected override void WndProc(ref Message m)
    {
        // Пустое место полосы и «развернуть» отдаём окну: там его можно тянуть, разворачивать двойным кликом, открывать раскладки
        if (m.Msg == 0x84 && CaptionButtons && !Vertical)
        {
            var p = PointToClient(new Point((short)(m.LParam.ToInt64() & 0xFFFF), (short)((m.LParam.ToInt64() >> 16) & 0xFFFF)));
            if (NcHit(p) != 0) { m.Result = (IntPtr)(-1); return; } // HTTRANSPARENT
        }
        base.WndProc(ref m);
    }

    public float Spin;
    public Color FrameColor { get; set; } = Theme.Frame;
    public string? Badge { get; set; }
    public event Action<BrowserTab>? TabSelected;
    public event Action<BrowserTab>? TabClosed;
    public event Action<BrowserTab>? TabMuteToggled;
    public event Action<BrowserTab, Point>? TabMenu;
    public event Action<TabGroup>? GroupClicked;
    public event Action<TabGroup, Point>? GroupMenu;
    public event Action? NewTab;

    public TabStrip(List<BrowserTab> tabs)
    {
        this.tabs = tabs;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                 ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
    }

    public BrowserTab? Selected { get => selected; set { selected = value; Invalidate(); } }

    static int S(float v) => Theme.S(v);
    int Top0 => S(8);
    int BadgeWidth => Badge == null ? 0 : TextRenderer.MeasureText(Badge, badgeFont).Width + S(40);
    int LabelWidth(TabGroup g) => TextRenderer.MeasureText(g.Name.Length > 0 ? g.Name : "  ", groupFont).Width + S(26);

    bool Hidden(BrowserTab t) => t.Group?.Collapsed == true && t != selected && !t.Pinned;

    int RowH => S(38);

    void RelayoutVertical()
    {
        var list = new List<Slot>();
        int pad = S(6), w = Width - pad * 2, y = S(8) - scroll;
        // Закреплённые — плиткой сверху
        int pinS = S(40), px = pad;
        var pinned = tabs.Where(t => t.Pinned).ToList();
        foreach (var t in pinned)
        {
            if (px + pinS > Width - pad) { px = pad; y += pinS + S(4); }
            list.Add(new Slot(new Rectangle(px, y, pinS, pinS), t, null));
            px += pinS + S(4);
        }
        if (pinned.Count > 0) y += pinS + S(10);
        TabGroup? prev = null;
        foreach (var t in tabs.Where(t => !t.Pinned))
        {
            if (t.Group != prev)
            {
                if (t.Group != null)
                {
                    list.Add(new Slot(new Rectangle(pad, y, w, S(26)), null, t.Group));
                    y += S(28);
                }
                prev = t.Group;
            }
            if (Hidden(t)) continue;
            list.Add(new Slot(new Rectangle(pad, y, w, RowH), t, null));
            y += RowH + S(2);
        }
        slots = list;
        plusRect = new Rectangle(pad, y + S(4), w, S(34));
        int cs = S(32);
        collapseRect = new Rectangle(pad, Height - cs - S(8), Compact ? w : cs, cs);
    }

    int ContentHeight => slots.Count == 0 ? 0 : plusRect.Bottom + scroll + S(56);

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        if (Vertical)
        {
            Relayout();
            int max = Math.Max(0, ContentHeight - Height);
            scroll = Math.Clamp(scroll - Math.Sign(e.Delta) * RowH * 2, 0, max);
            Invalidate();
        }
        base.OnMouseWheel(e);
    }

    void Relayout()
    {
        if (Vertical) { RelayoutVertical(); return; }
        var list = new List<Slot>();
        int h = Height - Top0, pinW = S(46);
        var visible = tabs.Where(t => !Hidden(t)).ToList();
        int pinned = visible.Count(t => t.Pinned);
        var groups = new List<TabGroup>();
        TabGroup? prev = null;
        foreach (var t in tabs.Where(t => !t.Pinned))
        {
            if (t.Group != prev && t.Group != null) groups.Add(t.Group);
            prev = t.Group;
        }
        int labels = groups.Sum(LabelWidth) + groups.Count * S(4);
        int normal = visible.Count(t => !t.Pinned);
        int avail = Width - S(12) - S(48) - S(40) - BadgeWidth - CapW - pinned * pinW - labels;
        int tw = Math.Clamp(avail / Math.Max(1, normal), S(44), S(236));
        int x = S(12);
        prev = null;
        foreach (var t in tabs)
        {
            if (t.Pinned) { list.Add(new Slot(new Rectangle(x, Top0, pinW, h), t, null)); x += pinW; continue; }
            if (t.Group != prev)
            {
                if (t.Group != null)
                {
                    int lw = LabelWidth(t.Group);
                    list.Add(new Slot(new Rectangle(x + S(2), Top0, lw, h), null, t.Group));
                    x += lw + S(4);
                }
                prev = t.Group;
            }
            if (Hidden(t)) continue;
            list.Add(new Slot(new Rectangle(x, Top0, tw, h), t, null));
            x += tw;
        }
        slots = list;
        int sz = S(30);
        plusRect = new Rectangle(x + S(8), Top0 + (h - sz) / 2, sz, sz);
        searchRect = new Rectangle(Width - CapW - BadgeWidth - sz - S(8), Top0 + (h - sz) / 2, sz, sz);
    }

    Rectangle CloseRect(Rectangle r) => new(r.Right - S(30), r.Y + (r.Height - S(22)) / 2, S(22), S(22));
    bool ShowClose(BrowserTab t, Rectangle r) => !t.Pinned && !(Vertical && Compact) && (t == selected || r.Width >= S(100) || (Vertical && t == hoverTab));
    bool ShowAudio(BrowserTab t, Rectangle r) => (t.Audio || t.Muted) && (t.Pinned || r.Width >= S(90)) && !(Vertical && Compact);

    Rectangle AudioRect(BrowserTab t, Rectangle r)
    {
        if (t.Pinned) return new Rectangle(r.Right - S(16), r.Y + S(2), S(14), S(14));
        int right = ShowClose(t, r) ? CloseRect(r).X - S(2) : r.Right - S(8);
        return new Rectangle(right - S(22), r.Y + (r.Height - S(22)) / 2, S(22), S(22));
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        Relayout();
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.Clear(FrameColor);

        for (int i = 0; i < slots.Count; i++)
        {
            var (r, t, label) = slots[i];
            if (label != null)
            {
                var lr = Vertical ? new RectangleF(r.X, r.Y, Compact ? r.Width : Math.Min(r.Width, LabelWidth(label)), r.Height)
                                  : new RectangleF(r.X, r.Y + (r.Height - S(24)) / 2f - S(2), r.Width, S(24));
                using var p = Theme.Round(lr, S(12));
                using var b = new SolidBrush(label == hoverLabel ? ControlPaint.Light(label.Color, 0.15f) : label.Color);
                g.FillPath(b, p);
                TextRenderer.DrawText(g, label.Name.Length > 0 ? label.Name : (label.Collapsed ? "…" : ""), groupFont, Rectangle.Round(lr),
                    Color.FromArgb(15, 17, 24), Theme.Center);
                continue;
            }
            if (t == null) continue;
            bool isSel = t == selected, isHover = t == hoverTab;
            if (isSel)
            {
                using var p = Vertical ? Theme.Round(r, S(9)) : Theme.TabShape(r, S(9));
                using var b = new SolidBrush(Vertical ? Theme.Hover : Theme.Toolbar);
                g.FillPath(b, p);
                if (Vertical && !t.Pinned)
                {
                    using var ab = new SolidBrush(Theme.Accent);
                    g.FillRectangle(ab, r.X, r.Y + S(9), S(3), r.Height - S(18));
                }
            }
            else if (selected != null && t.SplitWith == selected)
            {
                // Вторая половина разделённого экрана
                using var p = Vertical ? Theme.Round(r, S(9)) : Theme.TabShape(r, S(9));
                using var b = new SolidBrush(Color.FromArgb(150, Vertical ? Theme.Hover : Theme.Toolbar));
                g.FillPath(b, p);
            }
            else if (isHover)
            {
                using var p = Vertical ? Theme.Round(r, S(9)) : Theme.Round(new RectangleF(r.X + S(3), r.Y + S(2), r.Width - S(6), r.Height - S(8)), S(8));
                using var b = new SolidBrush(Color.FromArgb(22, Theme.Text));
                g.FillPath(b, p);
            }
            if (t.Group != null)
            {
                using var gb = new SolidBrush(t.Group.Color);
                if (Vertical) g.FillRectangle(gb, r.Right - S(4), r.Y + S(8), S(3), r.Height - S(16));
                else g.FillRectangle(gb, r.X + S(10), r.Bottom - S(3), r.Width - S(20), S(3));
            }
            var next = i + 1 < slots.Count ? slots[i + 1].Tab : null;
            if (!Vertical && next != null && !isSel && next != selected && !isHover && next != hoverTab && !t.Pinned)
            {
                using var pen = new Pen(Theme.Line, 1f);
                g.DrawLine(pen, r.Right, r.Y + S(9), r.Right, r.Bottom - S(11));
            }

            int ix = t.Pinned || (Vertical && Compact) ? r.X + (r.Width - S(16)) / 2 : r.X + S(14), iy = r.Y + (r.Height - S(16)) / 2 - (Vertical ? 0 : S(2));
            if (t.Loading)
            {
                using var pen = new Pen(Theme.Accent, S(2)) { StartCap = LineCap.Round, EndCap = LineCap.Round };
                g.DrawArc(pen, ix + S(1), iy + S(1), S(14), S(14), Spin, 270);
            }
            else if (t.Icon != null && !t.IsHome)
                g.DrawImage(t.Icon, ix, iy, S(16), S(16));
            else
                TextRenderer.DrawText(g, t.IsHome ? "" : "", iconFont,
                    new Rectangle(ix - S(2), iy - S(2), S(20), S(20)), t.IsHome ? Theme.Accent : Theme.TextDim, Theme.Center);

            bool close = ShowClose(t, r), audio = ShowAudio(t, r);
            if (!t.Pinned)
            {
                int tx = ix + S(26);
                int tr = audio ? AudioRect(t, r).X - S(2) : close ? CloseRect(r).X - S(4) : r.Right - S(10);
                var titleColor = isSel ? Theme.Text : t.Frozen || t.PendingUrl != null ? Color.FromArgb(150, Theme.TextDim) : Theme.TextDim;
                if (tr > tx)
                if (tr > tx && !(Vertical && Compact))
                    TextRenderer.DrawText(g, t.Title, titleFont, new Rectangle(tx, r.Y, tr - tx, r.Height - (Vertical ? 0 : S(4))), titleColor,
                        TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis |
                        TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding);
            }
            if (audio)
            {
                var a = AudioRect(t, r);
                if (!t.Pinned) a.Offset(0, -S(2));
                if (isHover && hoverAudio)
                {
                    using var b = new SolidBrush(Theme.Hover);
                    g.FillEllipse(b, a);
                }
                TextRenderer.DrawText(g, t.Muted ? "" : "", t.Pinned ? smallIcon : audioFont, a, t.Muted ? Theme.Danger : Theme.Text, Theme.Center);
            }
            if (close)
            {
                var c = CloseRect(r);
                c.Offset(0, -S(2));
                if (isHover && hoverClose)
                {
                    using var b = new SolidBrush(Theme.Hover);
                    g.FillEllipse(b, c);
                }
                TextRenderer.DrawText(g, "", smallIcon, c, isSel || isHover ? Theme.Text : Theme.TextDim, Theme.Center);
            }
        }

        if (Vertical)
        {
            // «+ Новая вкладка» строкой и кнопка «свернуть до значков» внизу
            if (hoverPlus)
            {
                using var p = Theme.Round(plusRect, S(9));
                using var b = new SolidBrush(Color.FromArgb(22, Theme.Text));
                g.FillPath(b, p);
            }
            if (Compact) TextRenderer.DrawText(g, "", iconFont, plusRect, Theme.Text, Theme.Center);
            else
            {
                TextRenderer.DrawText(g, "", iconFont, new Rectangle(plusRect.X + S(12), plusRect.Y, S(20), plusRect.Height), Theme.TextDim, Theme.Center);
                TextRenderer.DrawText(g, L.T("Новая вкладка"), titleFont, new Rectangle(plusRect.X + S(40), plusRect.Y, plusRect.Width - S(44), plusRect.Height), Theme.TextDim,
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
            }
            using (var bg = new SolidBrush(FrameColor)) g.FillRectangle(bg, 0, collapseRect.Y - S(8), Width, Height - collapseRect.Y + S(8));
            if (hoverCollapse)
            {
                using var p = Theme.Round(collapseRect, S(8));
                using var b = new SolidBrush(Theme.Hover);
                g.FillPath(b, p);
            }
            TextRenderer.DrawText(g, Compact ? "" : "", smallIcon, collapseRect, Theme.TextDim, Theme.Center);
            using (var pen = new Pen(Theme.Line)) g.DrawLine(pen, Width - 1, 0, Width - 1, Height);
            return;
        }

        var pr = plusRect;
        pr.Offset(0, -S(2));
        if (hoverPlus)
        {
            using var b = new SolidBrush(Theme.Hover);
            g.FillEllipse(b, pr);
        }
        TextRenderer.DrawText(g, "", iconFont, pr, Theme.Text, Theme.Center);

        // «⌄» — поиск по вкладкам
        var sr = searchRect;
        sr.Offset(0, -S(2));
        if (hoverSearch)
        {
            using var b = new SolidBrush(Theme.Hover);
            g.FillEllipse(b, sr);
        }
        TextRenderer.DrawText(g, "", smallIcon, sr, Theme.TextDim, Theme.Center);

        if (Badge != null)
        {
            int w = BadgeWidth - S(12), h = S(26);
            var br = new RectangleF(Width - CapW - w - S(10), Top0 + (Height - Top0 - h) / 2f - S(2), w, h);
            using var path = Theme.Round(br, h / 2f);
            using var b = new SolidBrush(Color.FromArgb(70, 176, 124, 255));
            g.FillPath(b, path);
            TextRenderer.DrawText(g, "", smallIcon, new Rectangle((int)br.X + S(8), (int)br.Y, S(16), h), Theme.Text, Theme.Center);
            TextRenderer.DrawText(g, Badge, badgeFont, new Rectangle((int)br.X + S(26), (int)br.Y, w - S(30), h), Theme.Text,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
        }

        // Кнопки окна: свернуть, развернуть/восстановить, закрыть
        if (CapW > 0)
        {
            var capFont = Theme.GetFont(Theme.IconFont, 7.5f);
            void Cap(Rectangle r, string glyph, bool hover, bool danger)
            {
                if (hover)
                {
                    using var b = new SolidBrush(danger ? Color.FromArgb(232, 17, 35) : Color.FromArgb(28, Theme.Text));
                    g.FillRectangle(b, r);
                }
                TextRenderer.DrawText(g, glyph, capFont, r, hover && danger ? Color.White : Theme.Text, Theme.Center);
            }
            Cap(MinRect, "\uE921", hoverMin, false);
            Cap(MaxRect, WindowMaximized ? "\uE923" : "\uE922", hoverMaxNc, false);
            Cap(CloseWinRect, "\uE8BB", hoverCloseWin, true);
        }
    }

    void UpdateHover(Point p)
    {
        Relayout();
        BrowserTab? ht = null;
        TabGroup? hl = null;
        bool hc = false, ha = false;
        foreach (var (r, t, label) in slots)
        {
            if (!r.Contains(p)) continue;
            if (label != null) { hl = label; break; }
            ht = t;
            hc = t != null && ShowClose(t, r) && CloseRect(r).Contains(p);
            ha = t != null && ShowAudio(t, r) && AudioRect(t, r).Contains(p);
            break;
        }
        bool hp = plusRect.Contains(p);
        bool hco = Vertical && collapseRect.Contains(p);
        bool hs = !Vertical && searchRect.Contains(p);
        bool hmin = CapW > 0 && MinRect.Contains(p), hcw = CapW > 0 && CloseWinRect.Contains(p);
        if (hmin != hoverMin || hcw != hoverCloseWin) { hoverMin = hmin; hoverCloseWin = hcw; Invalidate(); }
        if (hmin || hcw) { hs = false; HoverMax = false; }
        if (hmin || hcw) { ht = null; hl = null; hc = ha = false; }
        if (hs != hoverSearch) { hoverSearch = hs; Invalidate(); }
        if (hs) { ht = null; hl = null; hc = ha = hp = false; }
        if (hco) { ht = null; hl = null; hc = ha = hp = false; }
        if (ht == hoverTab && hl == hoverLabel && hc == hoverClose && hp == hoverPlus && ha == hoverAudio && hco == hoverCollapse) return;
        bool tabChanged = ht != hoverTab;
        hoverTab = ht; hoverLabel = hl; hoverClose = hc; hoverPlus = hp; hoverAudio = ha; hoverCollapse = hco;
        if (tabChanged)
        {
            var slot = ht == null ? null : slots.FirstOrDefault(s => s.Tab == ht);
            HoverChanged?.Invoke(ht, slot == null ? Rectangle.Empty : RectangleToScreen(slot.R));
        }
        Invalidate();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Left && dragTab != null)
        {
            if (!dragging && (Vertical ? Math.Abs(e.Y - dragStart.Y) : Math.Abs(e.X - dragStart.X)) > S(6)) dragging = true;
            if (dragging)
            {
                var target = slots.FirstOrDefault(s => s.Tab != null && s.Tab != dragTab &&
                    s.R.Contains(Vertical ? new Point(s.R.X + 2, e.Y) : new Point(e.X, s.R.Y + 2)))?.Tab;
                if (target != null && target.Pinned == dragTab.Pinned)
                {
                    int to = tabs.IndexOf(target);
                    tabs.Remove(dragTab);
                    tabs.Insert(to, dragTab);
                    dragTab.Group = target.Group; // перетащили в группу — вкладка в неё и входит
                    Invalidate();
                }
                return;
            }
        }
        UpdateHover(e.Location);
        base.OnMouseMove(e);
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        hoverTab = null; hoverLabel = null; hoverClose = hoverPlus = hoverAudio = hoverCollapse = false;
        hoverMin = hoverCloseWin = false;
        HoverChanged?.Invoke(null, Rectangle.Empty);
        Invalidate();
        base.OnMouseLeave(e);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        UpdateHover(e.Location);
        HoverChanged?.Invoke(null, Rectangle.Empty);
        if (hoverCollapse && e.Button == MouseButtons.Left) { CompactToggled?.Invoke(); base.OnMouseDown(e); return; }
        if (hoverSearch && e.Button == MouseButtons.Left) { TabSearch?.Invoke(); base.OnMouseDown(e); return; }
        if (hoverMin && e.Button == MouseButtons.Left) { MinimizeClicked?.Invoke(); base.OnMouseDown(e); return; }
        if (hoverCloseWin && e.Button == MouseButtons.Left) { CloseClicked?.Invoke(); base.OnMouseDown(e); return; }
        if (hoverLabel != null)
        {
            if (e.Button == MouseButtons.Left) GroupClicked?.Invoke(hoverLabel);
            else if (e.Button == MouseButtons.Right) GroupMenu?.Invoke(hoverLabel, PointToScreen(e.Location));
        }
        else if (hoverTab != null)
        {
            var t = hoverTab;
            if (e.Button == MouseButtons.Middle || (e.Button == MouseButtons.Left && hoverClose)) TabClosed?.Invoke(t);
            else if (e.Button == MouseButtons.Left && hoverAudio) TabMuteToggled?.Invoke(t);
            else if (e.Button == MouseButtons.Left)
            {
                TabSelected?.Invoke(t);
                dragTab = t;
                dragStart = e.Location;
            }
            else if (e.Button == MouseButtons.Right) TabMenu?.Invoke(t, PointToScreen(e.Location));
        }
        else if (hoverPlus && e.Button == MouseButtons.Left) NewTab?.Invoke();
        if (!IsDisposed) UpdateHover(PointToClient(Cursor.Position));
        base.OnMouseDown(e);
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        dragTab = null;
        dragging = false;
        base.OnMouseUp(e);
    }

    protected override void OnMouseDoubleClick(MouseEventArgs e)
    {
        if (hoverTab == null && hoverLabel == null && !hoverPlus && e.Button == MouseButtons.Left) NewTab?.Invoke();
        base.OnMouseDoubleClick(e);
    }
}

/// <summary>Плашка-уведомление под панелью: «Доступно обновление», «Сделать браузером по умолчанию?» и т. п.</summary>
sealed class InfoBar : Panel
{
    readonly Label icon = new() { AutoSize = false, TextAlign = ContentAlignment.MiddleCenter };
    readonly Label text = new() { AutoSize = false, TextAlign = ContentAlignment.MiddleLeft, AutoEllipsis = true };
    readonly FlowLayoutPanel buttons = new() { FlowDirection = FlowDirection.RightToLeft, WrapContents = false, AutoSize = true };
    readonly Button close = new();
    public string? Key { get; private set; }

    public InfoBar()
    {
        Dock = DockStyle.Top;
        Visible = false;
        BackColor = Color.FromArgb(40, 46, 70);
        DoubleBuffered = true;
        icon.ForeColor = Theme.Accent;
        text.ForeColor = Theme.Text;
        close.Text = "";
        close.FlatStyle = FlatStyle.Flat;
        close.FlatAppearance.BorderSize = 0;
        close.ForeColor = Theme.TextDim;
        close.Cursor = Cursors.Hand;
        close.Click += (_, _) => HideBar();
        Controls.AddRange(new Control[] { icon, text, buttons, close });
        Resize += (_, _) => DoLayout();
    }

    public void ShowBar(string key, string glyph, string message, params (string label, Action act, bool primary)[] actions)
    {
        Key = key;
        Height = Theme.S(44);
        icon.Font = Theme.GetFont(Theme.IconFont, 12f);
        text.Font = Theme.GetFont("Segoe UI", 9.5f);
        close.Font = Theme.GetFont(Theme.IconFont, 8f);
        icon.Text = glyph;
        text.Text = L.T(message);
        buttons.Controls.Clear();
        foreach (var (label, act, primary) in actions.Reverse())
        {
            var b = new Button
            {
                Text = L.T(label), AutoSize = true, FlatStyle = FlatStyle.Flat, Cursor = Cursors.Hand,
                Font = Theme.GetFont("Segoe UI", 9f, primary ? FontStyle.Bold : FontStyle.Regular),
                BackColor = primary ? Theme.Accent : Theme.Hover, ForeColor = primary ? Color.FromArgb(13, 16, 32) : Theme.Text,
                Margin = new Padding(Theme.S(4), Theme.S(7), 0, 0), Padding = new Padding(Theme.S(8), 0, Theme.S(8), 0), Height = Theme.S(30),
            };
            b.FlatAppearance.BorderSize = 0;
            b.Click += (_, _) => act();
            buttons.Controls.Add(b);
        }
        BackColor = Theme.IsLight ? Color.FromArgb(226, 232, 252) : Color.FromArgb(40, 46, 70);
        icon.ForeColor = Theme.Accent;
        text.ForeColor = Theme.Text;
        close.ForeColor = Theme.TextDim;
        Visible = true;
        DoLayout();
    }

    public void SetText(string message) => text.Text = L.T(message);

    public void HideBar() { Visible = false; Key = null; }

    void DoLayout()
    {
        int h = Height;
        icon.SetBounds(Theme.S(10), 0, Theme.S(30), h);
        close.SetBounds(Width - Theme.S(40), (h - Theme.S(30)) / 2, Theme.S(30), Theme.S(30));
        buttons.PerformLayout();
        var bw = buttons.PreferredSize.Width;
        buttons.SetBounds(close.Left - bw - Theme.S(6), 0, bw, h);
        text.SetBounds(icon.Right + Theme.S(4), 0, Math.Max(10, buttons.Left - icon.Right - Theme.S(10)), h);
    }
}

/// <summary>Панель закладок под адресной строкой.</summary>
sealed class BookmarksBar : Control
{
    Font font => Theme.GetFont("Segoe UI", 9f);
    Font iconFont => Theme.GetFont(Theme.IconFont, 9f);
    readonly List<(Bookmark b, Rectangle r)> items = new();
    readonly List<Bookmark> overflow = new();
    readonly Dictionary<string, Image?> icons = new();
    Rectangle moreRect;
    int hover = -1;
    bool hoverMore;

    public event Action<Bookmark, bool>? OpenBookmark; // bool — в новой вкладке

    public BookmarksBar()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                 ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        BackColor = Theme.Toolbar;
        Store.BookmarksChanged += OnChanged;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) Store.BookmarksChanged -= OnChanged;
        base.Dispose(disposing);
    }

    void OnChanged() { Relayout(); Invalidate(); }
    public void Rescale() => OnChanged();

    Image? IconOf(Bookmark b)
    {
        if (b.Icon == null) return null;
        if (icons.TryGetValue(b.Icon, out var img)) return img;
        try
        {
            using var ms = new MemoryStream(Convert.FromBase64String(b.Icon));
            using var src = Image.FromStream(ms);
            img = new Bitmap(src);
        }
        catch { img = null; }
        icons[b.Icon] = img;
        return img;
    }

    void Relayout()
    {
        items.Clear();
        overflow.Clear();
        int x = Theme.S(10), h = Height - Theme.S(8), y = Theme.S(3);
        int limit = Width - Theme.S(44);
        foreach (var b in Store.Bookmarks)
        {
            int tw = Math.Min(TextRenderer.MeasureText(b.Title, font).Width, Theme.S(150));
            int w = Theme.S(16 + 8 + 16) + tw;
            if (x + w > limit || overflow.Count > 0) { overflow.Add(b); continue; }
            items.Add((b, new Rectangle(x, y, w, h)));
            x += w + Theme.S(2);
        }
        moreRect = overflow.Count > 0 ? new Rectangle(Width - Theme.S(38), y, Theme.S(30), h) : Rectangle.Empty;
    }

    protected override void OnResize(EventArgs e) { Relayout(); base.OnResize(e); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.Clear(BackColor);

        if (Store.Bookmarks.Count == 0)
        {
            TextRenderer.DrawText(g, L.T("Чтобы добавить закладку, нажмите ☆ в адресной строке или Ctrl+D"), font,
                new Rectangle(Theme.S(14), 0, Width, Height - Theme.S(2)), Color.FromArgb(110, 115, 132),
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine);
            return;
        }

        for (int i = 0; i < items.Count; i++)
        {
            var (b, r) = items[i];
            if (i == hover)
            {
                using var p = Theme.Round(r, Theme.S(7));
                using var br = new SolidBrush(Theme.Hover);
                g.FillPath(br, p);
            }
            int ix = r.X + Theme.S(8), iy = r.Y + (r.Height - Theme.S(16)) / 2;
            var icon = IconOf(b);
            if (icon != null) g.DrawImage(icon, ix, iy, Theme.S(16), Theme.S(16));
            else TextRenderer.DrawText(g, "", iconFont, new Rectangle(ix, iy, Theme.S(16), Theme.S(16)), Theme.TextDim, Theme.Center);
            TextRenderer.DrawText(g, b.Title, font, new Rectangle(ix + Theme.S(22), r.Y, r.Right - ix - Theme.S(28), r.Height), Theme.Text,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix);
        }

        if (overflow.Count > 0)
        {
            if (hoverMore)
            {
                using var p = Theme.Round(moreRect, Theme.S(7));
                using var br = new SolidBrush(Theme.Hover);
                g.FillPath(br, p);
            }
            TextRenderer.DrawText(g, "»", Theme.GetFont("Segoe UI", 11f), moreRect, Theme.Text, Theme.Center);
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        int h = items.FindIndex(x => x.r.Contains(e.Location));
        bool hm = moreRect.Contains(e.Location);
        Cursor = h >= 0 || hm ? Cursors.Hand : Cursors.Default;
        if (h != hover || hm != hoverMore) { hover = h; hoverMore = hm; Invalidate(); }
        base.OnMouseMove(e);
    }

    protected override void OnMouseLeave(EventArgs e) { hover = -1; hoverMore = false; Invalidate(); base.OnMouseLeave(e); }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (hoverMore && e.Button == MouseButtons.Left)
        {
            var menu = Theme.NewMenu();
            foreach (var b in overflow.ToList())
            {
                var item = Theme.Item(b.Title.Length > 50 ? b.Title[..48] + "…" : b.Title, () => OpenBookmark?.Invoke(b, false));
                item.MouseUp += (_, me) => { if (me.Button == MouseButtons.Right) ShowItemMenu(b, Cursor.Position); };
                menu.Items.Add(item);
            }
            menu.Show(this, new Point(moreRect.Right - menu.PreferredSize.Width, moreRect.Bottom));
            return;
        }
        if (hover < 0 || hover >= items.Count) return;
        var bm = items[hover].b;
        if (e.Button == MouseButtons.Left) OpenBookmark?.Invoke(bm, (ModifierKeys & Keys.Control) != 0);
        else if (e.Button == MouseButtons.Middle) OpenBookmark?.Invoke(bm, true);
        else if (e.Button == MouseButtons.Right) ShowItemMenu(bm, PointToScreen(e.Location));
    }

    void ShowItemMenu(Bookmark b, Point screen)
    {
        var menu = Theme.NewMenu();
        menu.Items.Add(Theme.Item("Открыть", () => OpenBookmark?.Invoke(b, false)));
        menu.Items.Add(Theme.Item("Открыть в новой вкладке", () => OpenBookmark?.Invoke(b, true)));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(Theme.Item("Переименовать…", () =>
        {
            var name = InputDialog.Ask(FindForm()!, "Название закладки", b.Title);
            if (!string.IsNullOrWhiteSpace(name)) Store.RenameBookmark(b, name);
        }));
        menu.Items.Add(Theme.Item("Удалить", () => Store.RemoveBookmark(b)));
        menu.Show(screen);
    }
}
