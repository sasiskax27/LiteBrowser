using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace LiteBrowser;

/// <summary>Инструменты для страниц: видео, медиа, скриншоты, режим чтения.</summary>
static class PageTools
{
    /// <summary>Выполнить JS «как будто пользователь кликнул» и дождаться результата промиса (нужно для картинки в картинке).</summary>
    public static async Task<string> EvalAsync(CoreWebView2 core, string expression)
    {
        var args = JsonSerializer.Serialize(new { expression, awaitPromise = true, userGesture = true, returnByValue = true });
        var res = await core.CallDevToolsProtocolMethodAsync("Runtime.evaluate", args);
        using var doc = JsonDocument.Parse(res);
        if (doc.RootElement.TryGetProperty("result", out var r) && r.TryGetProperty("value", out var v))
            return v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : v.ToString();
        return "";
    }

    public const string PictureInPicture = """
(async () => {
  try {
    if (document.pictureInPictureElement) { await document.exitPictureInPicture(); return 'exit'; }
    const vids = [...document.querySelectorAll('video')].filter(v => v.readyState > 0);
    if (!vids.length) return 'novideo';
    const v = vids.sort((a, b) => b.clientWidth * b.clientHeight - a.clientWidth * a.clientHeight)[0];
    v.disablePictureInPicture = false;
    await v.requestPictureInPicture();
    return 'ok';
  } catch (e) { return 'fail'; }
})()
""";

    /// <summary>Данные для запасного мини-плеera YouTube: id видео и текущее время.</summary>
    public const string YouTubeInfo = """
(() => {
  const m = location.href.match(/[?&]v=([\w-]{11})/) || location.href.match(/\/(?:shorts|embed|live)\/([\w-]{11})/);
  const v = document.querySelector('video');
  if (!m) return '';
  const t = v ? Math.floor(v.currentTime) : 0;
  if (v) v.pause();
  return m[1] + '|' + t;
})()
""";

    public static string SetSpeed(double rate) => $$"""
(() => {
  window.__liteRate = {{rate.ToString(System.Globalization.CultureInfo.InvariantCulture)}};
  const apply = () => document.querySelectorAll('video, audio').forEach(m => { if (m.playbackRate !== window.__liteRate) m.playbackRate = window.__liteRate; });
  apply();
  if (!window.__liteRateTimer) window.__liteRateTimer = setInterval(apply, 700);
  return document.querySelectorAll('video, audio').length;
})()
""";

    /// <summary>Прямые ссылки на видео и аудио на странице (потоковое видео вроде YouTube так не скачать).</summary>
    public const string FindMedia = """
(() => {
  const out = [], seen = new Set();
  const add = (src, kind) => {
    try {
      if (!src || src.startsWith('blob:') || src.startsWith('data:')) return;
      const u = new URL(src, location.href).href;
      if (!/^https?:/.test(u) || seen.has(u) || /\.m3u8|\.mpd/i.test(u)) return;
      seen.add(u); out.push({ src: u, kind });
    } catch (e) {}
  };
  document.querySelectorAll('video, audio').forEach(m => {
    const kind = m.tagName === 'AUDIO' ? 'аудио' : 'видео';
    add(m.currentSrc || m.src, kind);
    m.querySelectorAll('source').forEach(s => add(s.src, kind));
  });
  document.querySelectorAll('a[href]').forEach(a => { if (/\.(mp4|webm|mkv|mov|mp3|m4a|ogg|wav|flac)(\?|$)/i.test(a.href)) add(a.href, /\.(mp3|m4a|ogg|wav|flac)/i.test(a.href) ? 'аудио' : 'видео'); });
  const og = document.querySelector('meta[property="og:video"], meta[property="og:video:url"], meta[property="og:audio"]');
  if (og) add(og.content, 'видео');
  return JSON.stringify({ title: document.title, items: out.slice(0, 15) });
})()
""";

