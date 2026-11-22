using System.IO.Compression;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Web.WebView2.Core;

namespace LiteBrowser;

sealed record StoreRef(string Store, string Id);   // Store: chrome | edge | firefox

/// <summary>Сведения о расширении из manifest.json.</summary>
sealed class ExtensionInfo
{
    public string Folder { get; set; } = "";
    public string Store { get; set; } = "";
    public string StoreId { get; set; } = "";
    public string Name { get; set; } = "";
    public string Version { get; set; } = "";
    public string Description { get; set; } = "";
    public int ManifestVersion { get; set; }
    public string? Popup { get; set; }
    public string? Options { get; set; }
    public string? IconFile { get; set; }
    public List<string> Permissions { get; set; } = new();
}

/// <summary>
/// Расширения из Chrome Web Store, Microsoft Edge Add-ons и Firefox Add-ons.
/// WebView2 не показывает значки и всплывающие окна расширений, поэтому окно и настройки расширения открываются во вкладке.
/// </summary>
static class Extensions
{
    /// <summary>Только для самотеста: другая папка расширений.</summary>
    public static string? DirOverride { get; set; }
    static string Dir => DirOverride ?? Path.Combine(AppEnv.DataDir, "extensions");
    static string MetaFile => Path.Combine(Dir, "extensions.json");
    static readonly Regex ChromeRx = new(@"^https?://(chromewebstore\.google\.com/detail|chrome\.google\.com/webstore/detail)/(?:[^/?#]+/)?([a-p]{32})", RegexOptions.IgnoreCase);
    static readonly Regex EdgeRx = new(@"^https?://microsoftedge\.microsoft\.com/addons/detail/(?:[^/?#]+/)?([a-z]{32})", RegexOptions.IgnoreCase);
    static readonly Regex FirefoxRx = new(@"^https?://addons\.mozilla\.org/(?:[^/?#]+/)?(?:firefox|android)/addon/([^/?#]+)", RegexOptions.IgnoreCase);

    public static readonly (string name, string desc, string url)[] Recommended =
    {
        ("uBlock Origin Lite", "Ещё один блокировщик рекламы", "https://chromewebstore.google.com/detail/ddkjiahejlhfcafbddmgiahcphecmpfh"),
        ("Dark Reader", "Тёмная тема для любого сайта", "https://chromewebstore.google.com/detail/eimadpbcbfnmbkopoojfekhnkhdbieeh"),
        ("SponsorBlock", "Пропуск рекламных вставок в видео YouTube", "https://chromewebstore.google.com/detail/mnjggcdmjocbbbhaepdhchncahnbgone"),
        ("Return YouTube Dislike", "Показывает дизлайки на YouTube", "https://chromewebstore.google.com/detail/gebbhagfogifgggkldgodflihgfeippi"),
        ("Bitwarden", "Менеджер паролей (работает частично)", "https://chromewebstore.google.com/detail/nngceckbapebfimnlniiiahkandclblb"),
    };

    public static StoreRef? Parse(string url)
    {
        url = url.Trim();
        var m = ChromeRx.Match(url);
        if (m.Success) return new StoreRef("chrome", m.Groups[2].Value.ToLowerInvariant());
        m = EdgeRx.Match(url);
        if (m.Success) return new StoreRef("edge", m.Groups[1].Value.ToLowerInvariant());
        m = FirefoxRx.Match(url);
        if (m.Success) return new StoreRef("firefox", Uri.UnescapeDataString(m.Groups[1].Value));
        if (Regex.IsMatch(url, "^[a-p]{32}$")) return new StoreRef("chrome", url);
        return null;
    }

    public static string StoreName(string store) => store switch { "chrome" => "Chrome Web Store", "edge" => "Edge Add-ons", "firefox" => "Firefox Add-ons", _ => store };

    // ---------- Сохранённые сведения ----------

    public static Dictionary<string, ExtensionInfo> LoadMeta()
    {
        try { return File.Exists(MetaFile) ? JsonSerializer.Deserialize<Dictionary<string, ExtensionInfo>>(File.ReadAllText(MetaFile), AppSettings.Json) ?? new() : new(); }
        catch { return new(); }
    }

    static void SaveMeta(Dictionary<string, ExtensionInfo> meta)
    {
        Directory.CreateDirectory(Dir);
        File.WriteAllText(MetaFile, JsonSerializer.Serialize(meta, AppSettings.Json));
    }

