using System.Drawing.Drawing2D;
using System.Text.Json;
using Microsoft.Web.WebView2.Core;

namespace LiteBrowser;

/// <summary>
/// Мини-плеер поверх всех окон: обложка, название, исполнитель и кнопки ⏮ ⏯ ⏭ для вкладки, где играет музыка или видео.
/// Кнопки «следующий/предыдущий» берём у самого сайта (Media Session: так ими управляют клавиши на клавиатуре),
/// поэтому работает на Яндекс Музыке, YouTube, VK и других сайтах, которые их объявляют.
/// </summary>
static class MediaSessionHook
{
    /// <summary>В каждую страницу: запоминаем обработчики кнопок медиа, которые регистрирует сайт.</summary>
    public const string Script = """
(() => {
  if (window.__liteMS || !('mediaSession' in navigator)) return;
  const handlers = {};
  window.__liteMS = handlers;
  const orig = navigator.mediaSession.setActionHandler.bind(navigator.mediaSession);
  navigator.mediaSession.setActionHandler = (action, fn) => { handlers[action] = fn; try { orig(action, fn); } catch (e) { } };
})();
""";

    /// <summary>Состояние: название, исполнитель, обложка, играет ли, есть ли «следующий/предыдущий».</summary>
    public const string StateJs = """
(() => {
  const ms = navigator.mediaSession, md = ms && ms.metadata, h = window.__liteMS || {};
  const media = [...document.querySelectorAll('video, audio')];
  const playing = media.some(m => !m.paused && !m.ended) || (ms && ms.playbackState === 'playing');
  let art = '';
  if (md && md.artwork && md.artwork.length) art = [...md.artwork].sort((a, b) => parseInt(b.sizes || '0') - parseInt(a.sizes || '0'))[0].src;
  return JSON.stringify({ title: md ? md.title : document.title, artist: md ? md.artist : location.hostname, art, playing,
    next: !!h.nexttrack, prev: !!h.previoustrack });
})()
""";

    public static string ActionJs(string action) => $$"""
(() => {
  const h = window.__liteMS || {};
  if (h['{{action}}']) { try { h['{{action}}']({ action: '{{action}}' }); return 'ms'; } catch (e) { } }
  const media = [...document.querySelectorAll('video, audio')];
  if ('{{action}}' === 'play' || '{{action}}' === 'pause') {
    const playing = media.filter(m => !m.paused && !m.ended);
    if ('{{action}}' === 'pause') playing.forEach(m => m.pause()); else (media[0] && media[0].play().catch(() => { }));
    return 'media';
  }
  return 'none';
})()
""";
}

sealed class MiniPlayerForm : Form
{
    static MiniPlayerForm? open;
    readonly MainForm owner;
    BrowserTab tab;
    readonly System.Windows.Forms.Timer timer = new() { Interval = 1000 };
    string title = "", artist = "", artUrl = "";
    bool playing, hasNext, hasPrev;
    Image? art;
    Rectangle prevR, playR, nextR, openR, closeR;
    int hover = -1;
    Point dragFrom;
    static readonly HttpClient http = new() { Timeout = TimeSpan.FromSeconds(10) };
    static int S(float v) => Theme.S(v);

    public static void ShowFor(MainForm owner, BrowserTab tab)
    {
        if (open is { IsDisposed: false }) { open.tab = tab; open.Refresh_(); open.Activate(); return; }
        open = new MiniPlayerForm(owner, tab);
        open.Show();
    }

    MiniPlayerForm(MainForm owner, BrowserTab tab)
    {
        this.owner = owner; this.tab = tab;
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        DoubleBuffered = true;
        BackColor = Theme.Toolbar;
        Size = new Size(S(340), S(84));
        var wa = Screen.FromControl(owner).WorkingArea;
        Location = new Point(wa.Right - Width - S(16), wa.Bottom - Height - S(16));
        timer.Tick += (_, _) => Refresh_();
        Shown += (_, _) => { Refresh_(); timer.Start(); };
        FormClosed += (_, _) => { timer.Dispose(); art?.Dispose(); open = null; };
    }

    protected override CreateParams CreateParams
    {
        get { var cp = base.CreateParams; cp.ExStyle |= 0x08000000; return cp; } // WS_EX_NOACTIVATE: не отнимаем фокус у других программ
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        try { Theme.StylePopup(Handle); } catch { }
    }

