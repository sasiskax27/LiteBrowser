using System.Diagnostics;
using System.Drawing.Drawing2D;
using Microsoft.Web.WebView2.Core;

namespace LiteBrowser;

sealed class DownloadItem
{
    public required CoreWebView2DownloadOperation Op { get; init; }
    public string Path { get; set; } = "";
    public string Name => System.IO.Path.GetFileName(Path);
    public long Received { get; set; }
    public long Total { get; set; }
    public CoreWebView2DownloadState State { get; set; }
    public bool Canceled { get; set; }
    public double Speed { get; set; }
    long lastBytes;
    DateTime lastTime = DateTime.Now;

    static long L(object? v) { try { return v == null ? 0 : Convert.ToInt64(v); } catch { return 0; } }

    public void Update()
    {
        Received = L(Op.BytesReceived);
        Total = L(Op.TotalBytesToReceive);
        State = Op.State;
        Path = Op.ResultFilePath;
        Canceled = State == CoreWebView2DownloadState.Interrupted && Op.InterruptReason.ToString() == "UserCanceled";
        var now = DateTime.Now;
        double dt = (now - lastTime).TotalSeconds;
        if (dt >= 0.5)
        {
            Speed = Speed * 0.5 + (Received - lastBytes) / dt * 0.5;
            lastBytes = Received;
            lastTime = now;
        }
    }
}

/// <summary>Общий для всех окон список загрузок.</summary>
static class Downloads
{
    public static readonly List<DownloadItem> Items = new();
    public static event Action? Changed;

    public static void Track(CoreWebView2DownloadOperation op)
    {
        var it = new DownloadItem { Op = op };
        it.Update();
        Items.Insert(0, it);
        if (Items.Count > 30) Items.RemoveAt(Items.Count - 1);
        op.BytesReceivedChanged += (_, _) => { it.Update(); Changed?.Invoke(); };
        op.StateChanged += (_, _) => { it.Update(); Changed?.Invoke(); };
        Changed?.Invoke();
    }

    public static bool AnyActive => Items.Any(i => i.State == CoreWebView2DownloadState.InProgress);

    /// <summary>Общий прогресс активных загрузок: -1 — нет активных.</summary>
    public static float Progress
    {
        get
        {
            var active = Items.Where(i => i.State == CoreWebView2DownloadState.InProgress).ToList();
            if (active.Count == 0) return -1;
            long total = active.Sum(i => i.Total), got = active.Sum(i => i.Received);
            return total > 0 ? Math.Clamp((float)got / total, 0, 1) : 0.15f;
        }
    }

    public static void Clear()
    {
        Items.RemoveAll(i => i.State != CoreWebView2DownloadState.InProgress);
        Changed?.Invoke();
    }
}

/// <summary>Всплывающее окно загрузок.</summary>
sealed class DownloadsPopup : Form
{
    readonly Font nameFont = new("Segoe UI", 9.5f, FontStyle.Bold);
    readonly Font infoFont = new("Segoe UI", 8.5f);
    readonly Font headFont = new("Segoe UI", 11f, FontStyle.Bold);
    readonly Font iconFont = new(Theme.IconFont, 11f);
    readonly Font bigIcon = new(Theme.IconFont, 16f);
    readonly Font linkFont = new("Segoe UI", 9f);
    int hoverRow = -1, hoverButton = -1;
    bool hoverFolder, hoverClear;
    public DateTime HiddenAt { get; private set; }

    static int S(float v) => Theme.S(v);
    int HeadH => S(48);
    int RowH => S(64);
    const int MaxRows = 6;

