using System.Drawing.Drawing2D;
using System.Text.Json;

namespace LiteBrowser;

/// <summary>Панель поиска по странице (Ctrl+F).</summary>
sealed class FindBar : Panel
{
    public readonly TextBox Box = new() { BorderStyle = BorderStyle.None };
    readonly Label count = new() { AutoSize = false, TextAlign = ContentAlignment.MiddleRight };
    readonly IconButton up, down, close;
    readonly System.Windows.Forms.Timer debounce = new() { Interval = 150 };

    public event Action<string>? Search;
    public event Action? Next, Previous, Closed;

    public FindBar(ToolTip tips)
    {
        Visible = false;
        DoubleBuffered = true;
        up = new IconButton("", "Предыдущее (Shift+Enter)", tips);
        down = new IconButton("", "Следующее (Enter)", tips);
        close = new IconButton("", "Закрыть (Esc)", tips);
        up.Click += (_, _) => Previous?.Invoke();
        down.Click += (_, _) => Next?.Invoke();
        close.Click += (_, _) => Closed?.Invoke();
        Box.TextChanged += (_, _) => { debounce.Stop(); debounce.Start(); };
        debounce.Tick += (_, _) => { debounce.Stop(); Search?.Invoke(Box.Text); };
        Box.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; if (e.Shift) Previous?.Invoke(); else Next?.Invoke(); }
            else if (e.KeyCode == Keys.Escape) { e.SuppressKeyPress = true; Closed?.Invoke(); }
        };
        Controls.AddRange(new Control[] { Box, count, up, down, close });
        Resize += (_, _) => DoLayout();
    }

    public void SetCount(int active, int total) =>
        count.Text = Box.Text.Length == 0 ? "" : total == 0 ? "Нет совпадений" : $"{active} из {total}";

    public void ApplyTheme()
    {
        BackColor = Theme.Menu;
        Box.BackColor = Theme.Field;
        Box.ForeColor = Theme.Text;
        Box.Font = Theme.GetFont("Segoe UI", 10f);
        count.Font = Theme.GetFont("Segoe UI", 8.5f);
        count.ForeColor = Theme.TextDim;
        count.BackColor = Theme.Menu;
        foreach (var b in new[] { up, down, close }) b.BackColor = Theme.Menu;
        Size = new Size(Theme.S(400), Theme.S(46));
        DoLayout();
        Invalidate();
    }

    void DoLayout()
    {
        int h = Height, s = Theme.S(32), y = (h - s) / 2;
        close.SetBounds(Width - s - Theme.S(8), y, s, s);
        down.SetBounds(close.Left - s, y, s, s);
        up.SetBounds(down.Left - s, y, s, s);
        count.SetBounds(up.Left - Theme.S(110), 0, Theme.S(104), h);
        int bh = Box.PreferredHeight;
        Box.SetBounds(Theme.S(18), (h - bh) / 2, Math.Max(20, count.Left - Theme.S(24)), bh);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using var pen = new Pen(Theme.Accent, Theme.S(1.5f));
        g.DrawRectangle(pen, 0, 0, Width - 1, Height - 1);
        using var field = new SolidBrush(Theme.Field);
        g.FillRectangle(field, Theme.S(10), Theme.S(8), count.Left - Theme.S(4), Height - Theme.S(16));
    }
}

/// <summary>Выпадающий список подсказок под адресной строкой. Не забирает фокус у поля ввода.</summary>
sealed class SuggestPopup : Form
{
    public sealed record Item(string Glyph, string Title, string Detail, string Url);

    List<Item> items = new();
    int selected = -1, hover = -1;
    public event Action<Item>? Chosen;
    static int S(float v) => Theme.S(v);
    int RowH => S(40);

