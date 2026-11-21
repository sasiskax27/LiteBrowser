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
        this.glyph = glyph;
        Font = new Font(Theme.IconFont, 11f);
        BackColor = Theme.Toolbar;
        Cursor = Cursors.Hand;
        tips.SetToolTip(this, tip);
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
    protected override void OnMouseUp(MouseEventArgs e) { down = false; Invalidate(); base.OnMouseUp(e); }
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

    public event Action? StarClicked;

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

    int PillWidth
    {
        get
        {
            if (blocked <= 0) return 0;
            int tw = TextRenderer.MeasureText(blocked.ToString(), smallFont, Size.Empty, TextFormatFlags.NoPadding).Width;
            return tw + Theme.S(30);
        }
    }

    Rectangle StarRect
    {
        get
        {
            int sz = Theme.S(28), right = Width - Theme.S(6) - (blocked > 0 ? PillWidth + Theme.S(4) : 0);
            return new Rectangle(right - sz, (Height - sz) / 2, sz, sz);
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        bool h = starVisible && StarRect.Contains(e.Location);
        Cursor = h ? Cursors.Hand : Cursors.IBeam;
        if (h != starHover) { starHover = h; Invalidate(); }
        base.OnMouseMove(e);
    }

    protected override void OnMouseLeave(EventArgs e) { starHover = false; Invalidate(); base.OnMouseLeave(e); }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        if (starVisible && StarRect.Contains(e.Location)) StarClicked?.Invoke();
        else { Box.Focus(); Box.SelectAll(); }
        base.OnMouseDown(e);
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

    public void SetBlocked(int n)
    {
        if (n == blocked) return;
        blocked = n; PerformLayout(); Invalidate();
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
        int right = (starVisible ? StarRect.X : Width - (blocked > 0 ? PillWidth + Theme.S(10) : 0)) - Theme.S(8);
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
            TextRenderer.DrawText(g, lockGlyph, iconFont, new Rectangle(Theme.S(12), 0, Theme.S(22), Height), lockColor, Theme.Center);

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

        if (blocked > 0)
        {
            var text = blocked.ToString();
            int w = PillWidth, h = Theme.S(22);
            var pill = new RectangleF(Width - w - Theme.S(7), (Height - h) / 2f, w, h);
            using var path = Theme.Round(pill, h / 2f);
            using var b = new SolidBrush(Color.FromArgb(44, 124, 156, 255));
            g.FillPath(b, path);
            TextRenderer.DrawText(g, "", iconFont, new Rectangle((int)pill.X + Theme.S(6), (int)pill.Y, Theme.S(16), h), Theme.Accent, Theme.Center);
            TextRenderer.DrawText(g, text, smallFont, new Rectangle((int)pill.X + Theme.S(23), (int)pill.Y, w - Theme.S(23), h), Theme.Accent,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
        }
    }
}

/// <summary>Полоса вкладок: рисуется вручную, вкладки можно перетаскивать.</summary>
sealed class TabStrip : Control
{
    readonly List<BrowserTab> tabs;
    Font titleFont => Theme.GetFont("Segoe UI", 9f);
    Font iconFont => Theme.GetFont(Theme.IconFont, 10f);
    Font smallIcon => Theme.GetFont(Theme.IconFont, 7f);
    Font audioFont => Theme.GetFont(Theme.IconFont, 8.5f);
    Font badgeFont => Theme.GetFont("Segoe UI", 8.5f, FontStyle.Bold);
    BrowserTab? selected, dragTab;
    Point dragStart;
    bool dragging;
    int hover = -1;
    bool hoverClose, hoverPlus, hoverAudio;

    public float Spin;
    public Color FrameColor { get; set; } = Theme.Frame;
    public string? Badge { get; set; }
    public event Action<BrowserTab>? TabSelected;
    public event Action<BrowserTab>? TabClosed;
    public event Action<BrowserTab>? TabMuteToggled;
    public event Action<BrowserTab, Point>? TabMenu;
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
    int TabWidth => Math.Clamp((Width - S(12) - S(48) - BadgeWidth) / Math.Max(1, tabs.Count), S(44), S(236));
    Rectangle TabRect(int i) => new(S(12) + i * TabWidth, Top0, TabWidth, Height - Top0);
    Rectangle CloseRect(Rectangle r) => new(r.Right - S(30), r.Y + (r.Height - S(22)) / 2, S(22), S(22));
    bool ShowClose(int i) => tabs[i] == selected || TabWidth >= S(100);
    bool ShowAudio(int i) => (tabs[i].Audio || tabs[i].Muted) && TabWidth >= S(90);

    Rectangle AudioRect(int i, Rectangle r)
    {
        int right = ShowClose(i) ? CloseRect(r).X - S(2) : r.Right - S(8);
        return new Rectangle(right - S(22), r.Y + (r.Height - S(22)) / 2, S(22), S(22));
    }

    Rectangle PlusRect
    {
        get
        {
            int sz = S(30);
            return new Rectangle(S(12) + tabs.Count * TabWidth + S(8), Top0 + (Height - Top0 - sz) / 2, sz, sz);
        }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.Clear(FrameColor);
        int sel = selected == null ? -1 : tabs.IndexOf(selected);

        for (int i = 0; i < tabs.Count; i++)
        {
            var r = TabRect(i);
            var t = tabs[i];
            bool isSel = i == sel, isHover = i == hover;

            if (isSel)
            {
                using var p = Theme.TabShape(r, S(9));
                using var b = new SolidBrush(Theme.Toolbar);
                g.FillPath(b, p);
            }
            else if (isHover)
            {
                using var p = Theme.Round(new RectangleF(r.X + S(3), r.Y + S(2), r.Width - S(6), r.Height - S(8)), S(8));
                using var b = new SolidBrush(Color.FromArgb(18, 255, 255, 255));
                g.FillPath(b, p);
            }

            if (i < tabs.Count - 1 && !isSel && i + 1 != sel && !isHover && i + 1 != hover)
            {
                using var pen = new Pen(Theme.Line, 1f);
                g.DrawLine(pen, r.Right, r.Y + S(9), r.Right, r.Bottom - S(11));
            }

            int ix = r.X + S(14), iy = r.Y + (r.Height - S(16)) / 2 - S(2);
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

            bool close = ShowClose(i), audio = ShowAudio(i);
            int tx = ix + S(26);
            int tr = audio ? AudioRect(i, r).X - S(2) : close ? CloseRect(r).X - S(4) : r.Right - S(10);
            var titleColor = isSel ? Theme.Text : t.Frozen || t.PendingUrl != null ? Color.FromArgb(105, 110, 126) : Theme.TextDim;
            if (tr > tx)
                TextRenderer.DrawText(g, t.Title, titleFont, new Rectangle(tx, r.Y, tr - tx, r.Height - S(4)), titleColor,
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis |
                    TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding);

            if (audio)
            {
                var a = AudioRect(i, r);
                a.Offset(0, -S(2));
                if (isHover && hoverAudio)
                {
                    using var b = new SolidBrush(Theme.Hover);
                    g.FillEllipse(b, a);
                }
                TextRenderer.DrawText(g, t.Muted ? "" : "", audioFont, a, t.Muted ? Theme.Danger : Theme.Text, Theme.Center);
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

        var pr = PlusRect;
        pr.Offset(0, -S(2));
        if (hoverPlus)
        {
            using var b = new SolidBrush(Theme.Hover);
            g.FillEllipse(b, pr);
        }
        TextRenderer.DrawText(g, "", iconFont, pr, Theme.Text, Theme.Center);

        if (Badge != null)
        {
            int w = BadgeWidth - S(12), h = S(26);
            var br = new RectangleF(Width - w - S(10), Top0 + (Height - Top0 - h) / 2f - S(2), w, h);
            using var path = Theme.Round(br, h / 2f);
            using var b = new SolidBrush(Color.FromArgb(70, 176, 124, 255));
            g.FillPath(b, path);
            TextRenderer.DrawText(g, "", smallIcon, new Rectangle((int)br.X + S(8), (int)br.Y, S(16), h), Theme.Text, Theme.Center);
            TextRenderer.DrawText(g, Badge, badgeFont, new Rectangle((int)br.X + S(26), (int)br.Y, w - S(30), h), Theme.Text,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
        }
    }

    void UpdateHover(Point p)
    {
        int h = -1;
        bool hc = false, ha = false;
        for (int i = 0; i < tabs.Count; i++)
        {
            var r = TabRect(i);
            if (!r.Contains(p)) continue;
            h = i;
            hc = ShowClose(i) && CloseRect(r).Contains(p);
            ha = ShowAudio(i) && AudioRect(i, r).Contains(p);
            break;
        }
        bool hp = PlusRect.Contains(p);
        if (h == hover && hc == hoverClose && hp == hoverPlus && ha == hoverAudio) return;
        hover = h; hoverClose = hc; hoverPlus = hp; hoverAudio = ha;
        Invalidate();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Left && dragTab != null)
        {
            if (!dragging && Math.Abs(e.X - dragStart.X) > S(6)) dragging = true;
            if (dragging)
            {
                int target = Math.Clamp((e.X - S(12)) / TabWidth, 0, tabs.Count - 1);
                int cur = tabs.IndexOf(dragTab);
                if (cur >= 0 && target != cur)
                {
                    tabs.RemoveAt(cur);
                    tabs.Insert(target, dragTab);
                    hover = target;
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
        hover = -1; hoverClose = hoverPlus = hoverAudio = false;
        Invalidate();
        base.OnMouseLeave(e);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        UpdateHover(e.Location);
        if (hover >= 0 && hover < tabs.Count)
        {
            var t = tabs[hover];
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
        if (hover < 0 && !hoverPlus && e.Button == MouseButtons.Left) NewTab?.Invoke();
        base.OnMouseDoubleClick(e);
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
            TextRenderer.DrawText(g, "Чтобы добавить закладку, нажмите ☆ в адресной строке или Ctrl+D", font,
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
