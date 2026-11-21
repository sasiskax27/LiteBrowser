using System.Drawing.Drawing2D;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace LiteBrowser;

/// <summary>Одна вкладка браузера.</summary>
sealed class BrowserTab
{
    public required WebView2 View { get; init; }
    public CoreWebView2? Core => View.CoreWebView2;
    public string Title { get; set; } = "Новая вкладка";
    public Image? Icon { get; set; }
    public bool Loading { get; set; }
    public bool IsHome { get; set; }
    public int Blocked { get; set; }
}

/// <summary>Круглая кнопка-иконка.</summary>
sealed class IconButton : Control
{
    bool hover, down;
    string glyph;

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
            g.FillEllipse(b, 1, 1, Width - 3, Height - 3);
        }
        var color = Enabled ? Theme.Text : Color.FromArgb(78, 82, 96);
        TextRenderer.DrawText(g, glyph, Font, ClientRectangle, color, Theme.Center);
    }
}

/// <summary>Адресная строка в виде «таблетки» с замком и счётчиком блокировок.</summary>
sealed class AddressBox : Control
{
    public readonly TextBox Box = new() { BorderStyle = BorderStyle.None };
    readonly Font iconFont = new(Theme.IconFont, 9.5f);
    readonly Font smallFont = new("Segoe UI", 8.5f, FontStyle.Bold);
    string lockGlyph = "";
    Color lockColor = Theme.TextDim;
    int blocked;

    public AddressBox()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                 ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        BackColor = Theme.Toolbar;
        Box.BackColor = Theme.Field;
        Box.ForeColor = Theme.Text;
        Box.Font = new Font("Segoe UI", 10.5f);
        Controls.Add(Box);
        Box.GotFocus += (_, _) => Invalidate();
        Box.LostFocus += (_, _) => Invalidate();
        Cursor = Cursors.IBeam;
    }

    protected override void OnMouseDown(MouseEventArgs e) { Box.Focus(); Box.SelectAll(); base.OnMouseDown(e); }

    public void SetLock(string glyph, Color color)
    {
        if (glyph == lockGlyph && color == lockColor) return;
        lockGlyph = glyph; lockColor = color; Invalidate();
    }

    public void SetBlocked(int n)
    {
        if (n == blocked) return;
        blocked = n; Invalidate();
    }

    protected override void OnLayout(LayoutEventArgs e)
    {
        base.OnLayout(e);
        int x = Theme.S(40), bh = Box.PreferredHeight;
        Box.SetBounds(x, (Height - bh) / 2 + Theme.S(1), Math.Max(10, Width - x - Theme.S(76)), bh);
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

        if (blocked > 0)
        {
            var text = blocked.ToString();
            int tw = TextRenderer.MeasureText(text, smallFont, Size.Empty, TextFormatFlags.NoPadding).Width;
            int w = tw + Theme.S(30), h = Theme.S(22);
            var pill = new RectangleF(Width - w - Theme.S(7), (Height - h) / 2f, w, h);
            using var path = Theme.Round(pill, h / 2f);
            using var b = new SolidBrush(Color.FromArgb(44, 124, 156, 255));
            g.FillPath(b, path);
            TextRenderer.DrawText(g, "", iconFont, new Rectangle((int)pill.X + Theme.S(6), (int)pill.Y, Theme.S(16), h), Theme.Accent, Theme.Center);
            TextRenderer.DrawText(g, text, smallFont, new Rectangle((int)pill.X + Theme.S(23), (int)pill.Y, tw + Theme.S(2), h), Theme.Accent,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
        }
    }
}

/// <summary>Полоса вкладок, нарисованная вручную.</summary>
sealed class TabStrip : Control
{
    readonly List<BrowserTab> tabs;
    readonly Font titleFont = new("Segoe UI", 9f);
    readonly Font iconFont = new(Theme.IconFont, 10f);
    readonly Font smallIcon = new(Theme.IconFont, 7f);
    BrowserTab? selected;
    int hover = -1;
    bool hoverClose, hoverPlus;

    public float Spin;
    public event Action<BrowserTab>? TabSelected;
    public event Action<BrowserTab>? TabClosed;
    public event Action? NewTab;

    public TabStrip(List<BrowserTab> tabs)
    {
        this.tabs = tabs;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                 ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        BackColor = Theme.Frame;
    }

    public BrowserTab? Selected { get => selected; set { selected = value; Invalidate(); } }

    static int S(float v) => Theme.S(v);
    int Top0 => S(8);
    int TabWidth => Math.Clamp((Width - S(12) - S(48)) / Math.Max(1, tabs.Count), S(44), S(236));
    Rectangle TabRect(int i) => new(S(12) + i * TabWidth, Top0, TabWidth, Height - Top0);
    Rectangle CloseRect(Rectangle r) => new(r.Right - S(30), r.Y + (r.Height - S(22)) / 2, S(22), S(22));
    bool ShowClose(int i) => tabs[i] == selected || TabWidth >= S(100);

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
        g.Clear(Theme.Frame);
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
                using var b = new SolidBrush(Theme.TabHover);
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

            bool close = ShowClose(i);
            int tx = ix + S(26), tr = close ? CloseRect(r).X - S(4) : r.Right - S(10);
            if (tr > tx)
                TextRenderer.DrawText(g, t.Title, titleFont, new Rectangle(tx, r.Y, tr - tx, r.Height - S(4)),
                    isSel ? Theme.Text : Theme.TextDim,
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis |
                    TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding);

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
    }

    void UpdateHover(Point p)
    {
        int h = -1;
        bool hc = false;
        for (int i = 0; i < tabs.Count; i++)
        {
            var r = TabRect(i);
            if (!r.Contains(p)) continue;
            h = i;
            hc = ShowClose(i) && CloseRect(r).Contains(p);
            break;
        }
        bool hp = PlusRect.Contains(p);
        if (h == hover && hc == hoverClose && hp == hoverPlus) return;
        hover = h; hoverClose = hc; hoverPlus = hp;
        Invalidate();
    }

    protected override void OnMouseMove(MouseEventArgs e) { UpdateHover(e.Location); base.OnMouseMove(e); }

    protected override void OnMouseLeave(EventArgs e)
    {
        hover = -1; hoverClose = hoverPlus = false;
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
            else if (e.Button == MouseButtons.Left) TabSelected?.Invoke(t);
        }
        else if (hoverPlus && e.Button == MouseButtons.Left) NewTab?.Invoke();
        if (!IsDisposed) UpdateHover(PointToClient(Cursor.Position));
        base.OnMouseDown(e);
    }

    protected override void OnMouseDoubleClick(MouseEventArgs e)
    {
        if (hover < 0 && !hoverPlus && e.Button == MouseButtons.Left) NewTab?.Invoke();
        base.OnMouseDoubleClick(e);
    }
}
