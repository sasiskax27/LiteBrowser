using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

namespace LiteBrowser;

/// <summary>
/// Редактор скриншота: обрезка, стрелка, рамка, маркер, текст и размытие (пикселизация) личных данных.
/// Результат — скопировать в буфер обмена или сохранить.
/// </summary>
sealed class ScreenshotEditor : Form
{
    enum Tool { Arrow, Rect, Pen, Text, Blur, Crop }

    abstract record Shape(Color Color);
    sealed record ArrowShape(Color Color, Point A, Point B) : Shape(Color);
    sealed record RectShape(Color Color, Rectangle R) : Shape(Color);
    sealed record PenShape(Color Color, List<Point> Points) : Shape(Color);
    sealed record TextShape(Color Color, Point At, string Text) : Shape(Color);
    sealed record BlurShape(Rectangle R) : Shape(Color.Empty);

    readonly string path;
    Bitmap image;
    readonly List<Shape> shapes = new();
    readonly Stack<(Bitmap img, List<Shape> shapes)> undo = new();
    Tool tool = Tool.Arrow;
    Color color = Color.FromArgb(255, 80, 80);
    Point? dragStart;
    Point dragNow;
    PenShape? drawing;
    float zoom = 1f;
    readonly BufferedPanel canvas = new() { Dock = DockStyle.Fill };
    readonly FlowLayoutPanel bar = new() { Dock = DockStyle.Top, WrapContents = false, AutoScroll = false };
    readonly Dictionary<Tool, Button> toolButtons = new();
    static int S(float v) => Theme.S(v);

    public ScreenshotEditor(string path)
    {
        this.path = path;
        using (var src = Image.FromFile(path)) image = new Bitmap(src);
        Text = L.T("Редактор скриншота") + " — " + Path.GetFileName(path);
        StartPosition = FormStartPosition.CenterParent;
        BackColor = Theme.Frame;
        ForeColor = Theme.Text;
        KeyPreview = true;
        var wa = Screen.PrimaryScreen!.WorkingArea;
        Size = new Size(Math.Min(wa.Width - S(80), Math.Max(S(820), image.Width + S(60))), Math.Min(wa.Height - S(80), Math.Max(S(560), image.Height + S(140))));
        bar.BackColor = Theme.Toolbar;
        bar.Height = S(48);
        bar.Padding = new Padding(S(8), S(7), S(8), 0);

        AddTool(Tool.Arrow, "", "Стрелка");
        AddTool(Tool.Rect, "", "Рамка");
        AddTool(Tool.Pen, "", "Маркер");
        AddTool(Tool.Text, "", "Текст");
        AddTool(Tool.Blur, "", "Размыть (скрыть личное)");
        AddTool(Tool.Crop, "", "Обрезать");
        bar.Controls.Add(new Label { Width = S(12) });
        foreach (var c in new[] { Color.FromArgb(255, 80, 80), Color.FromArgb(255, 196, 0), Color.FromArgb(70, 200, 120), Color.FromArgb(80, 150, 255), Color.White, Color.Black })
        {
            var cc = c;
            var b = new Button { Width = S(26), Height = S(26), BackColor = c, FlatStyle = FlatStyle.Flat, Margin = new Padding(S(3), S(4), S(3), 0), Cursor = Cursors.Hand };
            b.FlatAppearance.BorderColor = Theme.Line;
            b.Click += (_, _) => { color = cc; UpdateButtons(); };
            b.Tag = c;
            bar.Controls.Add(b);
        }
        bar.Controls.Add(new Label { Width = S(12) });
        AddAction("", "Отменить (Ctrl+Z)", Undo);
        AddAction("", "Копировать (Ctrl+C)", CopyOut, true);
        AddAction("", "Сохранить (Ctrl+S)", SaveOut, true);

        canvas.BackColor = Theme.Frame;
        canvas.Paint += PaintCanvas;
        canvas.MouseDown += CanvasDown;
        canvas.MouseMove += CanvasMove;
        canvas.MouseUp += CanvasUp;
        canvas.Resize += (_, _) => { FitZoom(); canvas.Invalidate(); };
        Controls.Add(canvas);
        Controls.Add(bar);
        KeyDown += (_, e) =>
        {
            if (e.Control && e.KeyCode == Keys.Z) { Undo(); e.Handled = true; }
            else if (e.Control && e.KeyCode == Keys.C) { CopyOut(); e.Handled = true; }
            else if (e.Control && e.KeyCode == Keys.S) { SaveOut(); e.Handled = true; }
            else if (e.KeyCode == Keys.Escape) Close();
        };
        UpdateButtons();
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        try { Theme.StyleTitleBar(Handle, Theme.Toolbar); } catch { }
    }

