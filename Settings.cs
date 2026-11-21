using System.Text.Json;
using System.Text.RegularExpressions;

namespace LiteBrowser;

sealed record SearchEngine(string Id, string Name, string Url, string Description, bool Private);

static class SearchEngines
{
    public static readonly SearchEngine[] All =
    {
        new("duckduckgo", "DuckDuckGo", "https://duckduckgo.com/?q=", "Не следит и не запоминает запросы", true),
        new("startpage", "Startpage", "https://www.startpage.com/sp/search?query=", "Результаты Google, но без слежки", true),
        new("brave", "Brave Search", "https://search.brave.com/search?q=", "Свой независимый поиск без слежки", true),
        new("google", "Google", "https://www.google.com/search?q=", "Лучше всех ищет, но собирает данные", false),
        new("yandex", "Яндекс", "https://yandex.ru/search/?text=", "Лучше всех ищет по-русски, собирает данные", false),
        new("bing", "Bing", "https://www.bing.com/search?q=", "Поиск Microsoft, собирает данные", false),
    };

    public static SearchEngine Get(string? id) => All.FirstOrDefault(e => e.Id == id) ?? All[0];
}

/// <summary>Настройки браузера, хранятся в settings.json. Любую функцию можно выключить.</summary>
sealed class AppSettings
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public static readonly string[] DefaultProxySites =
    {
        "youtube.com", "youtu.be", "googlevideo.com", "ytimg.com", "ggpht.com", "youtube-nocookie.com",
        "instagram.com", "cdninstagram.com", "facebook.com", "fbcdn.net", "messenger.com",
        "x.com", "twitter.com", "twimg.com", "t.co",
        "discord.com", "discord.gg", "discordapp.com", "discordapp.net", "discord.media",
        "linkedin.com", "licdn.com", "medium.com", "soundcloud.com", "sndcdn.com", "patreon.com",
        "chatgpt.com", "openai.com", "oaistatic.com", "oaiusercontent.com",
        "signal.org", "torproject.org", "proton.me", "protonvpn.com",
    };

    public static readonly int[] UiScales = { 75, 80, 90, 100, 110, 125 };
    public static readonly int[] ZoomSteps = { 25, 33, 50, 67, 75, 80, 90, 100, 110, 125, 150, 175, 200, 250, 300 };

    // Поиск
    public string Engine { get; set; } = "duckduckgo";
    // Безопасность
    public bool HttpsOnly { get; set; } = true;
    public bool BlockAds { get; set; } = true;
    public bool ExtendedBlocklist { get; set; } = true;
    // Обход блокировок
    public string ProxyMode { get; set; } = "off";      // off | all | list
    public string ProxyType { get; set; } = "socks5";   // socks5 | http
    public string ProxyHost { get; set; } = "127.0.0.1";
    public int ProxyPort { get; set; } = 10808;
    public List<string> ProxySites { get; set; } = DefaultProxySites.ToList();
    // Вкладки и память
    public bool RestoreSession { get; set; } = false;
    public int FreezeMinutes { get; set; } = 15;        // 0 = не замораживать
    // Закладки, история, загрузки
    public bool ShowBookmarksBar { get; set; } = true;
    public bool SaveHistory { get; set; } = true;
    public bool BuiltInDownloads { get; set; } = true;
    // Внешний вид
    public string SiteTheme { get; set; } = "auto";     // auto | dark | light
    // Менеджер паролей
    public bool PasswordManager { get; set; } = true;
    public int AutoLockMinutes { get; set; } = 15;      // 0 — до закрытия браузера
    public List<string> NeverSave { get; set; } = new();
    public int UiScale { get; set; } = 100;             // размер интерфейса, %
    public int PageZoom { get; set; } = 100;            // масштаб страниц по умолчанию, %
    // Предложение перенести данные на стартовой странице уже показано/скрыто
    public bool ImportOffered { get; set; } = false;

    static readonly Regex HostRx = new(@"^[a-z0-9]([a-z0-9\-\.]*[a-z0-9])?$", RegexOptions.Compiled);

    /// <summary>Приводит к допустимым значениям. Важно: адреса попадают в параметры запуска движка.</summary>
    public AppSettings Normalize()
    {
        Engine = SearchEngines.Get(Engine).Id;
        if (SiteTheme is not ("auto" or "dark" or "light")) SiteTheme = "auto";
        if (ProxyMode is not ("off" or "all" or "list")) ProxyMode = "off";
        if (ProxyType is not ("socks5" or "http")) ProxyType = "socks5";
        ProxyHost = (ProxyHost ?? "").Trim().ToLowerInvariant();
        if (!HostRx.IsMatch(ProxyHost)) ProxyHost = "127.0.0.1";
        if (ProxyPort is < 1 or > 65535) ProxyPort = 10808;
        if (FreezeMinutes is not (0 or 5 or 15 or 30 or 60)) FreezeMinutes = 15;
        if (!UiScales.Contains(UiScale)) UiScale = 100;
        if (AutoLockMinutes is not (0 or 5 or 15 or 30 or 60)) AutoLockMinutes = 15;
        NeverSave = (NeverSave ?? new()).Where(s => s.Length is > 0 and < 300).Distinct().Take(1000).ToList();
        if (!ZoomSteps.Contains(PageZoom)) PageZoom = 100;
        ProxySites = (ProxySites ?? new()).Select(CleanSite).Where(s => s.Length > 0 && HostRx.IsMatch(s))
            .Distinct().Take(2000).ToList();
        return this;
    }

    static string CleanSite(string s)
    {
        s = (s ?? "").Trim().ToLowerInvariant();
        int i = s.IndexOf("://", StringComparison.Ordinal);
        if (i >= 0) s = s[(i + 3)..];
        i = s.IndexOfAny(new[] { '/', '?', '#', ':' });
        if (i >= 0) s = s[..i];
        if (s.StartsWith("*.")) s = s[2..];
        if (s.StartsWith("www.")) s = s[4..];
        return s.Trim('.');
    }

    /// <summary>Подпись настроек, которые требуют перезапуска движка.</summary>
    public string EngineSignature() =>
        ProxyMode == "off" ? "off" : $"{ProxyMode}|{ProxyType}|{ProxyHost}|{ProxyPort}|{(ProxyMode == "list" ? string.Join(",", ProxySites) : "")}";

    static string FileIn(string dir) => Path.Combine(dir, "settings.json");

    public static AppSettings Load(string dir)
    {
        try
        {
            var path = FileIn(dir);
            if (File.Exists(path))
                return (JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path), Json) ?? new()).Normalize();
        }
        catch { /* повреждённый файл — берём настройки по умолчанию */ }
        return new AppSettings();
    }

    public void Save(string dir)
    {
        try
        {
            Directory.CreateDirectory(dir);
            File.WriteAllText(FileIn(dir), JsonSerializer.Serialize(this, Json));
        }
        catch { }
    }
}
