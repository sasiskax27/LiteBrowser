using System.Text.Json;

namespace LiteBrowser;

/// <summary>
/// Чтение вслух: выделенный текст или основной текст страницы (статья без меню и рекламы), голосами Windows.
/// Голос выбирается по языку страницы; читаем по предложениям — паузу и остановку можно нажать в любой момент.
/// </summary>
sealed partial class MainForm
{
    BrowserTab? speakingTab;

    internal const string SpeakJs = """
(() => {
  const synth = window.speechSynthesis;
  if (!synth) return 'nosynth';
  synth.cancel();
  const sel = String(getSelection() || '').trim();
  let text = sel;
  if (!text) {
    // Основной текст: article/main, иначе самый «текстовый» блок страницы
    const pick = document.querySelector('article') || document.querySelector('main') || document.querySelector('[role=main]');
    let root = pick;
    if (!root) {
      let best = document.body, bestLen = 0;
      for (const el of document.querySelectorAll('div, section')) {
        const len = [...el.querySelectorAll(':scope > p')].reduce((n, p) => n + p.innerText.length, 0);
        if (len > bestLen) { best = el; bestLen = len; }
      }
      root = best;
    }
    text = [...root.querySelectorAll('h1, h2, h3, p, li, blockquote')].map(e => e.innerText.trim()).filter(t => t.length > 1).join('\n') || root.innerText;
  }
  text = text.replace(/\s+\n/g, '\n').slice(0, 60000);
  if (!text.trim()) return 'empty';
  let lang = (document.documentElement.lang || navigator.language || 'ru').toLowerCase();
  // Язык страницы указан неверно, а текст в основном русский — читаем русским голосом
  const sample = text.slice(0, 2000), cyr = (sample.match(/[а-яё]/gi) || []).length, lat = (sample.match(/[a-z]/gi) || []).length;
  if (cyr > lat * 2) lang = 'ru-ru'; else if (lat > cyr * 2 && lang.startsWith('ru')) lang = 'en-us';
  const parts = text.match(/[^.!?…\n]+[.!?…]*\s*|\n/g) || [text];
  const chunks = [];
  let cur = '';
  for (const p of parts) { if ((cur + p).length > 220 && cur) { chunks.push(cur); cur = ''; } cur += p; }
  if (cur.trim()) chunks.push(cur);
  const voices = synth.getVoices();
  const voice = voices.find(v => v.lang.toLowerCase().startsWith(lang.slice(0, 2)) && /natural|online/i.test(v.name))
             || voices.find(v => v.lang.toLowerCase().startsWith(lang.slice(0, 2)));
  window.__liteSpeak = { left: chunks.length };
  for (const c of chunks) {
    const u = new SpeechSynthesisUtterance(c);
    if (voice) u.voice = voice; else u.lang = lang;
    u.rate = window.__liteSpeakRate || 1;
    if (window.__liteSpeakVolume !== undefined) u.volume = window.__liteSpeakVolume;
    u.onend = () => { window.__liteSpeak.left--; if (window.__liteSpeak.left <= 0 && window.chrome && chrome.webview) chrome.webview.postMessage({ type: 'speak:done' }); };
    synth.speak(u);
  }
  return 'ok:' + chunks.length + ':' + (voice ? voice.name : lang);
})()
""";

    async Task ReadAloud()
    {
        var tab = active;
        if (tab?.Core == null) return;
        if (speakingTab != null) StopReading();
        try
        {
            var raw = await tab.Core.CallDevToolsProtocolMethodAsync("Runtime.evaluate",
                JsonSerializer.Serialize(new { expression = SpeakJs, userGesture = true, returnByValue = true }));
            using var doc = JsonDocument.Parse(raw);
            var res = doc.RootElement.GetProperty("result").TryGetProperty("value", out var v) ? v.GetString() ?? "" : "";
            if (!res.StartsWith("ok")) { infoBar.ShowBar("speak", "", res == "empty" ? L.T("На странице нет текста для чтения") : L.T("Чтение вслух недоступно")); return; }
            speakingTab = tab;
            ShowSpeakBar(false);
        }
        catch { infoBar.ShowBar("speak", "", L.T("Чтение вслух недоступно")); }
    }

    void ShowSpeakBar(bool paused)
    {
        infoBar.ShowBar("speak", "", paused ? L.T("Чтение на паузе") : L.T("Читаю вслух…"),
            (paused ? L.T("Продолжить") : L.T("Пауза"), () => { _ = Js(paused ? "speechSynthesis.resume()" : "speechSynthesis.pause()"); ShowSpeakBar(!paused); }, true),
            (L.T("Стоп"), StopReading, false));
    }

    async Task Js(string code)
    {
        if (speakingTab?.Core is { } core) try { await core.ExecuteScriptAsync(code); } catch { }
    }

    void StopReading()
    {
        _ = Js("speechSynthesis.cancel()");
        speakingTab = null;
        if (infoBar.Key == "speak") infoBar.HideBar();
    }

    /// <summary>Сообщение «дочитал» со страницы — убираем панель.</summary>
    void OnSpeakDone(BrowserTab tab)
    {
        if (tab != speakingTab) return;
        speakingTab = null;
        if (infoBar.Key == "speak") infoBar.HideBar();
    }
}
