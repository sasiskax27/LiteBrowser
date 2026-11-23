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
    DateTime shownAt;
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
        box.PlaceholderText = L.T("Команда, вкладка, настройка, закладка или адрес…");
        Controls.Add(box);
        box.TextChanged += (_, _) => Refresh(box.Text);
        box.KeyDown += OnBoxKey;
        Deactivate += (_, _) =>
        {
            if (closing) return;
            // Сразу после открытия из страницы WebView2 может забрать фокус обратно — возвращаем его палитре
            if ((DateTime.Now - shownAt).TotalMilliseconds < 400) { BeginInvoke(() => { if (!IsDisposed) { Activate(); box.Focus(); } }); return; }
            Close();
        };
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
        shownAt = DateTime.Now;
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
            case System.Windows.Forms.Keys.Down: e.SuppressKeyPress = true; MoveSelection(1); break;
            case System.Windows.Forms.Keys.Up: e.SuppressKeyPress = true; MoveSelection(-1); break;
            case System.Windows.Forms.Keys.PageDown: e.SuppressKeyPress = true; MoveSelection(MaxRows); break;
            case System.Windows.Forms.Keys.PageUp: e.SuppressKeyPress = true; MoveSelection(-MaxRows); break;
            case System.Windows.Forms.Keys.Enter: e.SuppressKeyPress = true; Run(selected); break;
        }
    }

    void MoveSelection(int d)
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
        // Разделитель под строкой ввода — градиент акцента, гаснущий к краям
        var sepR = new Rectangle(0, Top0 - S(7), Width, S(2));
        using (var sep = new LinearGradientBrush(sepR, Color.Transparent, Color.Transparent, 0f))
        {
            sep.InterpolationColors = new ColorBlend
            {
                Colors = new[] { Color.FromArgb(0, Theme.Accent), Color.FromArgb(Theme.Neon ? 230 : 150, Theme.Accent), Color.FromArgb(Theme.Neon ? 230 : 150, Theme.Accent2), Color.FromArgb(0, Theme.Accent2) },
                Positions = new[] { 0f, .25f, .75f, 1f },
            };
            g.FillRectangle(sep, sepR.X, sepR.Y, sepR.Width, Theme.Neon ? S(2) : 1);
        }
        var title = Theme.GetFont("Segoe UI", 10f);
        var small = Theme.GetFont("Segoe UI", 8.5f);
        if (items.Count == 0)
        {
            TextRenderer.DrawText(g, L.T("Ничего не найдено"), small, new Rectangle(0, Top0, Width, S(30)), Theme.TextDim, Theme.Center);
            return;
        }
        for (int v = 0; v < MaxRows && scroll + v < items.Count; v++)
        {
            int i = scroll + v;
            var r = RowRect(v);
            Theme.PaintRow(g, r, i == selected, i == hover, BackColor);
            var it = items[i];
            TextRenderer.DrawText(g, it.Glyph, it.Glyph.Length == 1 && it.Glyph[0] >= '' ? icon : title,
                new Rectangle(r.X + S(8), r.Y, S(28), r.Height), it.Kind == "cmd" ? Theme.Accent : Theme.TextDim, Theme.Center);
            int tx = r.X + S(44);
            int right = r.Right - S(10);
            // Справа: сочетание клавиш или тип
            var tag = it.Keys.Length > 0 ? L.T(it.Keys) : L.T(KindName(it.Kind));
            var titleText = L.T(it.Title);
            var detailText = L.T(it.Detail);
            int tagW = tag.Length == 0 ? 0 : TextRenderer.MeasureText(tag, small, Size.Empty, TextFormatFlags.NoPadding).Width + S(14);
            if (tagW > 0)
            {
                // Сочетание клавиш или тип — в «таблетке»
                var tr = new Rectangle(right - tagW, r.Y + (r.Height - S(20)) / 2, tagW, S(20));
                using (var tp = Theme.Round(tr, S(6)))
                using (var tb = new SolidBrush(Theme.Blend(BackColor, Theme.Text, .08f)))
                    g.FillPath(tb, tp);
                TextRenderer.DrawText(g, tag, small, tr, i == selected ? Theme.Text : Theme.TextDim, Theme.Center | TextFormatFlags.NoPrefix);
            }
            int avail = right - tagW - S(14) - tx;
            int titleW = Math.Min(TextRenderer.MeasureText(titleText, title).Width, detailText.Length > 0 ? avail * 2 / 3 : avail);
            TextRenderer.DrawText(g, titleText, title, new Rectangle(tx, r.Y, titleW, r.Height), Theme.Text,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix);
            if (detailText.Length > 0)
                TextRenderer.DrawText(g, detailText, small, new Rectangle(tx + titleW + S(10), r.Y, avail - titleW - S(10), r.Height), Theme.TextDim,
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