    async void Refresh_()
    {
        if (IsDisposed) return;
        var core = tab.Core;
        if (core == null) { Close(); return; }
        try
        {
            var raw = await core.ExecuteScriptAsync(MediaSessionHook.StateJs);
            var json = JsonSerializer.Deserialize<string>(raw);
            if (json == null) return;
            using var d = JsonDocument.Parse(json);
            var r = d.RootElement;
            title = r.GetProperty("title").GetString() ?? "";
            artist = r.GetProperty("artist").GetString() ?? "";
            playing = r.GetProperty("playing").GetBoolean();
            hasNext = r.GetProperty("next").GetBoolean();
            hasPrev = r.GetProperty("prev").GetBoolean();
            var a = r.GetProperty("art").GetString() ?? "";
            if (a != artUrl) { artUrl = a; _ = LoadArt(a); }
            Invalidate();
        }
        catch { }
    }

    async Task LoadArt(string url)
    {
        Image? img = null;
        try
        {
            if (Uri.TryCreate(url, UriKind.Absolute, out var u) && u.Scheme is "https" or "http")
            {
                var bytes = await http.GetByteArrayAsync(u);
                if (bytes.Length < 5_000_000) img = Image.FromStream(new MemoryStream(bytes));
            }
        }
        catch { }
        if (IsDisposed) { img?.Dispose(); return; }
        art?.Dispose(); art = img; Invalidate();
    }

    async Task Act(string action)
    {
        if (tab.Core == null) return;
        try
        {
            await tab.Core.CallDevToolsProtocolMethodAsync("Runtime.evaluate",
                JsonSerializer.Serialize(new { expression = MediaSessionHook.ActionJs(action), userGesture = true, returnByValue = true }));
        }
        catch { }
        await Task.Delay(300);
        Refresh_();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
        int pad = S(10), side = Height - pad * 2;
        var artR = new Rectangle(pad, pad, side, side);
        using (var path = Theme.Round(artR, S(8)))
        {
            if (art != null) { g.SetClip(path); g.DrawImage(art, artR); g.ResetClip(); }
            else { using var b = new LinearGradientBrush(artR, Theme.Accent, Theme.Accent2, 45f); g.FillPath(b, path); TextRenderer.DrawText(g, "♫", Theme.GetFont("Segoe UI", 18f), artR, Color.White, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter); }
        }
        int x = artR.Right + pad, btn = S(30), by = Height - pad - btn;
        var textR = new Rectangle(x, pad - S(2), Width - x - S(34), S(20));
        TextRenderer.DrawText(g, title, Theme.GetFont("Segoe UI", 10f, FontStyle.Bold), textR, Theme.Text, TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        TextRenderer.DrawText(g, artist, Theme.GetFont("Segoe UI", 9f), new Rectangle(x, textR.Bottom, textR.Width, S(18)), Theme.TextDim, TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        prevR = new Rectangle(x, by, btn, btn);
        playR = new Rectangle(prevR.Right + S(4), by, btn, btn);
        nextR = new Rectangle(playR.Right + S(4), by, btn, btn);
        openR = new Rectangle(Width - pad - btn, by, btn, btn);
        closeR = new Rectangle(Width - S(28), S(4), S(24), S(24));
        void Btn(Rectangle r, string glyph, int id, bool enabled)
        {
            if (hover == id && enabled) { using var hb = new SolidBrush(Theme.Hover); using var hp = Theme.Round(r, S(6)); g.FillPath(hb, hp); }
            TextRenderer.DrawText(g, glyph, Theme.GetFont(Theme.IconFont, 11f), r, enabled ? Theme.Text : Theme.TextDim, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        }
        Btn(prevR, "", 0, hasPrev);
        Btn(playR, playing ? "" : "", 1, true);
        Btn(nextR, "", 2, hasNext);
        Btn(openR, "", 3, true);
        Btn(closeR, "", 4, true);
        using var pen = new Pen(Theme.Line);
        g.DrawRectangle(pen, 0, 0, Width - 1, Height - 1);
    }

    int HitTest(Point p) => prevR.Contains(p) ? 0 : playR.Contains(p) ? 1 : nextR.Contains(p) ? 2 : openR.Contains(p) ? 3 : closeR.Contains(p) ? 4 : -1;

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Left && hover == -1) { Location = new Point(Location.X + e.X - dragFrom.X, Location.Y + e.Y - dragFrom.Y); return; }
        int h = HitTest(e.Location);
        if (h != hover) { hover = h; Cursor = h >= 0 ? Cursors.Hand : Cursors.SizeAll; Invalidate(); }
    }

    protected override void OnMouseDown(MouseEventArgs e) { dragFrom = e.Location; hover = HitTest(e.Location); }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left) return;
        switch (HitTest(e.Location))
        {
            case 0: if (hasPrev) _ = Act("previoustrack"); break;
            case 1: _ = Act(playing ? "pause" : "play"); break;
            case 2: if (hasNext) _ = Act("nexttrack"); break;
            case 3: owner.ShowTabFromManager(tab); break;
            case 4: Close(); break;
        }
    }

    protected override void OnMouseLeave(EventArgs e) { hover = -1; Invalidate(); }
}