    public SuggestPopup()
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        DoubleBuffered = true;
    }

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= 0x08000000 | 0x80; // WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW
            cp.ClassStyle |= 0x20000;        // тень
            return cp;
        }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        try { Theme.StylePopup(Handle); } catch { }
    }

    public Item? Selected => selected >= 0 && selected < items.Count ? items[selected] : null;

    public void ShowItems(Control anchor, List<Item> list)
    {
        items = list;
        selected = -1;
        hover = -1;
        if (items.Count == 0) { Hide(); return; }
        BackColor = Theme.Menu;
        var p = anchor.PointToScreen(new Point(0, anchor.Height + S(4)));
        Bounds = new Rectangle(p.X, p.Y, anchor.Width, items.Count * RowH + S(10));
        if (!Visible) Show(anchor.FindForm());
        Invalidate();
    }

    public void Step(int dir)
    {
        if (items.Count == 0) return;
        selected = selected < 0 ? (dir > 0 ? 0 : items.Count - 1) : (selected + dir + items.Count) % items.Count;
        Invalidate();
    }

    Rectangle RowRect(int i) => new(S(5), S(5) + i * RowH, Width - S(10), RowH);

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(BackColor);
        var icon = Theme.GetFont(Theme.IconFont, 10f);
        var title = Theme.GetFont("Segoe UI", 10f);
        var detail = Theme.GetFont("Segoe UI", 8.5f);
        for (int i = 0; i < items.Count; i++)
        {
            var r = RowRect(i);
            if (i == selected || i == hover)
            {
                using var p = Theme.Round(r, S(8));
                using var b = new SolidBrush(i == selected ? Theme.Pressed : Theme.Hover);
                g.FillPath(b, p);
            }
            var it = items[i];
            TextRenderer.DrawText(g, it.Glyph, icon, new Rectangle(r.X + S(8), r.Y, S(26), r.Height), Theme.TextDim, Theme.Center);
            int tx = r.X + S(42);
            int titleW = Math.Min(TextRenderer.MeasureText(it.Title, title).Width, (r.Width - S(50)) * 2 / 3);
            TextRenderer.DrawText(g, it.Title, title, new Rectangle(tx, r.Y, titleW, r.Height), Theme.Text,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix);
            if (it.Detail.Length > 0)
                TextRenderer.DrawText(g, "— " + it.Detail, detail, new Rectangle(tx + titleW + S(8), r.Y, r.Right - tx - titleW - S(16), r.Height), Theme.Accent,
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix);
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        int h = -1;
        for (int i = 0; i < items.Count; i++) if (RowRect(i).Contains(e.Location)) h = i;
        Cursor = h >= 0 ? Cursors.Hand : Cursors.Default;
        if (h != hover) { hover = h; Invalidate(); }
        base.OnMouseMove(e);
    }

    protected override void OnMouseLeave(EventArgs e) { hover = -1; Invalidate(); base.OnMouseLeave(e); }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Left && hover >= 0 && hover < items.Count) Chosen?.Invoke(items[hover]);
        base.OnMouseDown(e);
    }
}

/// <summary>Подсказки от поисковика (только если пользователь их включил — запросы уходят поисковику по мере ввода).</summary>
static class RemoteSuggest
{
    static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(3) };

    static string? Endpoint(string engine, string q) => engine switch
    {
        "duckduckgo" => "https://duckduckgo.com/ac/?type=list&q=" + Uri.EscapeDataString(q),
        "google" => "https://suggestqueries.google.com/complete/search?client=firefox&ie=utf-8&oe=utf-8&hl=ru&q=" + Uri.EscapeDataString(q),
        "yandex" => "https://suggest.yandex.ru/suggest-ff.cgi?part=" + Uri.EscapeDataString(q),
        "bing" => "https://api.bing.com/osjson.aspx?query=" + Uri.EscapeDataString(q),
        "brave" => "https://search.brave.com/api/suggest?q=" + Uri.EscapeDataString(q),
        _ => null,
    };

    public static async Task<List<string>> Get(string engine, string q, CancellationToken ct)
    {
        var url = Endpoint(engine, q);
        if (url == null) return new();
        try
        {
            var json = await Http.GetStringAsync(url, ct);
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array || doc.RootElement.GetArrayLength() < 2) return new();
            return doc.RootElement[1].EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!).Take(8).ToList();
        }
        catch { return new(); }
    }
}
