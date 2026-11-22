using System.Globalization;
using System.Text.Json;

namespace LiteBrowser;

/// <summary>
/// Язык интерфейса. Исходные тексты — русские; для английского интерфейса берём перевод из словаря
/// wwwroot/i18n-en.json (тот же словарь переводит и свои страницы браузера).
/// </summary>
static class L
{
    static Dictionary<string, string>? en;
    static string[] prefixes = Array.Empty<string>();

    /// <summary>Итоговый язык: настройка «auto» — по языку Windows (русский для ru/uk/be/kk/ky/uz).</summary>
    public static string Lang => AppEnv.Settings.Language switch
    {
        "ru" => "ru",
        "en" => "en",
        _ => CultureInfo.CurrentUICulture.TwoLetterISOLanguageName is "ru" or "uk" or "be" or "kk" or "ky" or "uz" or "tg" ? "ru" : "en",
    };

    public static bool En => Lang == "en";

    static Dictionary<string, string> Dict
    {
        get
        {
            if (en != null) return en;
            try
            {
                using var s = typeof(L).Assembly.GetManifestResourceStream("wwwroot/i18n-en.json");
                en = s != null ? JsonSerializer.Deserialize<Dictionary<string, string>>(s) ?? new() : new();
            }
            catch { en = new(); }
            prefixes = en.Keys.Where(k => k.EndsWith(' ') || k.EndsWith(':')).OrderByDescending(k => k.Length).ToArray();
            return en;
        }
    }

    /// <summary>Перевести текст интерфейса (если интерфейс английский и перевод есть).</summary>
    public static string T(string ru)
    {
        if (!En || string.IsNullOrEmpty(ru)) return ru;
        var d = Dict;
        if (d.TryGetValue(ru, out var tr)) return tr;
        var t = ru.Trim();
        if (t.Length != ru.Length && d.TryGetValue(t, out tr)) return ru.Replace(t, tr);
        // Числа внутри строки: «Вернуть вкладки (2)?» ↔ ключ «Вернуть вкладки ({n})?»
        if (ru.Any(char.IsDigit))
        {
            var nums = new List<string>();
            var key = System.Text.RegularExpressions.Regex.Replace(ru, @"\d+(?:[.,]\d+)*", m => { nums.Add(m.Value); return "{n}"; });
            if (d.TryGetValue(key, out tr)) { int i = 0; return System.Text.RegularExpressions.Regex.Replace(tr, @"\{n\}", _ => i < nums.Count ? nums[i++] : ""); }
        }
        foreach (var p in prefixes)
            if (ru.StartsWith(p, StringComparison.Ordinal)) return d[p] + ru[p.Length..];
        if (ru.StartsWith("Искать «", StringComparison.Ordinal)) return "Search “" + ru[8..].TrimEnd('»') + "”";
        return ru;
    }
}