    public DownloadsPopup()
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        BackColor = Theme.Menu;
        DoubleBuffered = true;
        KeyPreview = true;
        Deactivate += (_, _) => HidePopup();
        KeyDown += (_, e) => { if (e.KeyCode == Keys.Escape) HidePopup(); };
        Downloads.Changed += OnChanged;
    }

    protected override CreateParams CreateParams
    {
        get { var cp = base.CreateParams; cp.ClassStyle |= 0x20000; return cp; } // тень
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        try { Theme.StylePopup(Handle); } catch { }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) Downloads.Changed -= OnChanged;
        base.Dispose(disposing);
    }

    void OnChanged()
    {
        if (!Visible) return;
        ResizeToContent();
        Invalidate();
    }

    void HidePopup()
    {
        if (!Visible) return;
        HiddenAt = DateTime.Now;
        Hide();
    }

    void ResizeToContent()
    {
        int rows = Math.Max(1, Math.Min(MaxRows, Downloads.Items.Count));
        Size = new Size(S(400), HeadH + rows * RowH + S(10));
    }

    public void ShowBelow(Control anchor)
    {
        ResizeToContent();
        var p = anchor.PointToScreen(new Point(anchor.Width, anchor.Height + S(6)));
        var screen = Screen.FromControl(anchor).WorkingArea;
        Location = new Point(Math.Max(screen.Left + 8, Math.Min(p.X - Width, screen.Right - Width - 8)), p.Y);
        if (!Visible) Show(anchor.FindForm());
        Activate();
        Invalidate();
    }

    Rectangle RowRect(int i) => new(S(6), HeadH + i * RowH, Width - S(12), RowH - S(4));
    Rectangle ButtonRect(int i) { var r = RowRect(i); return new Rectangle(r.Right - S(40), r.Y + (r.Height - S(32)) / 2, S(32), S(32)); }
    Rectangle FolderLink => new(Width - S(150), S(12), S(140), S(24));
    Rectangle ClearLink => new(Width - S(250), S(12), S(90), S(24));

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(BackColor);
        TextRenderer.DrawText(g, "Загрузки", headFont, new Rectangle(S(16), 0, S(200), HeadH), Theme.Text,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter);
        TextRenderer.DrawText(g, "Открыть папку", linkFont, FolderLink, hoverFolder ? Theme.Text : Theme.Accent,
            TextFormatFlags.Right | TextFormatFlags.VerticalCenter);
        if (Downloads.Items.Any(i => i.State != CoreWebView2DownloadState.InProgress))
            TextRenderer.DrawText(g, "Очистить", linkFont, ClearLink, hoverClear ? Theme.Text : Theme.TextDim,
                TextFormatFlags.Right | TextFormatFlags.VerticalCenter);

        if (Downloads.Items.Count == 0)
        {
            TextRenderer.DrawText(g, "Пока ничего не скачано", infoFont, new Rectangle(0, HeadH, Width, RowH), Theme.TextDim, Theme.Center);
            return;
        }

        for (int i = 0; i < Math.Min(MaxRows, Downloads.Items.Count); i++)
        {
            var it = Downloads.Items[i];
            var r = RowRect(i);
            if (i == hoverRow)
            {
                using var p = Theme.Round(r, S(10));
                using var b = new SolidBrush(Color.FromArgb(14, 255, 255, 255));
                g.FillPath(b, p);
            }
            bool done = it.State == CoreWebView2DownloadState.Completed;
            bool active = it.State == CoreWebView2DownloadState.InProgress;
            var iconColor = done ? Theme.Accent : active ? Theme.Text : Theme.TextDim;
            TextRenderer.DrawText(g, "", bigIcon, new Rectangle(r.X + S(8), r.Y, S(36), r.Height), iconColor, Theme.Center);

            int tx = r.X + S(52), tw = r.Width - S(52) - S(48);
            TextRenderer.DrawText(g, it.Name, nameFont, new Rectangle(tx, r.Y + S(10), tw, S(20)),
                it.Canceled ? Theme.TextDim : Theme.Text,
                TextFormatFlags.Left | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix);

            string info = active
                ? (it.Total > 0 ? $"{Theme.FormatSize(it.Received)} из {Theme.FormatSize(it.Total)}" : Theme.FormatSize(it.Received)) +
                  (it.Speed > 1 ? $" · {Theme.FormatSize((long)it.Speed)}/с" : "")
                : done ? $"{Theme.FormatSize(Math.Max(it.Total, it.Received))} · Готово — нажмите, чтобы открыть"
                : it.Canceled ? "Отменено" : "Ошибка загрузки";
            var infoColor = !active && !done && !it.Canceled ? Theme.Danger : Theme.TextDim;
            TextRenderer.DrawText(g, info, infoFont, new Rectangle(tx, r.Y + S(31), tw, S(18)), infoColor,
                TextFormatFlags.Left | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine);

            if (active)
            {
                var bar = new RectangleF(tx, r.Bottom - S(9), tw, S(3));
                using var track = new SolidBrush(Color.FromArgb(40, 255, 255, 255));
                g.FillRectangle(track, bar);
                float p = it.Total > 0 ? Math.Clamp((float)it.Received / it.Total, 0, 1) : 0.3f;
                using var fill = new LinearGradientBrush(new RectangleF(bar.X - 1, bar.Y, bar.Width + 2, bar.Height), Theme.Accent, Theme.Accent2, 0f);
                g.FillRectangle(fill, bar.X, bar.Y, bar.Width * p, bar.Height);
            }

            if (active || done)
            {
                var br = ButtonRect(i);
                if (i == hoverRow && hoverButton == i)
                {
                    using var b = new SolidBrush(Theme.Hover);
                    g.FillEllipse(b, br);
                }
                TextRenderer.DrawText(g, active ? "" : "", iconFont, br, Theme.Text, Theme.Center);
            }
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        int row = -1, btn = -1;
        for (int i = 0; i < Math.Min(MaxRows, Downloads.Items.Count); i++)
        {
            if (!RowRect(i).Contains(e.Location)) continue;
            row = i;
            if (ButtonRect(i).Contains(e.Location)) btn = i;
        }
        bool hf = FolderLink.Contains(e.Location), hc = ClearLink.Contains(e.Location);
        Cursor = row >= 0 || hf || hc ? Cursors.Hand : Cursors.Default;
        if (row != hoverRow || btn != hoverButton || hf != hoverFolder || hc != hoverClear)
        {
            hoverRow = row; hoverButton = btn; hoverFolder = hf; hoverClear = hc;
            Invalidate();
        }
        base.OnMouseMove(e);
    }

    protected override void OnMouseClick(MouseEventArgs e)
    {
        base.OnMouseClick(e);
        if (e.Button != MouseButtons.Left) return;
        try
        {
            if (hoverFolder)
            {
                var dir = Downloads.Items.FirstOrDefault()?.Path is { Length: > 0 } p
                    ? Path.GetDirectoryName(p)!
                    : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
                Process.Start(new ProcessStartInfo("explorer.exe", $"\"{dir}\"") { UseShellExecute = true });
                HidePopup();
                return;
            }
            if (hoverClear) { Downloads.Clear(); ResizeToContent(); Invalidate(); return; }
            if (hoverRow < 0 || hoverRow >= Downloads.Items.Count) return;
            var it = Downloads.Items[hoverRow];
            if (it.State == CoreWebView2DownloadState.InProgress)
            {
                if (hoverButton == hoverRow) it.Op.Cancel();
                return;
            }
            if (it.State != CoreWebView2DownloadState.Completed || !File.Exists(it.Path)) return;
            if (hoverButton == hoverRow)
                Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{it.Path}\"") { UseShellExecute = true });
            else
                Process.Start(new ProcessStartInfo(it.Path) { UseShellExecute = true });
            HidePopup();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "Не удалось открыть: " + ex.Message, "Загрузки");
        }
    }
}
