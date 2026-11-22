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

/// <summary>Плитка на стартовой странице.</summary>
sealed class HomeTile
{
    public string Name { get; set; } = "";
    public string Url { get; set; } = "";

    public static List<HomeTile> Defaults() => new()
    {
        new() { Name = "YouTube", Url = "https://www.youtube.com/" },
        new() { Name = "Википедия", Url = "https://ru.wikipedia.org/" },
        new() { Name = "ВКонтакте", Url = "https://vk.com/" },
        new() { Name = "Telegram", Url = "https://web.telegram.org/" },
        new() { Name = "GitHub", Url = "https://github.com/" },
        new() { Name = "Почта", Url = "https://mail.google.com/" },
    };
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
    public bool SearchSuggestions { get; set; } = false;   // подсказки от поисковика (запросы уходят ему по мере ввода)
    // Безопасность
    public bool HttpsOnly { get; set; } = true;
    public bool BlockAds { get; set; } = true;
    public bool ExtendedBlocklist { get; set; } = true;
    // Блокировщик рекламы
    public bool ListEasyList { get; set; } = true;
    public bool ListEasyPrivacy { get; set; } = true;
    public bool ListRuAdList { get; set; } = true;
    public bool ListCookies { get; set; } = false;
    public bool CosmeticFiltering { get; set; } = true;
    public bool CookieBanners { get; set; } = true;
    public bool FullTextHistory { get; set; } = true;   // запоминать текст страниц для поиска по истории (только на этом компьютере)      // отклонять cookie-баннеры (или прятать, если отказаться нельзя)
    public bool YouTubeAds { get; set; } = true;
    public bool BlockPopups { get; set; } = true;
    public List<string> AdblockAllow { get; set; } = new();
    // Уровни доверия сайтов и сохранённые разрешения
    public List<string> TrustedSites { get; set; } = new();     // без блокировки рекламы и всплывающих окон
    public List<string> UntrustedSites { get; set; } = new();   // без JavaScript, окон, разрешений и паролей
    public Dictionary<string, Dictionary<string, string>> SitePermissions { get; set; } = new(); // сайт → (разрешение → allow/deny)
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
    public string Language { get; set; } = "auto";      // auto | ru | en
    public Dictionary<string, int> SiteZoom { get; set; } = new();   // сайт → масштаб, %
    public bool ForceDark { get; set; } = false;        // тёмная тема для всех сайтов
    public bool VerticalTabs { get; set; } = false;     // вкладки слева
    public bool TorEnabled { get; set; } = false;       // весь трафик через встроенный Tor
    public bool HomeWidgets { get; set; } = true;       // погода, курс, заметка на стартовой
    public bool AutoPip { get; set; } = true;
    public bool LockHello { get; set; }                 // открывать браузер через Windows Hello (PIN остаётся запасным)
    public string LockPinHash { get; set; } = "";       // пароль (PIN) на браузер: соль:хеш PBKDF2, пусто — выключен
    public int AutoCollapseGroupsMinutes { get; set; } = 30; // сворачивать неиспользуемые группы вкладок (0 — нет)           // видео в «картинке в картинке» при уходе с вкладки
    public string WidgetCity { get; set; } = "";        // город для погоды (вводит пользователь)
    public string HomeNote { get; set; } = "";          // заметка на стартовой странице
    public bool FakeSiteWarning { get; set; } = true;   // предупреждать о сайтах-подделках
    public string TorBridges { get; set; } = "";        // "" | obfs4 | snowflake
    public List<string> FocusSites { get; set; } = new() { "youtube.com", "vk.com", "tiktok.com", "instagram.com", "twitch.tv", "x.com", "twitter.com", "reddit.com", "pikabu.ru", "dzen.ru", "ok.ru", "facebook.com" };
    public bool VerticalTabsCompact { get; set; } = false; // вертикальные вкладки — только значки
    public List<string> DarkSites { get; set; } = new();     // тёмная тема для этих сайтов (если общая выключена)
    public List<string> DarkExclude { get; set; } = new();   // без тёмной темы (если общая включена)
    public string SiteTheme { get; set; } = "auto";     // auto | dark | light
    // Менеджер паролей
    public bool PasswordManager { get; set; } = true;
    public int AutoLockMinutes { get; set; } = 15;      // 0 — до закрытия браузера
    public List<string> NeverSave { get; set; } = new();
    // Боковая панель
    public bool SidePanelOpen { get; set; } = false;
    public string SidePanelUrl { get; set; } = "https://lite.home/bookmarks.html";
    public int SidePanelWidth { get; set; } = 380;
    public List<HomeTile> SidePanelApps { get; set; } = SidePanel.DefaultApps(); // приложения в боковой панели
    // ИИ-помощник
    public bool AiEnabled { get; set; } = true;
    public string AiProvider { get; set; } = "local";    // local (встроенный) | ollama | openai
    public string AiLocalModel { get; set; } = "qwen2.5-1.5b";
    public bool AiGpu { get; set; } = true;              // встроенный ИИ на видеокарте (Vulkan)
    public string AiOllamaUrl { get; set; } = "http://localhost:11434";
    public string AiBaseUrl { get; set; } = "https://api.openai.com/v1";
    public string AiModel { get; set; } = "";
    public bool AiCloudConsent { get; set; } = false;     // согласие отправлять страницы во внешний сервис
    // Экономия трафика, жесты, перевод
    public bool DataSaver { get; set; } = false;          // не грузить картинки и видео
    public bool MouseGestures { get; set; } = true;
    public bool OfferTranslate { get; set; } = true;
    public bool SelectionTranslate { get; set; } = true;  // кнопка «Перевести» у выделенного текста
    public string TranslateService { get; set; } = "google"; // google | yandex
    // Родительский контроль
    public bool ParentalEnabled { get; set; } = false;
    public string ParentalPinHash { get; set; } = "";
    public bool ParentalAdult { get; set; } = true;       // блокировать сайты для взрослых
    public List<string> ParentalBlocked { get; set; } = new();
    // Синхронизация
    public bool SyncEnabled { get; set; } = false;
    public string SyncFolder { get; set; } = "";
    public bool SyncHistory { get; set; } = true;
    public string SyncLast { get; set; } = "";
    // Оформление
    public string UiTheme { get; set; } = "dark";    // dark | light | auto
    public string Accent { get; set; } = "blue";     // blue | purple | green | orange | pink | red
    public bool Wallpaper { get; set; } = false;
    public int WallpaperVersion { get; set; }
    public bool WallpaperVideo { get; set; }            // обои — видео (mp4/webm), крутится без звука
    public string HomeAnim { get; set; } = "";          // живой фон стартовой: "" | aurora | stars | waves
    public List<HomeTile> HomeTiles { get; set; } = HomeTile.Defaults();
    // Обновления и браузер по умолчанию
    public bool AutoUpdate { get; set; } = true;
    public string SkippedUpdate { get; set; } = "";
    public bool DefaultPromptDismissed { get; set; } = false;
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
        if (Language is not ("auto" or "ru" or "en")) Language = "auto";
        if (ProxyMode is not ("off" or "all" or "list")) ProxyMode = "off";
        if (ProxyType is not ("socks5" or "http")) ProxyType = "socks5";
        ProxyHost = (ProxyHost ?? "").Trim().ToLowerInvariant();
        if (!HostRx.IsMatch(ProxyHost)) ProxyHost = "127.0.0.1";
        if (ProxyPort is < 1 or > 65535) ProxyPort = 10808;
        if (FreezeMinutes is not (0 or 5 or 15 or 30 or 60)) FreezeMinutes = 15;
        if (!UiScales.Contains(UiScale)) UiScale = 100;
        SidePanelWidth = Math.Clamp(SidePanelWidth, 260, 1000);
        if (HomeAnim is not ("" or "aurora" or "stars" or "waves")) HomeAnim = "";
        if (UiTheme is not ("dark" or "light" or "auto") && !Theme.ExtraThemes.Contains(UiTheme)) UiTheme = "dark";
        if (TranslateService is not ("google" or "yandex")) TranslateService = "google";
        if (AiProvider is not ("local" or "ollama" or "openai")) AiProvider = "local";
        if (LocalAi.Find(AiLocalModel ?? "") == null) AiLocalModel = LocalAi.Models[0].Id;
        if (!Uri.TryCreate(AiOllamaUrl, UriKind.Absolute, out var ou) || ou.Scheme is not ("http" or "https")) AiOllamaUrl = "http://localhost:11434";
        if (!Uri.TryCreate(AiBaseUrl, UriKind.Absolute, out var bu) || bu.Scheme is not ("http" or "https")) AiBaseUrl = "https://api.openai.com/v1";
        AiModel = (AiModel ?? "").Trim();
        if (AiModel.Length > 200) AiModel = "";
        ParentalBlocked = (ParentalBlocked ?? new()).Select(CleanSite).Where(s => s.Length > 0 && HostRx.IsMatch(s)).Distinct().Take(2000).ToList();
        ParentalPinHash ??= "";
        if (!Theme.Accents.ContainsKey(Accent ?? "")) Accent = "blue";
        if (!Path.IsPathRooted(SyncFolder ?? "") && !(WebDav.IsUrl(SyncFolder) && WebDav.Allowed(SyncFolder!))) SyncFolder = "";
        HomeTiles = (HomeTiles ?? HomeTile.Defaults())
            .Where(t => t != null && (t.Name ?? "").Trim().Length is > 0 and <= 40 && Uri.TryCreate(t.Url, UriKind.Absolute, out var tu) && tu.Scheme is "http" or "https")
            .Take(24).ToList();
        if (!(SidePanelUrl ?? "").StartsWith("https://") && !(SidePanelUrl ?? "").StartsWith("http://")) SidePanelUrl = "https://lite.home/bookmarks.html";
        if (AutoLockMinutes is not (0 or 5 or 15 or 30 or 60)) AutoLockMinutes = 15;
        NeverSave = (NeverSave ?? new()).Where(s => s.Length is > 0 and < 300).Distinct().Take(1000).ToList();
        AdblockAllow = (AdblockAllow ?? new()).Select(CleanSite).Where(s => s.Length > 0 && HostRx.IsMatch(s)).Distinct().Take(1000).ToList();
        TrustedSites = (TrustedSites ?? new()).Select(CleanSite).Where(s => s.Length > 0 && HostRx.IsMatch(s)).Distinct().Take(1000).ToList();
        UntrustedSites = (UntrustedSites ?? new()).Select(CleanSite).Where(s => s.Length > 0 && HostRx.IsMatch(s) && !TrustedSites.Contains(s)).Distinct().Take(1000).ToList();
        SitePermissions = (SitePermissions ?? new())
            .Where(kv => HostRx.IsMatch(kv.Key) && kv.Value != null)
            .Take(1000)
            .ToDictionary(kv => kv.Key, kv => kv.Value.Where(p => p.Value is "allow" or "deny" && p.Key.Length < 50).ToDictionary(p => p.Key, p => p.Value));
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
        TorEnabled ? "tor" :
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
