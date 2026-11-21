using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;

namespace LiteBrowser;

/// <summary>Найденный профиль другого браузера.</summary>
sealed record BrowserProfile(string Id, string Browser, string Profile, string Dir, bool Firefox)
{
    public bool HasBookmarks => Firefox ? File.Exists(Path.Combine(Dir, "places.sqlite")) : File.Exists(Path.Combine(Dir, "Bookmarks"));
    public bool HasHistory => File.Exists(Path.Combine(Dir, Firefox ? "places.sqlite" : "History"));
}

/// <summary>
/// Перенос закладок и истории из Chrome, Edge, Яндекс Браузера, Opera, Brave, Vivaldi и Firefox,
/// а также экспорт/импорт закладок в HTML (формат, который понимают все браузеры).
/// Пароли и cookies не переносятся: браузеры шифруют их, и LiteBrowser их не хранит.
/// </summary>
static class Importer
{
    const int MaxHistory = 5000;
    static bool sqliteReady;

    static void InitSqlite()
    {
        if (sqliteReady) return;
        SQLitePCL.Batteries_V2.Init(); // встроенная в Windows winsqlite3.dll — ничего лишнего в exe
        sqliteReady = true;
    }

    public static List<BrowserProfile> Scan()
    {
        var list = new List<BrowserProfile>();
        string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);

        var chromium = new (string name, string dir)[]
        {
            ("Google Chrome", Path.Combine(local, @"Google\Chrome\User Data")),
            ("Microsoft Edge", Path.Combine(local, @"Microsoft\Edge\User Data")),
            ("Яндекс Браузер", Path.Combine(local, @"Yandex\YandexBrowser\User Data")),
            ("Brave", Path.Combine(local, @"BraveSoftware\Brave-Browser\User Data")),
            ("Vivaldi", Path.Combine(local, @"Vivaldi\User Data")),
            ("Chromium", Path.Combine(local, @"Chromium\User Data")),
        };
        foreach (var (name, dir) in chromium)
        {
            if (!Directory.Exists(dir)) continue;
            var names = ProfileNames(dir);
            foreach (var p in Directory.GetDirectories(dir))
            {
                var folder = Path.GetFileName(p);
                if (folder != "Default" && !folder.StartsWith("Profile ")) continue;
                if (!File.Exists(Path.Combine(p, "Bookmarks")) && !File.Exists(Path.Combine(p, "History"))) continue;
                var label = names.TryGetValue(folder, out var n) && n.Length > 0 ? n : folder == "Default" ? "Основной профиль" : folder;
                list.Add(new BrowserProfile($"{name}|{folder}", name, label, p, false));
            }
        }

        // Opera хранит профиль прямо в папке
        foreach (var (name, dir) in new[] { ("Opera", Path.Combine(roaming, @"Opera Software\Opera Stable")), ("Opera GX", Path.Combine(roaming, @"Opera Software\Opera GX Stable")) })
        {
            var p = Directory.Exists(Path.Combine(dir, "Default")) ? Path.Combine(dir, "Default") : dir;
            if (File.Exists(Path.Combine(p, "Bookmarks")) || File.Exists(Path.Combine(p, "History")))
                list.Add(new BrowserProfile($"{name}|main", name, "Основной профиль", p, false));
        }

