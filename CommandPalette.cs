using System.Drawing.Drawing2D;

namespace LiteBrowser;

/// <summary>Палитра команд (Ctrl+K): быстрый поиск по командам, вкладкам, настройкам, закладкам и истории.</summary>
sealed class CommandPalette : Form
{
    public sealed record Entry(string Kind, string Glyph, string Title, string Detail, Action Run, string Keys = "", string Extra = "");

    readonly TextBox box = new() { BorderStyle = BorderStyle.None };
    readonly Func<string, List<Entry>> source;
    List<Entry> items = new();
    int selected, hover = -1, scroll;
    bool closing;
    static int S(float v) => Theme.S(v);
    int RowH => S(40);
    int Top0 => S(58);
    const int MaxRows = 10;

    public CommandPalette(Func<string, List<Entry>> source)
    {
        this.source = source;
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        DoubleBuffered = true;
        KeyPreview = true;
        BackColor = Theme.Menu;
        box.BackColor = Theme.Menu;
        box.ForeColor = Theme.Text;
        box.Font = Theme.GetFont("Segoe UI", 12.5f);
        box.PlaceholderText = "Команда, вкладка, настройка, закладка или адрес…";
        Controls.Add(box);
        box.TextChanged += (_, _) => Refresh(box.Text);
        box.KeyDown += OnBoxKey;
        Deactivate += (_, _) => { if (!closing) Close(); };
    }

    protected override CreateParams CreateParams
    {
        get { var cp = base.CreateParams; cp.ExStyle |= 0x80; cp.ClassStyle |= 0x20000; return cp; }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        try { Theme.StylePopup(Handle); } catch { }
    }

    public void ShowOver(Form owner)
    {
        int w = Math.Min(S(640), owner.ClientSize.Width - S(40));
        var p = owner.PointToScreen(new Point((owner.ClientSize.Width - w) / 2, S(70)));
        Bounds = new Rectangle(p.X, p.Y, w, Top0 + S(10));
        box.SetBounds(S(46), S(18), w - S(62), S(26));
        Refresh("");
        Show(owner);
        Activate();
        box.Focus();
    }

    void Refresh(string q)
    {
        items = source(q.Trim());
        selected = 0;
        scroll = 0;
        int rows = Math.Min(MaxRows, items.Count);
        int h = Top0 + (rows == 0 ? S(36) : rows * RowH + S(8));
        if (Height != h) Height = h;
        Invalidate();
    }

    void OnBoxKey(object? sender, KeyEventArgs e)
    {
        switch (e.KeyCode)
        {
            case System.Windows.Forms.Keys.Escape: e.SuppressKeyPress = true; Close(); break;
            case System.Windows.Forms.Keys.Down: e.SuppressKeyPress = true; Move(1); break;
            case System.Windows.Forms.Keys.Up: e.SuppressKeyPress = true; Move(-1); break;
            case System.Windows.Forms.Keys.PageDown: e.SuppressKeyPress = true; Move(MaxRows); break;
            case System.Windows.Forms.Keys.PageUp: e.SuppressKeyPress = true; Move(-MaxRows); break;
            case System.Windows.Forms.Keys.Enter: e.SuppressKeyPress = true; Run(selected); break;
        }
    }

    void Move(int d)
    {
        if (items.Count == 0) return;
        selected = Math.Clamp(selected + d, 0, items.Count - 1);
        if (selected < scroll) scroll = selected;
        if (selected >= scroll + MaxRows) scroll = selected - MaxRows + 1;
        Invalidate();
    }

    void Run(int i)
    {
        if (i < 0 || i >= items.Count) return;
        var it = items[i];
        closing = true;
        Close();
        try { it.Run(); } catch { }
    }

