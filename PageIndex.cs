using Microsoft.Web.WebView2.Core;
using System.Text.Json;

namespace LiteBrowser;

/// <summary>
/// Поиск по истории по тексту страниц: «где я читал про видеокарты».
/// С каждой посещённой страницы запоминаем начало текста (до 3000 символов) — только на этом компьютере, в папке данных.
/// Страницы с полями пароля или номера карты не запоминаем; в инкогнито и при выключенной истории — ничего.
/// </summary>
static class PageIndex
{
    public sealed class Page
    {
        public string Url { get; set; } = "";
        public string Title { get; set; } = "";
        public string Text { get; set; } = "";
        public DateTime Time { get; set; }
    }

    const int MaxPages = 2000, MaxText = 3000;
    static readonly object sync = new();
    static List<Page>? pages;
    static bool dirty;
    static string FilePath => Path.Combine(AppEnv.DataDir, "pagetext.json");

    static List<Page> All()
    {
        lock (sync)
        {
            if (pages != null) return pages;
            try { pages = File.Exists(FilePath) ? JsonSerializer.Deserialize<List<Page>>(File.ReadAllText(FilePath)) ?? new() : new(); }
            catch { pages = new(); }
            return pages;
        }
    }

    /// <summary>Скрипт: текст страницы или null, если на ней вводят пароль/карту.</summary>
    public const string ExtractJs = """
(() => {
  if (document.querySelector('input[type=password], input[autocomplete^="cc-"], input[name*="card" i][inputmode="numeric"]')) return null;
  const t = (document.body ? document.body.innerText : '').replace(/\s+/g, ' ').trim();
  return t.slice(0, 3000);
})()
""";

    /// <summary>Снять текст со страницы через пару секунд после загрузки (когда она дорисовалась).</summary>
    public static async Task CaptureAsync(CoreWebView2 core, string url)
    {
        try
        {
            await Task.Delay(2000);
            if (core.Source != url) return;
            var raw = await core.ExecuteScriptAsync(ExtractJs);
            var text = JsonSerializer.Deserialize<string?>(raw);
            if (string.IsNullOrWhiteSpace(text) || text.Length < 40) return;
            Add(url, core.DocumentTitle, text);
        }
        catch { }
    }

    public static void Add(string url, string title, string text)
    {
        if (text.Length > MaxText) text = text[..MaxText];
        var list = All();
        lock (sync)
        {
            list.RemoveAll(p => p.Url == url);
            list.Add(new Page { Url = url, Title = title, Text = text, Time = DateTime.Now });
            if (list.Count > MaxPages) list.RemoveRange(0, list.Count - MaxPages);
            dirty = true;
        }
    }

    public static void Remove(string url) { var l = All(); lock (sync) { if (l.RemoveAll(p => p.Url == url) > 0) dirty = true; } }

    public static void Clear()
    {
        lock (sync) { pages = new(); dirty = false; }
        try { File.Delete(FilePath); } catch { }
    }

    public static void Flush()
    {
        List<Page> copy;
        lock (sync) { if (!dirty || pages == null) return; dirty = false; copy = pages.ToList(); }
        try
        {
            var tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(copy));
            File.Move(tmp, FilePath, true);
        }
        catch { }
    }

    /// <summary>Страницы, где встречаются все слова запроса; с отрывком текста вокруг первого слова.</summary>
    public static List<(Page page, string snippet)> Search(string q, int limit)
    {
        var words = q.ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Where(w => w.Length > 1).ToArray();
        if (words.Length == 0) return new();
        var list = All();
        List<Page> copy;
        lock (sync) copy = list.ToList();
        var found = new List<(Page, string, int)>();
        foreach (var p in copy)
        {
            var hay = p.Text.ToLowerInvariant();
            var title = p.Title.ToLowerInvariant();
            if (!words.All(w => hay.Contains(w) || title.Contains(w))) continue;
            int score = words.Sum(w => Count(hay, w) + (title.Contains(w) ? 5 : 0));
            found.Add((p, Snippet(p.Text, hay, words), score));
        }
        // Сначала самые подходящие, при равенстве — свежие
        return found.OrderByDescending(f => f.Item3).ThenByDescending(f => f.Item1.Time).Take(limit).Select(f => (f.Item1, f.Item2)).ToList();
    }

    static int Count(string hay, string w)
    {
        int n = 0, i = 0;
        while ((i = hay.IndexOf(w, i, StringComparison.Ordinal)) >= 0 && n < 20) { n++; i += w.Length; }
        return n;
    }

    internal static string Snippet(string text, string lower, string[] words)
    {
        int at = words.Select(w => lower.IndexOf(w, StringComparison.Ordinal)).Where(i => i >= 0).DefaultIfEmpty(0).Min();
        int start = Math.Max(0, at - 60), end = Math.Min(text.Length, at + 140);
        // не режем слово пополам
        for (int k = 0; k < 20 && start > 0 && !char.IsWhiteSpace(text[start - 1]); k++) start--;
        for (int k = 0; k < 20 && end < text.Length && !char.IsWhiteSpace(text[end]); k++) end++;
        return (start > 0 ? "…" : "") + text[start..end].Trim() + (end < text.Length ? "…" : "");
    }
}
