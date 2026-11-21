using System.Security.Cryptography;
using System.Text;

namespace LiteBrowser;

/// <summary>
/// Родительский контроль: свой список запрещённых сайтов, список сайтов для взрослых (StevenBlack),
/// безопасный поиск. Изменить настройки можно только с PIN-кодом.
/// </summary>
static class Parental
{
    const string AdultListUrl = "https://raw.githubusercontent.com/StevenBlack/hosts/master/alternates/porn-only/hosts";
    static volatile HashSet<string> adult = new(StringComparer.OrdinalIgnoreCase);
    static string FilePath => Path.Combine(AppEnv.DataDir, "parental-adult.txt");
    public static int AdultCount => adult.Count;

    static AppSettings Cfg => AppEnv.Settings;

    public static string Hash(string pin) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("LiteBrowserParental:" + pin)));

    public static bool CheckPin(string pin) => Cfg.ParentalPinHash.Length > 0 && Hash(pin) == Cfg.ParentalPinHash;

    static bool InSet(HashSet<string> set, string host)
    {
        for (string h = host; ;)
        {
            if (set.Contains(h)) return true;
            int dot = h.IndexOf('.');
            if (dot < 0) return false;
            h = h[(dot + 1)..];
        }
    }

    public static bool IsBlocked(string host)
    {
        if (!Cfg.ParentalEnabled || host.Length == 0) return false;
        host = host.ToLowerInvariant();
        foreach (var b in Cfg.ParentalBlocked)
            if (host == b || host.EndsWith("." + b)) return true;
        return Cfg.ParentalAdult && InSet(adult, host);
    }

    /// <summary>Безопасный поиск у поисковиков.</summary>
    public static string SafeSearch(string url)
    {
        if (!Cfg.ParentalEnabled) return url;
        if (url.Contains("google.com/search")) return url + "&safe=active";
        if (url.Contains("duckduckgo.com")) return url + "&kp=1";
        if (url.Contains("yandex.ru/search")) return url + "&family=yes";
        if (url.Contains("bing.com/search")) return url + "&adlt=strict";
        if (url.Contains("search.brave.com")) return url + "&safesearch=strict";
        return url;
    }

    public static async Task InitAsync()
    {
        try
        {
            if (File.Exists(FilePath))
                adult = new HashSet<string>(await File.ReadAllLinesAsync(FilePath), StringComparer.OrdinalIgnoreCase);
            if (!Cfg.ParentalEnabled || !Cfg.ParentalAdult) return;
            if (File.Exists(FilePath) && (DateTime.Now - File.GetLastWriteTime(FilePath)).TotalDays < 7) return;
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
            var text = await http.GetStringAsync(AdultListUrl);
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var raw in text.Split('\n'))
            {
                var line = raw.Trim();
                if (!line.StartsWith("0.0.0.0 ")) continue;
                var h = line[8..].Split('#')[0].Trim();
                if (h.Contains('.') && h != "0.0.0.0") set.Add(h);
            }
            if (set.Count < 1000) return;
            await File.WriteAllLinesAsync(FilePath, set);
            adult = set;
        }
        catch { }
    }
}