    Rectangle RowRect(int visible) => new(S(6), Top0 + visible * RowH, Width - S(12), RowH);

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(BackColor);
        var icon = Theme.GetFont(Theme.IconFont, 11f);
        TextRenderer.DrawText(g, "", icon, new Rectangle(S(14), S(16), S(24), S(30)), Theme.Accent, Theme.Center);
        using (var pen = new Pen(Theme.Line)) g.DrawLine(pen, 0, Top0 - S(6), Width, Top0 - S(6));
        var title = Theme.GetFont("Segoe UI", 10f);
        var small = Theme.GetFont("Segoe UI", 8.5f);
        if (items.Count == 0)
        {
            TextRenderer.DrawText(g, "Ничего не найдено", small, new Rectangle(0, Top0, Width, S(30)), Theme.TextDim, Theme.Center);
            return;
        }
        for (int v = 0; v < MaxRows && scroll + v < items.Count; v++)
        {
            int i = scroll + v;
            var r = RowRect(v);
            if (i == selected || i == hover)
            {
                using var p = Theme.Round(r, S(8));
                using var b = new SolidBrush(i == selected ? Theme.Pressed : Theme.Hover);
                g.FillPath(b, p);
            }
            var it = items[i];
            TextRenderer.DrawText(g, it.Glyph, it.Glyph.Length == 1 && it.Glyph[0] >= '' ? icon : title,
                new Rectangle(r.X + S(8), r.Y, S(28), r.Height), it.Kind == "cmd" ? Theme.Accent : Theme.TextDim, Theme.Center);
            int tx = r.X + S(44);
            int right = r.Right - S(10);
            // Справа: сочетание клавиш или тип
            var tag = it.Keys.Length > 0 ? it.Keys : KindName(it.Kind);
            int tagW = TextRenderer.MeasureText(tag, small).Width;
            TextRenderer.DrawText(g, tag, small, new Rectangle(right - tagW, r.Y, tagW, r.Height), Theme.TextDim,
                TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix);
            int avail = right - tagW - S(14) - tx;
            int titleW = Math.Min(TextRenderer.MeasureText(it.Title, title).Width, it.Detail.Length > 0 ? avail * 2 / 3 : avail);
            TextRenderer.DrawText(g, it.Title, title, new Rectangle(tx, r.Y, titleW, r.Height), Theme.Text,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix);
            if (it.Detail.Length > 0)
                TextRenderer.DrawText(g, it.Detail, small, new Rectangle(tx + titleW + S(10), r.Y, avail - titleW - S(10), r.Height), Theme.TextDim,
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix);
        }
        if (items.Count > MaxRows)
        {
            // Полоса прокрутки
            int track = MaxRows * RowH, th = Math.Max(S(20), track * MaxRows / items.Count);
            int ty = Top0 + (track - th) * scroll / Math.Max(1, items.Count - MaxRows);
            using var sb = new SolidBrush(Theme.Line);
            using var sp = Theme.Round(new Rectangle(Width - S(5), ty, S(3), th), S(2));
            g.FillPath(sb, sp);
        }
    }

    static string KindName(string k) => k switch
    {
        "cmd" => "команда", "tab" => "вкладка", "set" => "настройка", "bm" => "закладка", "hist" => "история", "go" => "", "page" => "страница", _ => "",
    };

    protected override void OnMouseMove(MouseEventArgs e)
    {
        int h = -1;
        for (int v = 0; v < MaxRows && scroll + v < items.Count; v++) if (RowRect(v).Contains(e.Location)) h = scroll + v;
        Cursor = h >= 0 ? Cursors.Hand : Cursors.Default;
        if (h != hover) { hover = h; Invalidate(); }
        base.OnMouseMove(e);
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        scroll = Math.Clamp(scroll - Math.Sign(e.Delta) * 3, 0, Math.Max(0, items.Count - MaxRows));
        Invalidate();
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Left && hover >= 0) Run(hover);
        base.OnMouseDown(e);
    }

    /// <summary>Нечёткое совпадение: подстрока лучше, начало слова лучше, буквы по порядку — хуже всего. 0 — не подходит.</summary>
    public static int Score(string text, string q)
    {
        if (q.Length == 0) return 1;
        var t = text.ToLowerInvariant();
        q = q.ToLowerInvariant();
        int i = t.IndexOf(q, StringComparison.Ordinal);
        if (i == 0) return 1000 - t.Length;
        if (i > 0) return (char.IsLetterOrDigit(t[i - 1]) ? 500 : 800) - i;
        // Все слова запроса встречаются
        var words = q.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length > 1 && words.All(w => t.Contains(w, StringComparison.Ordinal))) return 400;
        // Буквы по порядку (например «нвк» → «новая вкладка»)
        int k = 0, gaps = 0, last = -1;
        for (int j = 0; j < t.Length && k < q.Length; j++)
            if (t[j] == q[k]) { if (last >= 0 && j - last > 1) gaps++; last = j; k++; }
        return k == q.Length && q.Length >= 2 ? Math.Max(1, 200 - gaps * 20) : 0;
    }
}