    // ---------- Скачивание ----------

    static HttpClient Http()
    {
        var s = AppEnv.Settings;
        var handler = new HttpClientHandler { AutomaticDecompression = DecompressionMethods.All, AllowAutoRedirect = false };
        if (s.ProxyMode == "all")
            handler.Proxy = new WebProxy($"{(s.ProxyType == "http" ? "http" : "socks5")}://{s.ProxyHost}:{s.ProxyPort}");
        var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(90) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/157.0 Safari/537.36");
        return http;
    }

    static async Task<byte[]> GetLimited(HttpClient http, string url)
    {
        // Перенаправления обрабатываем сами: на HTTP разрешаем только серверы доставки Microsoft
        // (CRX потом всё равно проверяется по цифровой подписи, так что подменить файл нельзя)
        HttpResponseMessage resp;
        for (int hop = 0; ; hop++)
        {
            resp = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
            if ((int)resp.StatusCode is < 300 or >= 400 || resp.Headers.Location == null) break;
            var next = new Uri(new Uri(url), resp.Headers.Location);
            resp.Dispose();
            bool msDelivery = next.Host.EndsWith(".microsoft.com", StringComparison.OrdinalIgnoreCase);
            if (hop > 6 || (next.Scheme != "https" && !(next.Scheme == "http" && msDelivery)))
                throw new InvalidOperationException("Магазин перенаправил загрузку на недоверенный адрес");
            url = next.ToString();
        }
        using var _ = resp;
        if (resp.StatusCode == HttpStatusCode.NoContent || resp.StatusCode == HttpStatusCode.NotFound)
            throw new InvalidOperationException("Расширение не найдено в магазине (или недоступно для этой версии браузера)");
        resp.EnsureSuccessStatusCode();
        using var s = await resp.Content.ReadAsStreamAsync();
        using var ms = new MemoryStream();
        var buf = new byte[81920];
        int n;
        while ((n = await s.ReadAsync(buf)) > 0)
        {
            ms.Write(buf, 0, n);
            if (ms.Length > 150_000_000) throw new InvalidDataException("Расширение слишком большое");
        }
        return ms.ToArray();
    }