    void AddTool(Tool t, string glyph, string tip)
    {
        var b = MakeButton(glyph, tip);
        b.Click += (_, _) => { tool = t; UpdateButtons(); };
        toolButtons[t] = b;
        bar.Controls.Add(b);
    }

    void AddAction(string glyph, string tip, Action act, bool withText = false)
    {
        var b = MakeButton(glyph, tip);
        if (withText)
        {
            // Кнопка с подписью: «Копировать», «Сохранить»
            b.Text = L.T(tip.Split(' ')[0]);
            b.Font = Theme.GetFont("Segoe UI", 9.5f);
            b.Width = S(104);
            b.BackColor = Theme.Hover;
        }
        b.Click += (_, _) => act();
        bar.Controls.Add(b);
    }

    readonly ToolTip tips = new();

    Button MakeButton(string glyph, string tip)
    {
        var b = new Button
        {
            Text = glyph, Width = S(36), Height = S(34), FlatStyle = FlatStyle.Flat, Font = Theme.GetFont(Theme.IconFont, 11f),
            ForeColor = Theme.Text, BackColor = Theme.Toolbar, Margin = new Padding(S(2), 0, S(2), 0), Cursor = Cursors.Hand,
        };
        b.FlatAppearance.BorderSize = 0;
        b.FlatAppearance.MouseOverBackColor = Theme.Hover;
        tips.SetToolTip(b, L.T(tip));
        return b;
    }

    void UpdateButtons()
    {
        foreach (var (t, b) in toolButtons) b.BackColor = t == tool ? Theme.Pressed : Theme.Toolbar;
        foreach (Control c in bar.Controls)
            if (c is Button b && b.Tag is Color col) b.FlatAppearance.BorderSize = col == color ? S(3) : 1;
        canvas.Cursor = tool == Tool.Text ? Cursors.IBeam : Cursors.Cross;
    }

    // ---- Координаты: картинка по центру холста, с масштабом ----
    void FitZoom() => zoom = Math.Min(1f, Math.Min((canvas.Width - S(24)) / (float)image.Width, (canvas.Height - S(24)) / (float)image.Height));
    Rectangle ImageRect()
    {
        int w = (int)(image.Width * zoom), h = (int)(image.Height * zoom);
        return new Rectangle((canvas.Width - w) / 2, (canvas.Height - h) / 2, w, h);
    }
    Point ToImage(Point p)
    {
        var r = ImageRect();
        return new Point(Math.Clamp((int)((p.X - r.X) / zoom), 0, image.Width), Math.Clamp((int)((p.Y - r.Y) / zoom), 0, image.Height));
    }
    static Rectangle Norm(Point a, Point b) => Rectangle.FromLTRB(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Max(a.X, b.X), Math.Max(a.Y, b.Y));

