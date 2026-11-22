using System.Text;
using System.Text.Json;
using Microsoft.Web.WebView2.Core;

namespace LiteBrowser;

/// <summary>Субтитры видео YouTube для пересказа ИИ: берутся прямо со страницы видео (как их показывает сам плеер).</summary>
static class VideoContext
{
    public static bool IsYouTubeVideo(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var u) &&
        (u.Host.EndsWith("youtube.com") && (u.AbsolutePath == "/watch" || u.AbsolutePath.StartsWith("/shorts/") || u.AbsolutePath.StartsWith("/live/")) || u.Host == "youtu.be");

    const string Script = """
(async () => {
  const mp = document.getElementById('movie_player');
  const p = (mp && mp.getPlayerResponse && mp.getPlayerResponse()) || window.ytInitialPlayerResponse;
  if (!p || !p.videoDetails) return JSON.stringify({ error: 'noplayer' });
  const v = p.videoDetails;
  const info = { title: v.title || document.title, author: v.author || '', len: +v.lengthSeconds || 0, desc: (v.shortDescription || '').slice(0, 1500) };
  const tracks = (p.captions && p.captions.playerCaptionsTracklistRenderer && p.captions.playerCaptionsTracklistRenderer.captionTracks) || [];
  if (!tracks.length) return JSON.stringify({ ...info, error: 'nocaptions' });
  const pick = tracks.find(t => t.languageCode === 'ru' && t.kind !== 'asr') || tracks.find(t => t.kind !== 'asr')
            || tracks.find(t => t.languageCode === 'ru') || tracks[0];
  // YouTube отдаёт субтитры только запросу самого плеера (с токеном). Берём адрес его запроса;
  // если субтитры выключены — на пару секунд включаем их и потом возвращаем как было.
  const findUrl = () => {
    const e = performance.getEntriesByType('resource').map(x => x.name).filter(n => n.includes('/api/timedtext') && n.includes('v=' + v.videoId));
    return e.length ? e[e.length - 1] : '';
  };
  let url = findUrl(), turnedOn = false;
  if (!url && mp && mp.setOption) {
    // Буфер замеров переполнен запросами страницы — освобождаем место, чтобы увидеть новый запрос
    try { performance.clearResourceTimings(); performance.setResourceTimingBufferSize(1000); } catch (e) { }
    try { mp.loadModule && mp.loadModule('captions'); mp.setOption('captions', 'track', { languageCode: pick.languageCode }); turnedOn = true; } catch (e) { }
    for (let i = 0; i < 25 && !url; i++) { await new Promise(r => setTimeout(r, 200)); url = findUrl(); }
  }
  if (turnedOn) { try { mp.setOption('captions', 'track', {}); mp.unloadModule && mp.unloadModule('captions'); } catch (e) { } }
  if (!url) url = pick.baseUrl;
  const u = new URL(url, location.href);
  u.searchParams.set('fmt', 'json3');
  let text = '', dbg = '';
  try {
    const r = await fetch(u.toString(), { credentials: 'include' }); dbg = 'found=' + (url !== pick.baseUrl) + ' on=' + turnedOn + ' status=' + r.status;
    const raw = await r.text(); dbg += ' len=' + raw.length; const j = JSON.parse(raw || '{}');
    for (const e of (j.events || [])) {
      if (!e.segs) continue;
      const line = e.segs.map(s => s.utf8 || '').join('').replace(/\s+/g, ' ').trim();
      if (!line) continue;
      const t = Math.floor((e.tStartMs || 0) / 1000);
      text += '[' + Math.floor(t / 60) + ':' + String(t % 60).padStart(2, '0') + '] ' + line + '\n';
    }
  } catch (e) { dbg += ' err=' + e; }
  // Запасной путь — как кнопка «Показать текст видео»: панель расшифровки (youtubei get_transcript)
  if (!text) {
    try {
      let src = '';
      const d = window.ytInitialData;
      if (d && JSON.stringify(d.currentVideoEndpoint || {}).includes(v.videoId)) src = JSON.stringify(d);
      if (!src) src = await (await fetch(location.href, { credentials: 'include' })).text();
      const m = src.match(/"getTranscriptEndpoint":\{"params":"([^"]+)"/);
      const ctx = window.ytcfg && ytcfg.get && ytcfg.get('INNERTUBE_CONTEXT');
      dbg += ' tp=' + !!m + ' ctx=' + !!ctx;
      if (m && ctx) {
        const key = ytcfg.get('INNERTUBE_API_KEY');
        const headers = { 'Content-Type': 'application/json' };
        const cn = ytcfg.get('INNERTUBE_CONTEXT_CLIENT_NAME'), cv = ytcfg.get('INNERTUBE_CLIENT_VERSION');
        if (cn) headers['X-Youtube-Client-Name'] = String(cn);
        if (cv) headers['X-Youtube-Client-Version'] = cv;
        if (ctx.client && ctx.client.visitorData) headers['X-Goog-Visitor-Id'] = ctx.client.visitorData;
        const r = await fetch('/youtubei/v1/get_transcript?prettyPrint=false' + (key ? '&key=' + key : ''), {
          method: 'POST', credentials: 'include', headers,
          body: JSON.stringify({ context: ctx, params: m[1] }),
        });
        const j = await r.json();
        dbg += ' ts=' + r.status;
        const walk = o => {
          if (!o || typeof o !== 'object') return;
          if (o.transcriptSegmentRenderer) {
            const s = o.transcriptSegmentRenderer;
            const line = ((s.snippet && s.snippet.runs) || []).map(x => x.text).join('').replace(/\s+/g, ' ').trim();
            const t = Math.floor((+s.startMs || 0) / 1000);
            if (line) text += '[' + Math.floor(t / 60) + ':' + String(t % 60).padStart(2, '0') + '] ' + line + '\n';
            return;
          }
          for (const k in o) walk(o[k]);
        };
        walk(j);
      }
    } catch (e) { dbg += ' terr=' + e; }
  }
  // Последний путь — как человек: нажать «Показать текст видео» и прочитать открывшуюся панель
  if (!text) {
    try {
      const readPanel = () => [...document.querySelectorAll('ytd-transcript-segment-renderer')].map(s => {
        const ts = (s.querySelector('.segment-timestamp') || {}).textContent || '';
        const tx = (s.querySelector('.segment-text') || {}).textContent || '';
        return tx.trim() ? '[' + ts.trim() + '] ' + tx.replace(/\s+/g, ' ').trim() : '';
      }).filter(Boolean);
      let lines = readPanel(), opened = false;
      if (!lines.length) {
        const btn = document.querySelector('ytd-video-description-transcript-section-renderer button')
                 || [...document.querySelectorAll('button')].find(b => /transcript|расшифров|текст видео/i.test(b.getAttribute('aria-label') || b.textContent || ''));
        dbg += ' btn=' + !!btn;
        if (btn) {
          btn.click(); opened = true;
          for (let i = 0; i < 40 && !lines.length; i++) { await new Promise(r => setTimeout(r, 200)); lines = readPanel(); }
        }
      }
      dbg += ' panel=' + lines.length;
      if (lines.length) text = lines.join('\n') + '\n';
      if (opened) {
        const close = document.querySelector('ytd-engagement-panel-section-list-renderer[target-id="engagement-panel-searchable-transcript"] #visibility-button button');
        if (close) close.click();
      }
    } catch (e) { dbg += ' perr=' + e; }
  }
  return JSON.stringify({ ...info, lang: pick.languageCode, auto: pick.kind === 'asr', text, error: text ? '' : 'empty', dbg });
})()
""";

