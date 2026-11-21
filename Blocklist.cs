using System.Net;

namespace LiteBrowser;

/// <summary>
/// Список блокировки. Встроенный — ~45 рекламных и следящих доменов.
/// Расширенный — список StevenBlack (~80 000 доменов рекламы, трекеров и вредоносных сайтов), обновляется раз в неделю.
/// </summary>
static class Blocklist
{
    const string ListUrl = "https://raw.githubusercontent.com/StevenBlack/hosts/master/hosts";

    public static readonly string[] BuiltIn =
    {
        "doubleclick.net", "googlesyndication.com", "googleadservices.com", "google-analytics.com",
        "googletagmanager.com", "googletagservices.com", "adservice.google.com",
        "adnxs.com", "adsrvr.org", "criteo.com", "criteo.net", "taboola.com", "outbrain.com",
        "scorecardresearch.com", "quantserve.com", "moatads.com", "amazon-adsystem.com", "pubmatic.com",
        "rubiconproject.com", "openx.net", "casalemedia.com", "advertising.com", "adform.net",
        "hotjar.com", "mouseflow.com", "fullstory.com", "mixpanel.com", "segment.io", "branch.io",
        "connect.facebook.net", "ads-twitter.com", "analytics.tiktok.com",
        "mc.yandex.ru", "an.yandex.ru", "yabs.yandex.ru", "top-fwz1.mail.ru", "ad.mail.ru",
        "counter.yadro.ru", "adfox.ru", "adriver.ru", "popads.net", "propellerads.com", "exoclick.com",
    };

    static readonly HashSet<string> builtIn = new(BuiltIn, StringComparer.OrdinalIgnoreCase);
    static volatile HashSet<string> extended = new(StringComparer.OrdinalIgnoreCase);
    static readonly HashSet<string> allowed = new(StringComparer.OrdinalIgnoreCase);

    public static int ExtendedCount => extended.Count;
    public static DateTime? Updated { get; private set; }
    public static bool Updating { get; private set; }

    static string FileIn(string dir) => Path.Combine(dir, "blocklist.txt");

    /// <summary>Разрешить домен до конца сессии (кнопка «Всё равно открыть»).</summary>
    public static void Allow(string host) { lock (allowed) allowed.Add(host); }

    public static bool IsBlocked(string host, bool useExtended)
    {
        if (string.IsNullOrEmpty(host)) return false;
        lock (allowed) if (allowed.Contains(host)) return false;
        var ext = extended;
        for (string h = host; ;)
        {
            if (builtIn.Contains(h) || (useExtended && ext.Contains(h))) return true;
            int dot = h.IndexOf('.');
            if (dot < 0 || dot == h.Length - 1) return false;
            h = h[(dot + 1)..];
            if (h.IndexOf('.') < 0) return false; // не проверяем голые зоны вроде "com"
        }
    }

    public static void LoadCached(string dir)
    {
        try
        {
            var path = FileIn(dir);
            if (!File.Exists(path)) return;
            extended = new HashSet<string>(File.ReadLines(path).Where(l => l.Length > 0), StringComparer.OrdinalIgnoreCase);
            Updated = File.GetLastWriteTime(path);
        }
        catch { }
    }

    public static async Task<bool> UpdateAsync(string dir, AppSettings s, bool force)
    {
        if (Updating) return false;
        if (!force && Updated is DateTime u && (DateTime.Now - u).TotalDays < 7) return false;
        Updating = true;
        try
        {
            var handler = new HttpClientHandler();
            if (s.ProxyMode == "all")
                handler.Proxy = new WebProxy($"{(s.ProxyType == "http" ? "http" : "socks5")}://{s.ProxyHost}:{s.ProxyPort}");
            using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(60) };
            var text = await http.GetStringAsync(ListUrl);
            var set = await Task.Run(() =>
            {
                var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var raw in text.Split('\n'))
                {
                    var line = raw.Trim();
                    if (!line.StartsWith("0.0.0.0 ")) continue;
                    var host = line[8..].Split('#')[0].Trim();
                    if (host.Length > 0 && host != "0.0.0.0" && host.Contains('.')) result.Add(host);
                }
                return result;
            });
            if (set.Count < 1000) return false; // что-то пошло не так — оставляем старый список
            Directory.CreateDirectory(dir);
            await File.WriteAllLinesAsync(FileIn(dir), set);
            extended = set;
            Updated = DateTime.Now;
            return true;
        }
        catch { return false; }
        finally { Updating = false; }
    }
}
