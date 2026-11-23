using System.Text.Json;

namespace LiteBrowser;

/// <summary>
/// Стикеры на страницах: заметка поверх сайта в нужном месте, её можно двигать и править; появляется снова при следующем заходе.
/// Хранятся только на этом компьютере (stickies.json), в инкогнито не создаются.
/// Рисуются в изолированном теневом DOM — стили сайта на них не влияют, сайт не видит их текст через обычный DOM.
/// </summary>
sealed class Sticky
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..12];
    public string Url { get; set; } = "";   // Store.PageKey — адрес без #якоря
    public int X { get; set; }               // пиксели от левого края документа
    public int Y { get; set; }               // пиксели от верха документа
    public string Text { get; set; } = "";
    public DateTime Time { get; set; } = DateTime.Now;
}

static class Stickies
{
    static List<Sticky>? all;
    static string FilePath => Path.Combine(AppEnv.DataDir, "stickies.json");

    static List<Sticky> All()
    {
        if (all != null) return all;
        try { all = File.Exists(FilePath) ? JsonSerializer.Deserialize<List<Sticky>>(File.ReadAllText(FilePath)) ?? new() : new(); }
        catch { all = new(); }
        return all;
    }

    static void Save()
    {
        try { var tmp = FilePath + ".tmp"; File.WriteAllText(tmp, JsonSerializer.Serialize(All())); File.Move(tmp, FilePath, true); } catch { }
    }

    public static List<Sticky> For(string pageKey) => All().Where(s => s.Url == pageKey).ToList();

    public static Sticky Add(string pageKey, int x, int y)
    {
        var s = new Sticky { Url = pageKey, X = Math.Max(0, x), Y = Math.Max(0, y) };
        All().Add(s);
        if (all!.Count > 3000) all.RemoveAt(0);
        Save();
        return s;
    }

    /// <summary>Обновить стикер — только если он принадлежит этой же странице (сайт не может трогать чужие стикеры).</summary>
    public static void Update(string pageKey, string id, string text, int x, int y)
    {
        var s = All().FirstOrDefault(a => a.Id == id && a.Url == pageKey);
        if (s == null) return;
        s.Text = text.Length > 2000 ? text[..2000] : text; s.X = Math.Max(0, x); s.Y = Math.Max(0, y); s.Time = DateTime.Now;
        Save();
    }

    public static void Delete(string pageKey, string id)
    {
        if (All().RemoveAll(a => a.Id == id && a.Url == pageKey) > 0) Save();
    }

    /// <summary>Скрипт: нарисовать стикеры страницы (focusId — сразу поставить курсор в новый).</summary>
    public static string Script(IEnumerable<Sticky> notes, string? focusId = null) =>
        Template.Replace("__NOTES__", JsonSerializer.Serialize(notes.Select(n => new { id = n.Id, x = n.X, y = n.Y, text = n.Text })))
                .Replace("__FOCUS__", JsonSerializer.Serialize(focusId ?? ""));

    const string Template = """
(() => {
  const notes = __NOTES__, focusId = __FOCUS__;
  let host = document.getElementById('lite-stickies');
  if (host) host.remove();
  if (!notes.length) return 0;
  host = document.createElement('div'); host.id = 'lite-stickies';
  host.style.cssText = 'position:absolute;left:0;top:0;width:0;height:0;z-index:2147483600';
  (document.body || document.documentElement).appendChild(host);
  const root = host.attachShadow({ mode: 'closed' });
  const style = document.createElement('style');
  style.textContent = `
    .n{position:absolute;width:200px;min-height:110px;background:#fff6a8;color:#3a3200;border-radius:10px;box-shadow:0 8px 28px rgba(0,0,0,.28);font:13px/1.4 "Segoe UI",system-ui,sans-serif;display:flex;flex-direction:column}
    .h{height:22px;cursor:move;display:flex;justify-content:flex-end;align-items:center;padding:0 4px;border-radius:10px 10px 0 0;background:rgba(0,0,0,.06)}
    .x{border:0;background:none;cursor:pointer;font-size:14px;color:#6b5d00;width:20px;height:20px;border-radius:5px}
    .x:hover{background:rgba(0,0,0,.1)}
    textarea{flex:1;border:0;background:transparent;resize:vertical;padding:6px 10px 10px;font:inherit;color:inherit;outline:none;min-height:80px}`;
  root.appendChild(style);
  const send = m => { try { window.chrome.webview.postMessage(m); } catch (e) { } };
  for (const n of notes) {
    const el = document.createElement('div'); el.className = 'n';
    el.style.left = n.x + 'px'; el.style.top = n.y + 'px';
    el.innerHTML = '<div class="h"><button class="x" title="Удалить стикер">✕</button></div><textarea placeholder="Заметка…" maxlength="2000"></textarea>';
    const ta = el.querySelector('textarea'); ta.value = n.text;
    let timer = 0;
    const save = () => send({ type: 'sticky:save', id: n.id, text: ta.value, x: parseInt(el.style.left), y: parseInt(el.style.top) });
    ta.addEventListener('input', () => { clearTimeout(timer); timer = setTimeout(save, 500); });
    el.querySelector('.x').onclick = () => { el.remove(); send({ type: 'sticky:del', id: n.id }); };
    // Перетаскивание за шапку
    el.querySelector('.h').addEventListener('mousedown', e => {
      if (e.target.closest('.x')) return;
      e.preventDefault();
      const sx = e.pageX - parseInt(el.style.left), sy = e.pageY - parseInt(el.style.top);
      const move = ev => { el.style.left = Math.max(0, ev.pageX - sx) + 'px'; el.style.top = Math.max(0, ev.pageY - sy) + 'px'; };
      const up = () => { removeEventListener('mousemove', move, true); removeEventListener('mouseup', up, true); save(); };
      addEventListener('mousemove', move, true); addEventListener('mouseup', up, true);
    });
    root.appendChild(el);
    if (n.id === focusId) setTimeout(() => ta.focus(), 50);
  }
  return notes.length;
})()
""";
}