    public sealed record Result(string Title, string Author, int Length, string Description, string Lang, string Text, string Error, string Debug = "");

    public static async Task<Result> GetAsync(CoreWebView2 core)
    {
        var json = await core.CallDevToolsProtocolMethodAsync("Runtime.evaluate",
            JsonSerializer.Serialize(new { expression = Script, awaitPromise = true, returnByValue = true, timeout = 15000 }));
        using var d = JsonDocument.Parse(json);
        var value = d.RootElement.TryGetProperty("result", out var r) && r.TryGetProperty("value", out var v) ? v.GetString() ?? "{}" : "{}";
        using var x = JsonDocument.Parse(value);
        string S(string n) => x.RootElement.TryGetProperty(n, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() ?? "" : "";
        int len = x.RootElement.TryGetProperty("len", out var l) && l.TryGetInt32(out var li) ? li : 0;
        return new Result(S("title"), S("author"), len, S("desc"), S("lang"), S("text"), S("error"), S("dbg"));
    }

    /// <summary>Уложить субтитры в бюджет: если не помещаются, берём строки равномерно по всему видео.</summary>
    public static string Fit(string text, int budget)
    {
        if (text.Length <= budget) return text;
        var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        double keep = (double)budget / text.Length;
        var sb = new StringBuilder();
        double acc = 0;
        foreach (var line in lines)
        {
            acc += keep;
            if (acc < 1) continue;
            acc -= 1;
            if (sb.Length + line.Length + 1 > budget) break;
            sb.Append(line).Append('\n');
        }
        return sb.ToString();
    }
}