    /// <summary>Режим чтения: оставляет только текст статьи в удобном виде.</summary>
    public const string Reader = """
(() => {
  const old = document.getElementById('lite-reader');
  if (old) { old.remove(); document.documentElement.style.overflow = ''; return 'closed'; }
  const score = el => [...el.querySelectorAll('p')].reduce((s, p) => s + (p.innerText || '').trim().length, 0);
  let best = document.querySelector('article');
  if (!best || score(best) < 500) {
    best = null; let max = 0;
    document.querySelectorAll('main, [role=main], article, section, div').forEach(el => {
      const s = score(el) - (el.querySelectorAll('a').length * 20);
      if (s > max) { max = s; best = el; }
    });
  }
  if (!best || score(best) < 300) return 'notarticle';
  const title = (document.querySelector('h1') || {}).innerText || document.title;
  const box = document.createElement('div');
  box.id = 'lite-reader';
  const theme = localStorage.getItem('liteReaderTheme') || 'dark';
  let size = +(localStorage.getItem('liteReaderSize') || 20);
  box.innerHTML = `<style>
    #lite-reader{position:fixed;inset:0;z-index:2147483647;overflow:auto;font-family:Georgia,"Times New Roman",serif;transition:background .2s}
    #lite-reader.dark{background:#16171c;color:#e4e6ee} #lite-reader.light{background:#f7f3ea;color:#2b2a27} #lite-reader.sepia{background:#efe2c6;color:#4a3a22}
    #lite-reader .lr-bar{position:sticky;top:0;display:flex;gap:6px;justify-content:center;padding:10px;backdrop-filter:blur(10px);font-family:"Segoe UI",sans-serif;z-index:2}
    #lite-reader.dark .lr-bar{background:rgba(22,23,28,.85)} #lite-reader.light .lr-bar{background:rgba(247,243,234,.85)} #lite-reader.sepia .lr-bar{background:rgba(239,226,198,.85)}
    #lite-reader .lr-bar button{border:0;border-radius:10px;padding:7px 14px;cursor:pointer;font:600 13px "Segoe UI";background:rgba(127,127,127,.18);color:inherit}
    #lite-reader .lr-body{max-width:720px;margin:0 auto;padding:30px 24px 120px;line-height:1.75}
    #lite-reader h1.lr-title{font-size:2em;line-height:1.25;margin:10px 0 24px}
    #lite-reader img,#lite-reader video,#lite-reader figure{max-width:100%;height:auto;border-radius:10px}
    #lite-reader a{color:#7c9cff} #lite-reader pre{white-space:pre-wrap;background:rgba(127,127,127,.12);padding:12px;border-radius:8px}
    #lite-reader blockquote{border-left:3px solid #7c9cff;margin:1em 0;padding-left:1em;opacity:.9}
  </style>
  <div class="lr-bar"><button data-a="minus">A−</button><button data-a="plus">A+</button><button data-a="dark">Тёмная</button><button data-a="light">Светлая</button><button data-a="sepia">Сепия</button><button data-a="close">✕ Закрыть (Esc)</button></div>
  <div class="lr-body"><h1 class="lr-title"></h1><div class="lr-content"></div></div>`;
  box.className = theme;
  box.querySelector('.lr-title').textContent = title;
  const content = box.querySelector('.lr-content');
  best.querySelectorAll('p, h2, h3, h4, img, figure, blockquote, ul, ol, pre, table').forEach(el => {
    if (el.closest('nav, aside, footer, form, [class*=comment], [class*=share], [class*=related], [class*=advert], [id*=ad-]')) return;
    if (el.parentElement && el.parentElement.closest('figure, blockquote, ul, ol, table, pre') && el.parentElement.closest('figure, blockquote, ul, ol, table, pre') !== el) return;
    if (el.tagName === 'P' && (el.innerText || '').trim().length < 25) return;
    const c = el.cloneNode(true);
    c.querySelectorAll('script, style, iframe, button, form').forEach(x => x.remove());
    c.removeAttribute('style'); c.removeAttribute('class');
    if (c.tagName === 'IMG' && !c.src) return;
    content.appendChild(c);
  });
  const apply = () => { box.querySelector('.lr-body').style.fontSize = size + 'px'; };
  apply();
  box.querySelector('.lr-bar').onclick = e => {
    const a = e.target.dataset && e.target.dataset.a;
    if (a === 'plus') size = Math.min(32, size + 2);
    if (a === 'minus') size = Math.max(14, size - 2);
    if (['dark', 'light', 'sepia'].includes(a)) { box.className = a; try { localStorage.setItem('liteReaderTheme', a); } catch (x) {} }
    if (a === 'close') { box.remove(); document.documentElement.style.overflow = ''; return; }
    try { localStorage.setItem('liteReaderSize', size); } catch (x) {}
    apply();
  };
  const esc = e => { if (e.key === 'Escape' && document.getElementById('lite-reader')) { box.remove(); document.documentElement.style.overflow = ''; document.removeEventListener('keydown', esc); } };
  document.addEventListener('keydown', esc);
  document.documentElement.style.overflow = 'hidden';
  document.body.appendChild(box);
  box.focus();
  return 'opened';
})()
""";

