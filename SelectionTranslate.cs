using System.Text.Json;

namespace LiteBrowser;

/// <summary>
/// Перевод выделенного текста: рядом с выделением появляется маленькая кнопка «Перевести»,
/// по клику — подсказка с переводом. Текст уходит переводчику только по клику.
/// </summary>
static class SelectionTranslate
{
    static readonly HttpClient Http = CreateHttp();

    static HttpClient CreateHttp()
    {
        var h = new HttpClient { Timeout = TimeSpan.FromSeconds(6) };
        // Без обычного заголовка браузера Google быстро отвечает «слишком много запросов»
        h.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/157.0 Safari/537.36");
        return h;
    }

    /// <summary>Скрипт страницы: кнопка у выделения и всплывающая подсказка (в Shadow DOM, чтобы стили сайта не мешали).</summary>
    public const string Script = """
(() => {
  if (window.top !== window || window.__liteTr) return;
  let host, root, btn, tip, lastText = '';
  const ensure = () => {
    if (host && host.isConnected) return;
    host = document.createElement('lite-translate');
    host.style.cssText = 'position:absolute;left:0;top:0;z-index:2147483647;';
    root = host.attachShadow({ mode: 'closed' });
    root.innerHTML = `<style>
      .b{position:absolute;display:none;align-items:center;gap:5px;font:600 12px "Segoe UI",sans-serif;color:#0d1020;background:linear-gradient(135deg,#7c9cff,#b07cff);
         border:0;border-radius:9px;padding:5px 9px;cursor:pointer;box-shadow:0 6px 18px rgba(0,0,0,.35)}
      .t{position:absolute;display:none;max-width:380px;font:13.5px/1.45 "Segoe UI",sans-serif;color:#e8eaf2;background:#262833;border:1px solid rgba(255,255,255,.12);
         border-radius:12px;padding:10px 12px;box-shadow:0 12px 32px rgba(0,0,0,.45);white-space:pre-wrap}
      .t small{display:block;color:#959bb0;font-size:11px;margin-top:6px}
    </style><button class="b">文A <span>Перевести</span></button><div class="t"></div>`;
    btn = root.querySelector('.b');
    tip = root.querySelector('.t');
    btn.addEventListener('mousedown', e => { e.preventDefault(); e.stopPropagation(); });
    btn.addEventListener('click', e => {
      e.stopPropagation();
      btn.style.display = 'none';
      tip.textContent = '…';
      tip.style.display = 'block';
      tip.style.left = btn.style.left; tip.style.top = btn.style.top;
      try { chrome.webview.postMessage({ type: 'lite-translate', text: lastText }); } catch (err) { }
    });
    document.documentElement.appendChild(host);
  };
  const hide = () => { if (btn) btn.style.display = 'none'; if (tip) tip.style.display = 'none'; };
  document.addEventListener('mouseup', e => {
    if (e.button !== 0) return;
    setTimeout(() => {
      const sel = window.getSelection();
      const text = sel ? sel.toString().trim() : '';
      if (!text || text.length > 1500 || /^[\d\s.,:;!?()-]+$/.test(text)) return;
      const a = document.activeElement;
      if (a && (a.isContentEditable || /^(INPUT|TEXTAREA)$/.test(a.tagName))) return;
      const r = sel.getRangeAt(0).getBoundingClientRect();
      if (!r.width && !r.height) return;
      ensure();
      lastText = text;
      tip.style.display = 'none';
      btn.style.left = Math.max(4, scrollX + r.left) + 'px';
      btn.style.top = (scrollY + r.bottom + 8) + 'px';
      btn.style.display = 'flex';
    }, 10);
  }, true);
  document.addEventListener('mousedown', e => { if (host && !e.composedPath().includes(host)) hide(); }, true);
  document.addEventListener('scroll', () => { if (btn) btn.style.display = 'none'; }, true);
  window.__liteTr = (text, note) => {
    if (!tip) return;
    tip.textContent = text;
    if (note) { const s = document.createElement('small'); s.textContent = note; tip.appendChild(s); }
    tip.style.display = 'block';
  };
})();
""";

    /// <summary>Перевод через Google (тот же сервис, что «Перевести страницу»). Если не на русском — на русский, иначе на английский.</summary>
    public static async Task<(string text, string from)> TranslateAsync(string text, string uiLang)
    {
        string target = uiLang == "en" ? "en" : "ru";
        var (t, from) = await Google(text, target);
        if (from == target) (t, from) = await Google(text, target == "ru" ? "en" : "ru");
        return (t, from);
    }

    static async Task<(string, string)> Google(string text, string to)
    {
        try { return await GoogleGtx(text, to); }
        catch (HttpRequestException)
        {
            // Запасной адрес (тот, что использует расширение Google Переводчика)
            var url2 = "https://clients5.google.com/translate_a/t?client=dict-chrome-ex&sl=auto&tl=" + to + "&q=" + Uri.EscapeDataString(text);
            using var d2 = JsonDocument.Parse(await Http.GetStringAsync(url2));
            var first = d2.RootElement[0];
            return first.ValueKind == JsonValueKind.Array
                ? (first[0].GetString() ?? "", first.GetArrayLength() > 1 ? first[1].GetString() ?? "" : "")
                : (first.GetString() ?? "", "");
        }
    }

    static async Task<(string, string)> GoogleGtx(string text, string to)
    {
        var url = "https://translate.googleapis.com/translate_a/single?client=gtx&sl=auto&dt=t&ie=UTF-8&oe=UTF-8&tl=" + to + "&q=" + Uri.EscapeDataString(text);
        var json = await Http.GetStringAsync(url);
        using var d = JsonDocument.Parse(json);
        var parts = d.RootElement[0].EnumerateArray().Select(p => p[0].ValueKind == JsonValueKind.String ? p[0].GetString() : "").ToList();
        var from = d.RootElement.GetArrayLength() > 2 && d.RootElement[2].ValueKind == JsonValueKind.String ? d.RootElement[2].GetString() ?? "" : "";
        return (string.Concat(parts), from);
    }
}