    /// <summary>Скачивает и распаковывает расширение во временную папку, возвращает его сведения (ещё не установлено).</summary>
    public static async Task<ExtensionInfo> Download(StoreRef r, string browserVersion)
    {
        using var http = Http();
        byte[] data;
        if (r.Store == "firefox")
        {
            var api = await http.GetStringAsync($"https://addons.mozilla.org/api/v5/addons/addon/{Uri.EscapeDataString(r.Id)}/");
            using var doc = JsonDocument.Parse(api);
            var fileUrl = doc.RootElement.GetProperty("current_version").GetProperty("file").GetProperty("url").GetString()
                          ?? throw new InvalidDataException("В магазине нет файла расширения");
            data = await GetLimited(http, fileUrl);
        }
        else
        {
            var ver = string.IsNullOrEmpty(browserVersion) ? "157.0.0.0" : browserVersion.Split(' ')[0];
            var url = r.Store == "edge"
                ? $"https://edge.microsoft.com/extensionwebstorebase/v1/crx?response=redirect&prod=chromiumcrx&prodchannel=&prodversion={ver}&x=id%3D{r.Id}%26installsource%3Dondemand%26uc"
                : $"https://clients2.google.com/service/update2/crx?response=redirect&prodversion={ver}&acceptformat=crx2,crx3&x=id%3D{r.Id}%26uc";
            data = await GetLimited(http, url);
        }

        int offset = ZipOffset(data);
        if (r.Store != "firefox") CrxVerifier.Verify(data, r.Id);
        var tmp = Path.Combine(Dir, ".tmp-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmp);
        try
        {
            using (var ms = new MemoryStream(data, offset, data.Length - offset))
                ZipFile.ExtractToDirectory(ms, tmp); // .NET сам защищает от путей вида ../ в архиве
            foreach (var reserved in new[] { "_metadata", "META-INF" })
            {
                var p = Path.Combine(tmp, reserved);
                if (Directory.Exists(p)) Directory.Delete(p, true);
            }
            var info = ReadManifest(tmp);
            info.Store = r.Store;
            info.StoreId = r.Id;
            return info;
        }
        catch
        {
            try { Directory.Delete(tmp, true); } catch { }
            throw;
        }
    }

    /// <summary>CRX — это zip с заголовком; находим, где начинается сам архив.</summary>
    static int ZipOffset(byte[] b)
    {
        if (b.Length > 16 && b[0] == 'C' && b[1] == 'r' && b[2] == '2' && b[3] == '4')
        {
            uint version = BitConverter.ToUInt32(b, 4);
            if (version == 3) return checked((int)(12 + BitConverter.ToUInt32(b, 8)));
            if (version == 2) return checked((int)(16 + BitConverter.ToUInt32(b, 8) + BitConverter.ToUInt32(b, 12)));
            throw new InvalidDataException("Неизвестный формат CRX");
        }
        if (b.Length > 4 && b[0] == 'P' && b[1] == 'K') return 0;
        throw new InvalidDataException("Магазин вернул не расширение (возможно, оно недоступно в вашей стране)");
    }

    static ExtensionInfo ReadManifest(string folder)
    {
        var path = Path.Combine(folder, "manifest.json");
        if (!File.Exists(path)) throw new InvalidDataException("В архиве нет manifest.json");
        using var doc = JsonDocument.Parse(File.ReadAllText(path), new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
        var m = doc.RootElement;
        string Str(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

        var info = new ExtensionInfo
        {
            Folder = folder,
            ManifestVersion = m.TryGetProperty("manifest_version", out var mv) && mv.TryGetInt32(out var mvi) ? mvi : 0,
            Version = Str(m, "version"),
        };
        var locale = Str(m, "default_locale");
        info.Name = Localize(folder, locale, Str(m, "name"));
        info.Description = Localize(folder, locale, Str(m, "description"));

        foreach (var key in new[] { "action", "browser_action", "page_action" })
            if (m.TryGetProperty(key, out var a) && a.ValueKind == JsonValueKind.Object && Str(a, "default_popup") is { Length: > 0 } pp) { info.Popup = pp; break; }
        if (Str(m, "options_page") is { Length: > 0 } op) info.Options = op;
        else if (m.TryGetProperty("options_ui", out var ou) && Str(ou, "page") is { Length: > 0 } oup) info.Options = oup;

        if (m.TryGetProperty("icons", out var icons) && icons.ValueKind == JsonValueKind.Object)
            info.IconFile = icons.EnumerateObject().OrderByDescending(p => int.TryParse(p.Name, out var s) ? s : 0)
                .Select(p => p.Value.GetString()).FirstOrDefault(s => !string.IsNullOrEmpty(s));

        foreach (var key in new[] { "permissions", "host_permissions", "optional_host_permissions" })
            if (m.TryGetProperty(key, out var perms) && perms.ValueKind == JsonValueKind.Array)
                foreach (var p in perms.EnumerateArray())
                    if (p.ValueKind == JsonValueKind.String && key != "optional_host_permissions") info.Permissions.Add(p.GetString()!);
        if (m.TryGetProperty("content_scripts", out var cs) && cs.ValueKind == JsonValueKind.Array)
            foreach (var c in cs.EnumerateArray())
                if (c.TryGetProperty("matches", out var mt) && mt.ValueKind == JsonValueKind.Array)
                    foreach (var x in mt.EnumerateArray()) if (x.ValueKind == JsonValueKind.String) info.Permissions.Add(x.GetString()!);
        info.Permissions = info.Permissions.Distinct().ToList();

        // Проверки совместимости с Chromium
        if (info.ManifestVersion == 2)
            throw new NotSupportedException("Это расширение старого формата (Manifest V2) — современный Chromium его больше не поддерживает. Поищите версию этого расширения для Chrome.");
        if (m.TryGetProperty("background", out var bg) && bg.TryGetProperty("scripts", out _) && !bg.TryGetProperty("service_worker", out _))
            throw new NotSupportedException("Это расширение сделано только для Firefox (фоновые скрипты в формате Firefox). Поищите версию для Chrome или Edge.");
        return info;
    }

    static string Localize(string folder, string defLocale, string value)
    {
        var m = Regex.Match(value, @"^__MSG_(\w+)__$");
        if (!m.Success) return value;
        var key = m.Groups[1].Value;
        foreach (var loc in new[] { "ru", defLocale, "en", "en_US" }.Where(l => l.Length > 0))
        {
            var f = Path.Combine(folder, "_locales", loc, "messages.json");
            if (!File.Exists(f)) continue;
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(f), new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
                foreach (var p in doc.RootElement.EnumerateObject())
                    if (p.Name.Equals(key, StringComparison.OrdinalIgnoreCase) && p.Value.TryGetProperty("message", out var msg))
                        return msg.GetString() ?? value;
            }
            catch { }
        }
        return key;
    }

    /// <summary>Понятное описание прав расширения.</summary>
    public static List<string> DescribePermissions(ExtensionInfo info)
    {
        var res = new List<string>();
        var p = info.Permissions;
        if (p.Any(x => x is "<all_urls>" or "*://*/*" or "http://*/*" or "https://*/*")) res.Add("читать и изменять данные на ВСЕХ сайтах");
        else
        {
            var hosts = p.Where(x => x.Contains("://")).Select(x => Regex.Replace(x, @"^\*?:?//|/\*$|^https?://|^\*://", "")).Distinct().Take(6).ToList();
            if (hosts.Count > 0) res.Add("доступ к сайтам: " + string.Join(", ", hosts));
        }
        var map = new Dictionary<string, string>
        {
            ["tabs"] = "видеть открытые вкладки", ["history"] = "читать историю", ["bookmarks"] = "читать и менять закладки",
            ["downloads"] = "управлять загрузками", ["cookies"] = "читать cookies", ["webRequest"] = "отслеживать сетевые запросы",
            ["declarativeNetRequest"] = "блокировать и изменять запросы", ["clipboardRead"] = "читать буфер обмена",
            ["nativeMessaging"] = "запускать программы на компьютере", ["management"] = "управлять другими расширениями",
            ["proxy"] = "менять прокси", ["privacy"] = "менять настройки приватности", ["scripting"] = "внедрять скрипты на страницы",
        };
        foreach (var kv in map) if (p.Contains(kv.Key)) res.Add(kv.Value);
        if (res.Count == 0) res.Add("особых прав не требует");
        return res;
    }

    /// <summary>Устанавливает скачанное расширение в профиль браузера.</summary>
    public static async Task<CoreWebView2BrowserExtension> Install(CoreWebView2Profile profile, ExtensionInfo info)
    {
        var final = Path.Combine(Dir, $"{info.Store}-{Regex.Replace(info.StoreId, @"[^\w\-]", "_")}");
        var meta = LoadMeta();
        // Удаляем старую версию, если обновляем
        foreach (var ext in await profile.GetBrowserExtensionsAsync())
        {
            if (meta.TryGetValue(ext.Id, out var old) && old.Folder.Equals(final, StringComparison.OrdinalIgnoreCase))
            {
                try { await ext.RemoveAsync(); } catch { }
                meta.Remove(ext.Id);
            }
        }
        if (Directory.Exists(final)) Directory.Delete(final, true);
        Directory.Move(info.Folder, final);
        info.Folder = final;
        try
        {
            var added = await profile.AddBrowserExtensionAsync(final);
            meta[added.Id] = info;
            SaveMeta(meta);
            return added;
        }
        catch (Exception ex)
        {
            try { Directory.Delete(final, true); } catch { }
            throw new InvalidOperationException("Движок не смог загрузить расширение: " + ex.Message);
        }
    }

    public static void Discard(ExtensionInfo info)
    {
        try { if (info.Folder.Contains(".tmp-")) Directory.Delete(info.Folder, true); } catch { }
    }

    public static async Task Remove(CoreWebView2Profile profile, string id)
    {
        var meta = LoadMeta();
        foreach (var ext in await profile.GetBrowserExtensionsAsync())
            if (ext.Id == id) await ext.RemoveAsync();
        if (meta.Remove(id, out var info))
        {
            try { Directory.Delete(info.Folder, true); } catch { }
            SaveMeta(meta);
        }
    }

    public static string? IconData(ExtensionInfo info)
    {
        try
        {
            if (info.IconFile == null) return null;
            var f = Path.GetFullPath(Path.Combine(info.Folder, info.IconFile.TrimStart('/')));
            if (!f.StartsWith(Path.GetFullPath(info.Folder), StringComparison.OrdinalIgnoreCase) || !File.Exists(f)) return null;
            if (new FileInfo(f).Length > 300_000) return null;
            var mime = f.EndsWith(".svg", StringComparison.OrdinalIgnoreCase) ? "image/svg+xml" : "image/png";
            return $"data:{mime};base64,{Convert.ToBase64String(File.ReadAllBytes(f))}";
        }
        catch { return null; }
    }
}
