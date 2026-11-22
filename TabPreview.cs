using System.Drawing.Drawing2D;
using Microsoft.Web.WebView2.Core;

namespace LiteBrowser;

/// <summary>Карточка-превью вкладки при наведении: название, сайт, состояние и уменьшенная картинка страницы.</summary>
sealed class TabPreview : Form
{
    BrowserTab? tab;
    static int S(float v) => Theme.S(v);
    int W => S(300);
    int ImgH => S(168);

    public TabPreview()
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
            cp.ExStyle |= 0x08000000 | 0x80 | 0x20; // без активации, окно-инструмент, клики проходят насквозь
            cp.ClassStyle |= 0x20000;
            return cp;
        }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        try { Theme.StylePopup(Handle); } catch { }
    }

    public BrowserTab? Tab => Visible ? tab : null;

    /// <summary>Показать у вкладки: под ней (вкладки сверху) или справа (вертикальные вкладки).</summary>
    public void ShowFor(BrowserTab t, Rectangle tabScreen, bool vertical, Form owner)
    {
        tab = t;
        BackColor = Theme.Menu;
        int h = S(62) + (t.Thumb != null ? ImgH + S(8) : 0);
        var wa = Screen.FromRectangle(tabScreen).WorkingArea;
        int x = vertical ? tabScreen.Right + S(8) : tabScreen.X;
        int y = vertical ? tabScreen.Y : tabScreen.Bottom + S(6);
        x = Math.Clamp(x, wa.Left + S(4), wa.Right - W - S(4));
        y = Math.Clamp(y, wa.Top + S(4), wa.Bottom - h - S(4));
        Bounds = new Rectangle(x, y, W, h);
        if (!Visible) Show(owner);
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        if (tab == null) return;
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.Clear(BackColor);
        int pad = S(12);
        var titleFont = Theme.GetFont("Segoe UI", 9.5f, FontStyle.Bold);
        var small = Theme.GetFont("Segoe UI", 8.5f);
        TextRenderer.DrawText(g, tab.Title, titleFont, new Rectangle(pad, S(8), W - pad * 2, S(22)), Theme.Text,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix);
        var url = tab.Core?.Source ?? tab.PendingUrl ?? "";
        var host = LitePages.Display(url) ?? (Uri.TryCreate(url, UriKind.Absolute, out var u) ? u.Host : url);
        var state = tab.Frozen ? L.T("заморожена — не занимает память") : tab.Core == null ? L.T("ещё не загружена") : tab.Muted ? L.T("звук выключен") : tab.Audio ? L.T("играет звук") : "";
        TextRenderer.DrawText(g, host + (state.Length > 0 ? "  ·  " + state : ""), small, new Rectangle(pad, S(30), W - pad * 2, S(20)), Theme.TextDim,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix);
        if (tab.Thumb != null)
        {
            var r = new Rectangle(pad, S(58), W - pad * 2, ImgH);
            using var clip = Theme.Round(r, S(8));
            g.SetClip(clip);
            // Картинка — по ширине, верх страницы
            float scale = (float)r.Width / tab.Thumb.Width;
            int srcH = (int)Math.Min(tab.Thumb.Height, r.Height / scale);
            g.DrawImage(tab.Thumb, r with { Height = (int)(srcH * scale) }, new Rectangle(0, 0, tab.Thumb.Width, srcH), GraphicsUnit.Pixel);
            g.ResetClip();
            using var pen = new Pen(Theme.Line);
            g.DrawPath(pen, clip);
        }
    }

    /// <summary>Снимок страницы вкладки (уменьшенный) — делается, когда вкладку покидают.</summary>
    public static async Task CaptureAsync(BrowserTab t)
    {
        var core = t.Core;
        if (core == null || !t.View.Visible || t.View.Width < 50) return;
        try
        {
            using var ms = new MemoryStream();
            await core.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Jpeg, ms);
            ms.Position = 0;
            using var full = Image.FromStream(ms);
            int w = S(276), h = (int)((double)full.Height * w / full.Width);
            var small = new Bitmap(w, Math.Max(1, h));
            using (var g = Graphics.FromImage(small))
            {
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.DrawImage(full, 0, 0, w, h);
            }
            var old = t.Thumb;
            t.Thumb = small;
            old?.Dispose();
        }
        catch { }
    }
}
