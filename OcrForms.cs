namespace LiteBrowser;

/// <summary>Выбор области на снимке страницы: затемнённый снимок поверх вкладки, тянем рамку мышью. Esc — отмена.</summary>
sealed class AreaSelectForm : Form
{
    readonly Bitmap shot;
    Point start; Rectangle sel; bool dragging;
    public Rectangle Selection { get; private set; }

    public AreaSelectForm(Bitmap shot, Rectangle screenBounds)
    {
        this.shot = shot;
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        Bounds = screenBounds;
        ShowInTaskbar = false;
        TopMost = true;
        DoubleBuffered = true;
        Cursor = Cursors.Cross;
        KeyPreview = true;
        KeyDown += (_, e) => { if (e.KeyCode == Keys.Escape) { DialogResult = DialogResult.Cancel; Close(); } };
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.DrawImage(shot, ClientRectangle);
        using var dim = new SolidBrush(Color.FromArgb(120, 0, 0, 0));
        var region = new Region(ClientRectangle);
        if (sel.Width > 0 && sel.Height > 0) region.Exclude(sel);
        g.FillRegion(dim, region);
        if (sel.Width > 0 && sel.Height > 0) { using var pen = new Pen(Theme.Accent, 2); g.DrawRectangle(pen, sel); }
        var hint = L.T("Выделите область с текстом · Esc — отмена");
        var sz = TextRenderer.MeasureText(hint, Theme.GetFont("Segoe UI", 10.5f));
        var r = new Rectangle((Width - sz.Width) / 2 - 14, 16, sz.Width + 28, sz.Height + 12);
        using var bg = new SolidBrush(Color.FromArgb(220, 20, 22, 30));
        using var path = Theme.Round(r, 10);
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        g.FillPath(bg, path);
        TextRenderer.DrawText(g, hint, Theme.GetFont("Segoe UI", 10.5f), r, Color.White, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
    }

    protected override void OnMouseDown(MouseEventArgs e) { if (e.Button == MouseButtons.Left) { dragging = true; start = e.Location; sel = Rectangle.Empty; } }
    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (!dragging) return;
        sel = Rectangle.FromLTRB(Math.Min(start.X, e.X), Math.Min(start.Y, e.Y), Math.Max(start.X, e.X), Math.Max(start.Y, e.Y));
        Invalidate();
    }
    protected override void OnMouseUp(MouseEventArgs e)
    {
        if (!dragging) return;
        dragging = false;
        if (sel.Width < 8 || sel.Height < 8) { sel = Rectangle.Empty; Invalidate(); return; }
        // Переводим из координат окна в пиксели снимка
        double kx = shot.Width / (double)ClientSize.Width, ky = shot.Height / (double)ClientSize.Height;
        Selection = new Rectangle((int)(sel.X * kx), (int)(sel.Y * ky), (int)(sel.Width * kx), (int)(sel.Height * ky));
        DialogResult = DialogResult.OK;
        Close();
    }
}

/// <summary>Результат распознавания: текст можно поправить и скопировать.</summary>
sealed class TextResultForm : Form
{
    public TextResultForm(string title, string text)
    {
        Text = title;
        Icon = Theme.MakeAppIcon();
        StartPosition = FormStartPosition.CenterParent;
        ShowInTaskbar = false;
        BackColor = Theme.Toolbar; ForeColor = Theme.Text;
        ClientSize = new Size(Theme.S(520), Theme.S(340));
        var box = new TextBox { Multiline = true, ScrollBars = ScrollBars.Vertical, Text = text.Replace("\n", "\r\n").Replace("\r\r", "\r"), BorderStyle = BorderStyle.FixedSingle,
                                BackColor = Theme.Field, ForeColor = Theme.Text, Font = Theme.GetFont("Segoe UI", 10.5f) };
        var copy = new Button { Text = L.T("Скопировать"), FlatStyle = FlatStyle.Flat, BackColor = Theme.Accent, ForeColor = Color.FromArgb(13, 16, 32), Font = Theme.GetFont("Segoe UI", 10f, FontStyle.Bold) };
        copy.FlatAppearance.BorderSize = 0;
        copy.Click += (_, _) => { if (box.Text.Length > 0) Clipboard.SetText(box.Text); Close(); };
        int pad = Theme.S(12), bh = Theme.S(36);
        box.SetBounds(pad, pad, ClientSize.Width - pad * 2, ClientSize.Height - pad * 3 - bh);
        copy.SetBounds(ClientSize.Width - pad - Theme.S(140), ClientSize.Height - pad - bh, Theme.S(140), bh);
        box.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
        copy.Anchor = AnchorStyles.Bottom | AnchorStyles.Right;
        Controls.AddRange(new Control[] { box, copy });
        KeyPreview = true;
        KeyDown += (_, e) => { if (e.KeyCode == Keys.Escape) Close(); };
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        try { Theme.StyleTitleBar(Handle, Theme.Toolbar); } catch { }
    }
}
