using System.Text.Json;
using Microsoft.Web.WebView2.Core;

namespace LiteBrowser;

sealed class OfflinePage
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Url { get; set; } = "";
    public string Title { get; set; } = "";
    public DateTime Time { get; set; } = DateTime.Now;
    public long Size { get; set; }
}

/// <summary>
/// Страницы для чтения без интернета: снимок целиком (текст, картинки, стили) в одном файле MHTML.
/// Открывается из файла без скриптов — безопасно и работает без сети.
/// </summary>
static class Offline
{
    static string? dirOverride;
    public static string Dir => dirOverride ?? Path.Combine(AppEnv.DataDir, "offline");
    static string IndexFile => Path.Combine(Dir, "index.json");
    static List<OfflinePage>? items;

    public static void UseDir(string dir) { dirOverride = dir; items = null; }

    public static List<OfflinePage> All
    {
        get
        {
            if (items != null) return items;
            try { items = File.Exists(IndexFile) ? JsonSerializer.Deserialize<List<OfflinePage>>(File.ReadAllText(IndexFile), AppSettings.Json) : null; }
            catch { }
            items ??= new();
            items.RemoveAll(p => !File.Exists(PathOf(p)));
            return items;
        }
    }

    public static string PathOf(OfflinePage p) => Path.Combine(Dir, p.Id + ".mhtml");
    public static string FileUrl(OfflinePage p) => new Uri(PathOf(p)).AbsoluteUri;
    public static OfflinePage? Get(string id) => All.FirstOrDefault(p => p.Id == id);
    public static OfflinePage? ByFileUrl(string url) => All.FirstOrDefault(p => FileUrl(p).Equals(url, StringComparison.OrdinalIgnoreCase));
    public static OfflinePage? ByUrl(string url) => All.FirstOrDefault(p => p.Url == url);

    static void SaveIndex()
    {
        Directory.CreateDirectory(Dir);
        var tmp = IndexFile + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(All, AppSettings.Json));
        File.Move(tmp, IndexFile, true);
    }

    /// <summary>Сохранить открытую страницу. Повторное сохранение того же адреса заменяет старый снимок.</summary>
    public static async Task<OfflinePage> SaveAsync(CoreWebView2 core, string title)
    {
        var url = core.Source;
        if (!url.StartsWith("http", StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Сохранить можно только обычную веб-страницу");
        var json = await core.CallDevToolsProtocolMethodAsync("Page.captureSnapshot", "{\"format\":\"mhtml\"}");
        using var d = JsonDocument.Parse(json);
        var data = d.RootElement.GetProperty("data").GetString() ?? "";
        if (data.Length == 0) throw new InvalidOperationException("Не удалось сохранить страницу");
        Directory.CreateDirectory(Dir);
        var old = ByUrl(url);
        var p = old ?? new OfflinePage { Url = url };
        p.Title = string.IsNullOrWhiteSpace(title) ? url : title;
        p.Time = DateTime.Now;
        // MHTML — текст (картинки внутри в base64)
        await File.WriteAllTextAsync(PathOf(p), data, new System.Text.UTF8Encoding(false));
        p.Size = new FileInfo(PathOf(p)).Length;
        if (old == null) All.Insert(0, p);
        SaveIndex();
        return p;
    }

    public static void Remove(string id)
    {
        var p = Get(id);
        if (p == null) return;
        try { File.Delete(PathOf(p)); } catch { }
        All.Remove(p);
        SaveIndex();
    }

    public static long TotalSize => All.Sum(p => p.Size);
}