        var ff = Path.Combine(roaming, @"Mozilla\Firefox\Profiles");
        if (Directory.Exists(ff))
        {
            foreach (var p in Directory.GetDirectories(ff))
            {
                if (!File.Exists(Path.Combine(p, "places.sqlite"))) continue;
                var folder = Path.GetFileName(p);
                int dot = folder.IndexOf('.');
                var label = dot >= 0 ? folder[(dot + 1)..] : folder;
                if (new FileInfo(Path.Combine(p, "places.sqlite")).Length < 300_000 && list.Any(x => x.Firefox)) continue; // пустые служебные профили
                list.Add(new BrowserProfile($"Firefox|{folder}", "Firefox", label, p, true));
            }
        }
        return list;
    }

    static Dictionary<string, string> ProfileNames(string userData)
    {
        var result = new Dictionary<string, string>();
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(userData, "Local State")));
            if (doc.RootElement.TryGetProperty("profile", out var prof) && prof.TryGetProperty("info_cache", out var cache))
                foreach (var p in cache.EnumerateObject())
                    if (p.Value.TryGetProperty("name", out var n)) result[p.Name] = n.GetString() ?? "";
        }
        catch { }
        return result;
    }

    // ---------------- Чтение данных ----------------

    public static List<(string title, string url)> ReadBookmarks(BrowserProfile p) => p.Firefox ? FirefoxBookmarks(p.Dir) : ChromiumBookmarks(p.Dir);
    public static List<HistoryEntry> ReadHistory(BrowserProfile p) => p.Firefox ? FirefoxHistory(p.Dir) : ChromiumHistory(p.Dir);

    static List<(string, string)> ChromiumBookmarks(string dir)
    {
        var result = new List<(string, string)>();
        var path = Path.Combine(dir, "Bookmarks");
        if (!File.Exists(path)) return result;
        using var doc = JsonDocument.Parse(ReadShared(path));
        if (!doc.RootElement.TryGetProperty("roots", out var roots)) return result;
        void Walk(JsonElement node)
        {
            if (node.ValueKind != JsonValueKind.Object) return;
            var type = node.TryGetProperty("type", out var t) ? t.GetString() : null;
            if (type == "url" && node.TryGetProperty("url", out var u))
            {
                var url = u.GetString() ?? "";
                var name = node.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                if (IsWeb(url)) result.Add((name.Length > 0 ? name : url, url));
            }
            if (node.TryGetProperty("children", out var ch) && ch.ValueKind == JsonValueKind.Array)
                foreach (var c in ch.EnumerateArray()) Walk(c);
        }
        foreach (var key in new[] { "bookmark_bar", "other", "synced" })
            if (roots.TryGetProperty(key, out var r)) Walk(r);
        return result;
    }

    static List<HistoryEntry> ChromiumHistory(string dir)
    {
        var result = new List<HistoryEntry>();
        WithDbCopy(Path.Combine(dir, "History"), cmd =>
        {
            cmd.CommandText = $"SELECT url, title, last_visit_time FROM urls WHERE hidden = 0 AND last_visit_time > 0 ORDER BY last_visit_time DESC LIMIT {MaxHistory}";
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                var url = r.GetString(0);
                if (!IsWeb(url)) continue;
                long t = r.GetInt64(2); // микросекунды с 1601 года
                result.Add(new HistoryEntry { Url = url, Title = r.IsDBNull(1) ? "" : r.GetString(1), Time = DateTime.FromFileTimeUtc(t * 10).ToLocalTime() });
            }
        });
        return result;
    }

    static List<(string, string)> FirefoxBookmarks(string dir)
    {
        var result = new List<(string, string)>();
        WithDbCopy(Path.Combine(dir, "places.sqlite"), cmd =>
        {
            cmd.CommandText = "SELECT b.title, p.url FROM moz_bookmarks b JOIN moz_places p ON b.fk = p.id WHERE b.type = 1 ORDER BY b.parent, b.position";
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                var url = r.GetString(1);
                if (!IsWeb(url)) continue;
                var title = r.IsDBNull(0) ? "" : r.GetString(0);
                result.Add((title.Length > 0 ? title : url, url));
            }
        });
        return result;
    }

    static List<HistoryEntry> FirefoxHistory(string dir)
    {
        var result = new List<HistoryEntry>();
        WithDbCopy(Path.Combine(dir, "places.sqlite"), cmd =>
        {
            cmd.CommandText = $"SELECT url, title, last_visit_date FROM moz_places WHERE last_visit_date IS NOT NULL AND hidden = 0 ORDER BY last_visit_date DESC LIMIT {MaxHistory}";
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                var url = r.GetString(0);
                if (!IsWeb(url)) continue;
                long t = r.GetInt64(2); // микросекунды с 1970 года
                result.Add(new HistoryEntry { Url = url, Title = r.IsDBNull(1) ? "" : r.GetString(1), Time = DateTimeOffset.FromUnixTimeMilliseconds(t / 1000).LocalDateTime });
            }
        });
        return result;
    }

    static bool IsWeb(string url) => url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || url.StartsWith("https://", StringComparison.OrdinalIgnoreCase);

    /// <summary>Читает файл, даже если его держит открытым работающий браузер.</summary>
    static string ReadShared(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var sr = new StreamReader(fs, Encoding.UTF8);
        return sr.ReadToEnd();
    }

    static void CopyShared(string from, string to)
    {
        using var src = new FileStream(from, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var dst = File.Create(to);
        src.CopyTo(dst);
    }

    /// <summary>Базу копируем во временную папку: так не мешаем работающему браузеру и ничего у него не меняем.</summary>
    static void WithDbCopy(string db, Action<SqliteCommand> work)
    {
        if (!File.Exists(db)) return;
        InitSqlite();
        var tmp = Path.Combine(Path.GetTempPath(), "LiteBrowserImport-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmp);
        try
        {
            var copy = Path.Combine(tmp, "db.sqlite");
            try { CopyShared(db, copy); }
            catch (IOException) { throw new IOException("Файл занят. Закройте этот браузер и попробуйте ещё раз."); }
            if (File.Exists(db + "-wal")) try { CopyShared(db + "-wal", copy + "-wal"); } catch { }
            using (var conn = new SqliteConnection($"Data Source={copy};Pooling=False"))
            {
                conn.Open();
                using var cmd = conn.CreateCommand();
                work(cmd);
            }
        }
        finally
        {
            try { Directory.Delete(tmp, true); } catch { }
        }
    }

    // ---------------- HTML ----------------

    public static string ExportHtml(IEnumerable<Bookmark> bookmarks)
    {
        var sb = new StringBuilder();
        sb.AppendLine("<!DOCTYPE NETSCAPE-Bookmark-file-1>");
        sb.AppendLine("<META HTTP-EQUIV=\"Content-Type\" CONTENT=\"text/html; charset=UTF-8\">");
        sb.AppendLine("<TITLE>Bookmarks</TITLE>");
        sb.AppendLine("<H1>Bookmarks</H1>");
        sb.AppendLine("<DL><p>");
        sb.AppendLine("    <DT><H3>LiteBrowser</H3>");
        sb.AppendLine("    <DL><p>");
        foreach (var b in bookmarks)
            sb.AppendLine($"        <DT><A HREF=\"{WebUtility.HtmlEncode(b.Url)}\">{WebUtility.HtmlEncode(b.Title)}</A>");
        sb.AppendLine("    </DL><p>");
        sb.AppendLine("</DL><p>");
        return sb.ToString();
    }

    static readonly Regex LinkRx = new("<A\\s[^>]*?HREF\\s*=\\s*\"([^\"]+)\"[^>]*>(.*?)</A>", RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);
    static readonly Regex TagRx = new("<[^>]+>", RegexOptions.Compiled);

    public static List<(string title, string url)> ParseHtml(string html)
    {
        var result = new List<(string, string)>();
        foreach (Match m in LinkRx.Matches(html))
        {
            var url = WebUtility.HtmlDecode(m.Groups[1].Value).Trim();
            if (!IsWeb(url)) continue;
            var title = WebUtility.HtmlDecode(TagRx.Replace(m.Groups[2].Value, "")).Trim();
            result.Add((title.Length > 0 ? title : url, url));
        }
        return result;
    }
}