    void PaintCanvas(object? s, PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Theme.Frame);
        if (zoom <= 0) FitZoom();
        var r = ImageRect();
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        using (var composed = Render(includeTemp: true)) g.DrawImage(composed, r);
        if (dragStart is { } a && tool == Tool.Crop)
        {
            var cr = Norm(a, dragNow);
            var sr = new Rectangle(r.X + (int)(cr.X * zoom), r.Y + (int)(cr.Y * zoom), (int)(cr.Width * zoom), (int)(cr.Height * zoom));
            using var dim = new SolidBrush(Color.FromArgb(140, 0, 0, 0));
            using var region = new Region(r);
            region.Exclude(sr);
            g.FillRegion(dim, region);
            using var pen = new Pen(Color.White, 1.5f) { DashStyle = DashStyle.Dash };
            g.DrawRectangle(pen, sr);
        }
    }

    /// <summary>Картинка со всеми пометками (и рисуемой сейчас, если includeTemp).</summary>
    Bitmap Render(bool includeTemp)
    {
        var bmp = new Bitmap(image);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var list = shapes.ToList();
        if (includeTemp && dragStart is { } a && dragNow != a)
        {
            switch (tool)
            {
                case Tool.Arrow: list.Add(new ArrowShape(color, a, dragNow)); break;
                case Tool.Rect: list.Add(new RectShape(color, Norm(a, dragNow))); break;
                case Tool.Blur: list.Add(new BlurShape(Norm(a, dragNow))); break;
            }
        }
        if (includeTemp && drawing != null) list.Add(drawing);
        float wpx = Math.Max(3f, image.Width / 400f);
        foreach (var sh in list) DrawShape(g, bmp, sh, wpx);
        return bmp;
    }

    void DrawShape(Graphics g, Bitmap bmp, Shape sh, float wpx)
    {
        switch (sh)
        {
            case ArrowShape ar:
                using (var pen = new Pen(ar.Color, wpx) { StartCap = LineCap.Round })
                {
                    pen.CustomEndCap = new AdjustableArrowCap(4, 4, true);
                    g.DrawLine(pen, ar.A, ar.B);
                }
                break;
            case RectShape rs:
                using (var pen = new Pen(rs.Color, wpx)) g.DrawRectangle(pen, rs.R);
                break;
            case PenShape ps when ps.Points.Count > 1:
                using (var pen = new Pen(Color.FromArgb(150, ps.Color), wpx * 3.5f) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round })
                    g.DrawLines(pen, ps.Points.ToArray());
                break;
            case TextShape ts:
                using (var f = new Font("Segoe UI", Math.Max(14f, image.Width / 70f), FontStyle.Bold, GraphicsUnit.Pixel))
                using (var shadow = new SolidBrush(Color.FromArgb(160, 0, 0, 0)))
                using (var br = new SolidBrush(ts.Color))
                {
                    g.DrawString(ts.Text, f, shadow, ts.At.X + 2, ts.At.Y + 2);
                    g.DrawString(ts.Text, f, br, ts.At);
                }
                break;
            case BlurShape bs:
                Pixelate(bmp, Rectangle.Intersect(bs.R, new Rectangle(0, 0, bmp.Width, bmp.Height)));
                break;
        }
    }

    /// <summary>Пикселизация: крупные квадраты — текст под ними прочитать нельзя.</summary>
    static void Pixelate(Bitmap bmp, Rectangle r)
    {
        if (r.Width < 2 || r.Height < 2) return;
        int block = Math.Max(10, Math.Min(r.Width, r.Height) / 6);
        using var g = Graphics.FromImage(bmp);
        for (int y = r.Top; y < r.Bottom; y += block)
            for (int x = r.Left; x < r.Right; x += block)
            {
                var c = bmp.GetPixel(Math.Min(x + block / 2, r.Right - 1), Math.Min(y + block / 2, r.Bottom - 1));
                using var b = new SolidBrush(c);
                g.FillRectangle(b, x, y, Math.Min(block, r.Right - x), Math.Min(block, r.Bottom - y));
            }
    }

    void PushUndo() => undo.Push((new Bitmap(image), shapes.ToList()));

    void Undo()
    {
        if (undo.Count == 0) return;
        var (img, sh) = undo.Pop();
        image.Dispose();
        image = img;
        shapes.Clear();
        shapes.AddRange(sh);
        FitZoom();
        canvas.Invalidate();
    }

    void CanvasDown(object? s, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left) return;
        var p = ToImage(e.Location);
        if (tool == Tool.Text)
        {
            var text = InputDialog.Ask(this, L.T("Текст на скриншоте"), "");
            if (string.IsNullOrWhiteSpace(text)) return;
            PushUndo();
            shapes.Add(new TextShape(color, p, text));
            canvas.Invalidate();
            return;
        }
        dragStart = p;
        dragNow = p;
        if (tool == Tool.Pen) drawing = new PenShape(color, new List<Point> { p });
    }

    void CanvasMove(object? s, MouseEventArgs e)
    {
        if (dragStart == null) return;
        dragNow = ToImage(e.Location);
        drawing?.Points.Add(dragNow);
        canvas.Invalidate();
    }

    void CanvasUp(object? s, MouseEventArgs e)
    {
        if (dragStart is not { } a) return;
        var b = ToImage(e.Location);
        dragStart = null;
        var r = Norm(a, b);
        switch (tool)
        {
            case Tool.Arrow when a != b: PushUndo(); shapes.Add(new ArrowShape(color, a, b)); break;
            case Tool.Rect when r.Width > 2: PushUndo(); shapes.Add(new RectShape(color, r)); break;
            case Tool.Blur when r.Width > 2: PushUndo(); shapes.Add(new BlurShape(r)); break;
            case Tool.Pen when drawing != null: PushUndo(); shapes.Add(drawing); break;
            case Tool.Crop when r.Width > 10 && r.Height > 10:
                PushUndo();
                // Сначала «впекаем» пометки, потом обрезаем
                using (var composed = Render(includeTemp: false))
                {
                    var cut = composed.Clone(r, PixelFormat.Format32bppArgb);
                    image.Dispose();
                    image = cut;
                }
                shapes.Clear();
                FitZoom();
                break;
        }
        drawing = null;
        canvas.Invalidate();
    }

    void CopyOut()
    {
        using var bmp = Render(includeTemp: false);
        try { Clipboard.SetImage(bmp); Text = L.T("Скопировано в буфер обмена") + " — " + Path.GetFileName(path); } catch { }
    }

    void SaveOut()
    {
        using var bmp = Render(includeTemp: false);
        try
        {
            bmp.Save(path, ImageFormat.Png);
            Text = L.T("Сохранено") + " — " + Path.GetFileName(path);
        }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, L.T("Редактор скриншота")); }
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        image.Dispose();
        foreach (var (img, _) in undo) img.Dispose();
        base.OnFormClosed(e);
    }
}
