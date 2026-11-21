using System.Text.Json;

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

/// <summary>Настройки браузера, хранятся в settings.json.</summary>
sealed class AppSettings
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public string Engine { get; set; } = "duckduckgo";
    public bool HttpsOnly { get; set; } = true;
    public bool BlockAds { get; set; } = true;
    public string SiteTheme { get; set; } = "auto"; // auto | dark | light

    public AppSettings Normalize()
    {
        Engine = SearchEngines.Get(Engine).Id;
        if (SiteTheme is not ("auto" or "dark" or "light")) SiteTheme = "auto";
        return this;
    }

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