    /// <summary>Скриншот: видимая часть или вся страница целиком (через протокол DevTools).</summary>
    public static async Task<string> Screenshot(CoreWebView2 core, bool fullPage)
    {
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "LiteBrowser");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, $"Скриншот {DateTime.Now:yyyy-MM-dd HH-mm-ss}.png");
        if (!fullPage)
        {
            await using var fs = File.Create(path);
            await core.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, fs);
        }
        else
        {
            var metrics = await core.CallDevToolsProtocolMethodAsync("Page.getLayoutMetrics", "{}");
            using var m = JsonDocument.Parse(metrics);
            var size = m.RootElement.TryGetProperty("cssContentSize", out var cs) ? cs : m.RootElement.GetProperty("contentSize");
            double w = size.GetProperty("width").GetDouble(), h = Math.Min(size.GetProperty("height").GetDouble(), 20000);
            var args = JsonSerializer.Serialize(new
            {
                format = "png", captureBeyondViewport = true, fromSurface = true,
                clip = new { x = 0, y = 0, width = w, height = h, scale = 1 },
            });
            var res = await core.CallDevToolsProtocolMethodAsync("Page.captureScreenshot", args);
            using var d = JsonDocument.Parse(res);
            await File.WriteAllBytesAsync(path, Convert.FromBase64String(d.RootElement.GetProperty("data").GetString()!));
        }
        try
        {
            using var img = Image.FromFile(path);
            Clipboard.SetImage(new Bitmap(img));
        }
        catch { }
        return path;
    }
}

/// <summary>Мини-плеер поверх всех окон (запасной вариант «картинки в картинке» для YouTube).</summary>
sealed class MiniPlayer : Form
{
    readonly WebView2 view = new() { Dock = DockStyle.Fill };

    public MiniPlayer(string videoId, int start)
    {
        Text = "LiteBrowser — мини-плеер";
        Icon = Theme.MakeAppIcon();
        TopMost = true;
        FormBorderStyle = FormBorderStyle.SizableToolWindow;
        StartPosition = FormStartPosition.Manual;
        BackColor = Color.Black;
        Size = new Size(Theme.S(480), Theme.S(300));
        var wa = Screen.PrimaryScreen!.WorkingArea;
        Location = new Point(wa.Right - Width - 20, wa.Bottom - Height - 20);
        Controls.Add(view);
        Load += async (_, _) =>
        {
            var env = await AppEnv.GetEnvironment();
            await view.EnsureCoreWebView2Async(env);
            view.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
            view.CoreWebView2.Navigate($"https://www.youtube-nocookie.com/embed/{videoId}?autoplay=1&start={start}");
        };
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        try { Theme.StyleTitleBar(Handle, Theme.Frame); } catch { }
    }
}
