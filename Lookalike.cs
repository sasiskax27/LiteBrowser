using System.Globalization;

namespace LiteBrowser;

/// <summary>
/// Защита от сайтов-подделок: адрес похож на известный сайт (банк, госуслуги, почта, соцсеть), но им не является —
/// «sberbank-online.ru.xyz», «g00gle.com», «vk-com.ru», буквы другого алфавита (xn--…).
/// </summary>
static class Lookalike
{
    /// <summary>Известные сайты, которые чаще всего подделывают.</summary>
    static readonly string[] Targets =
    {
        "sberbank.ru", "online.sberbank.ru", "tinkoff.ru", "tbank.ru", "vtb.ru", "alfabank.ru", "gazprombank.ru", "raiffeisen.ru", "pochtabank.ru",
        "gosuslugi.ru", "nalog.gov.ru", "mos.ru", "pfr.gov.ru",
        "vk.com", "ok.ru", "mail.ru", "yandex.ru", "ya.ru", "ozon.ru", "wildberries.ru", "avito.ru", "kaspi.kz", "telegram.org", "web.telegram.org",
        "google.com", "gmail.com", "youtube.com", "facebook.com", "instagram.com", "whatsapp.com", "microsoft.com", "live.com", "outlook.com",
        "apple.com", "icloud.com", "paypal.com", "amazon.com", "steampowered.com", "steamcommunity.com", "epicgames.com", "roblox.com",
        "discord.com", "binance.com", "github.com", "netflix.com", "spotify.com",
    };

    static readonly IdnMapping Idn = new();
    static readonly HashSet<string> allowed = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Пользователь нажал «Всё равно открыть» — больше не предупреждаем об этом сайте в этом сеансе.</summary>
    public static void Allow(string host) { lock (allowed) allowed.Add(host.StartsWith("www.") ? host[4..] : host); }

    /// <summary>Если адрес похож на известный сайт, но не он — вернуть этот сайт; иначе null.</summary>
    public static string? Check(string host)
    {
        host = host.ToLowerInvariant().TrimEnd('.');
        if (host.StartsWith("www.")) host = host[4..];
        if (host.Length == 0 || System.Net.IPAddress.TryParse(host, out _)) return null;
        lock (allowed) if (allowed.Contains(host)) return null;
        // Настоящий сайт или его поддомен — всё в порядке
        foreach (var t in Targets)
            if (host == t || host.EndsWith("." + t)) return null;

        string unicode = host;
        try { if (host.Contains("xn--")) unicode = Idn.GetUnicode(host); } catch { }
        var baseDomain = AdBlock.BaseDomain(host);
        var label = baseDomain.Split('.')[0];                         // «sberbank-online» из «sberbank-online.ru.xyz»
        var skeleton = Skeleton(unicode);

        foreach (var t in Targets)
        {
            var tBase = AdBlock.BaseDomain(t);
            var tLabel = tBase.Split('.')[0];                         // «sberbank»
            // 1) Тот же вид, но другие буквы: «ѕberbank.ru» (кириллица), «g00gle.com», «paypa1.com»
            if (Skeleton(tBase) == Skeleton(AdBlock.BaseDomain(unicode)) && tBase != baseDomain) return t;
            // 2) Настоящий адрес спрятан в начале чужого: «gosuslugi.ru.secure-login.com», «vk.com-login.ru»
            if (baseDomain != tBase && (host.StartsWith(tBase + ".") || host.StartsWith(tBase + "-") || host.Contains("." + tBase + "."))) return t;
            if (tLabel.Length < 4) continue;                          // vk, ok, ya — для правил ниже слишком коротко (ложные тревоги)
            // 3) Название через дефис в чужом домене: «sberbank-online.xyz», «login-gosuslugi.ru»
            //    (без дефиса не трогаем: googleusercontent.com, google.ru и т. п. — настоящие)
            if (label != tLabel && label.Contains('-') && label.Split('-').Contains(tLabel) && !IsBrandFamily(baseDomain, tLabel)) return t;
            // 4) Опечатка в одну букву в длинном названии: «yandeks.ru», «gooogle.com», «wildberies.ru»
            if (label.Length >= 6 && tLabel.Length >= 6 && Math.Abs(label.Length - tLabel.Length) <= 1 && label != tLabel && Distance(label, tLabel) == 1) return t;
        }
        return null;
    }

    /// <summary>Свои домены компании (другая зона или служебный домен) — не подделка.</summary>
    static bool IsBrandFamily(string baseDomain, string brand) => baseDomain.Split('.')[0] == brand;

    /// <summary>«Скелет» строки: похожие буквы (латиница, кириллица, цифры) приводятся к одной.</summary>
    static string Skeleton(string s)
    {
        var map = new Dictionary<char, char>
        {
            ['а'] = 'a', ['е'] = 'e', ['о'] = 'o', ['р'] = 'p', ['с'] = 'c', ['у'] = 'y', ['х'] = 'x', ['к'] = 'k', ['м'] = 'm', ['т'] = 't',
            ['н'] = 'h', ['в'] = 'b', ['ѕ'] = 's', ['і'] = 'i', ['ј'] = 'j', ['ӏ'] = 'l', ['ԁ'] = 'd', ['ɡ'] = 'g',
            ['0'] = 'o', ['1'] = 'l', ['3'] = 'e', ['5'] = 's', ['i'] = 'l', ['|'] = 'l',
        };
        var chars = s.ToLowerInvariant().Select(c => map.TryGetValue(c, out var m) ? m : c).ToArray();
        return new string(chars).Replace("rn", "m").Replace("vv", "w");
    }

    static int Distance(string a, string b)
    {
        var d = new int[a.Length + 1, b.Length + 1];
        for (int i = 0; i <= a.Length; i++) d[i, 0] = i;
        for (int j = 0; j <= b.Length; j++) d[0, j] = j;
        for (int i = 1; i <= a.Length; i++)
            for (int j = 1; j <= b.Length; j++)
                d[i, j] = Math.Min(Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1), d[i - 1, j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1));
        return d[a.Length, b.Length];
    }
}
