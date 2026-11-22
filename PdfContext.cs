using System.Text;
using Microsoft.Web.WebView2.Core;
using UglyToad.PdfPig;

namespace LiteBrowser;

/// <summary>Текст PDF, открытого во вкладке, — чтобы ИИ мог пересказать документ и ответить на вопросы по нему.</summary>
static class PdfContext
{
    static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(30) };
    const long MaxBytes = 60L << 20;

    /// <summary>Открыт ли во вкладке PDF (встроенный просмотрщик движка).</summary>
    public static async Task<bool> IsPdfAsync(CoreWebView2 core)
    {
        var src = core.Source;
        if (src.Split('?', '#')[0].EndsWith(".pdf", StringComparison.OrdinalIgnoreCase)) return true;
        try { return (await core.ExecuteScriptAsync("document.contentType")).Contains("pdf"); }
        catch { return false; }
    }

    static async Task<byte[]> LoadAsync(string url)
    {
        var u = new Uri(url);
        if (u.IsFile)
        {
            var fi = new FileInfo(u.LocalPath);
            if (fi.Length > MaxBytes) throw new InvalidOperationException("PDF слишком большой");
            return await File.ReadAllBytesAsync(u.LocalPath);
        }
        if (u.Scheme is not ("http" or "https")) throw new InvalidOperationException("Этот PDF не получается прочитать");
        using var resp = await Http.GetAsync(u, HttpCompletionOption.ResponseHeadersRead);
        resp.EnsureSuccessStatusCode();
        if (resp.Content.Headers.ContentLength > MaxBytes) throw new InvalidOperationException("PDF слишком большой");
        return await resp.Content.ReadAsByteArrayAsync();
    }

    /// <summary>Текст документа постранично («[стр. N]»), не длиннее budget символов. Если не помещается — начало и конец.</summary>
    public static async Task<(string title, string text, int pages)> ReadAsync(string url, int budget)
    {
        var bytes = await LoadAsync(url);
        return await Task.Run(() => Extract(bytes, budget));
    }

    public static (string title, string text, int pages) Extract(byte[] bytes, int budget)
    {
        using var doc = PdfDocument.Open(bytes);
        var title = doc.Information?.Title ?? "";
        var parts = new List<string>();
        foreach (var page in doc.GetPages())
        {
            string t;
            try { t = string.Join(" ", page.GetWords().Select(w => w.Text)); }
            catch { t = page.Text; }
            if (t.Trim().Length > 0) parts.Add($"[стр. {page.Number}] {t}");
        }
        var all = string.Join("\n\n", parts);
        if (all.Length > budget)
        {
            // Начало документа важнее, но и конец (выводы) тоже нужен
            int head = budget * 3 / 4, tail = budget - head;
            all = all[..head] + "\n\n[… середина документа пропущена …]\n\n" + all[^tail..];
        }
        return (title, all, doc.NumberOfPages);
    }
}
