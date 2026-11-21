namespace LiteBrowser;

/// <summary>Автоматическая раскладка вкладок по темам: видео, соцсети, покупки, новости и т. д. Работает мгновенно и без интернета.</summary>
static class TabGrouping
{
    static readonly (string name, string[] hosts, string[] words)[] Categories =
    {
        ("Видео", new[] { "youtube.com", "youtu.be", "twitch.tv", "rutube.ru", "vkvideo.ru", "kinopoisk.ru", "ivi.ru", "okko.tv", "netflix.com", "vimeo.com", "dzen.ru/video" }, new[] { "/video", "watch" }),
        ("Соцсети и чаты", new[] { "vk.com", "ok.ru", "t.me", "web.telegram.org", "instagram.com", "facebook.com", "x.com", "twitter.com", "tiktok.com", "reddit.com", "discord.com", "web.whatsapp.com", "pikabu.ru" }, Array.Empty<string>()),
        ("Покупки", new[] { "ozon.ru", "wildberries.ru", "market.yandex.ru", "aliexpress.ru", "aliexpress.com", "avito.ru", "dns-shop.ru", "mvideo.ru", "citilink.ru", "amazon.com", "ebay.com", "megamarket.ru", "lamoda.ru" }, new[] { "/cart", "/product", "/catalog" }),
        ("Новости", new[] { "lenta.ru", "ria.ru", "rbc.ru", "tass.ru", "kommersant.ru", "meduza.io", "bbc.com", "cnn.com", "news.", "iz.ru", "gazeta.ru", "rg.ru", "interfax.ru", "dzen.ru" }, new[] { "/news" }),
        ("Разработка", new[] { "github.com", "gitlab.com", "stackoverflow.com", "habr.com", "learn.microsoft.com", "developer.mozilla.org", "npmjs.com", "pypi.org", "nuget.org", "docs.python.org", "localhost" }, new[] { "/docs" }),
        ("Почта и документы", new[] { "mail.google.com", "mail.yandex.ru", "e.mail.ru", "outlook.live.com", "docs.google.com", "drive.google.com", "disk.yandex.ru", "notion.so", "onedrive.live.com", "office.com" }, Array.Empty<string>()),
        ("Учёба", new[] { "wikipedia.org", "coursera.org", "stepik.org", "khanacademy.org", "duolingo.com", "brainly", "znanija.com", "foxford.ru", "uchi.ru", "skysmart.ru" }, Array.Empty<string>()),
        ("Игры", new[] { "store.steampowered.com", "steamcommunity.com", "epicgames.com", "roblox.com", "minecraft.net", "playground.ru", "stopgame.ru", "ign.com", "nexusmods.com" }, Array.Empty<string>()),
        ("Музыка", new[] { "music.yandex.ru", "open.spotify.com", "soundcloud.com", "music.youtube.com", "zvuk.com" }, Array.Empty<string>()),
        ("ИИ", new[] { "chatgpt.com", "chat.openai.com", "claude.ai", "gemini.google.com", "perplexity.ai", "deepseek.com", "copilot.microsoft.com", "alice.yandex.ru", "giga.chat" }, Array.Empty<string>()),
    };

    /// <summary>Тема адреса или пустая строка.</summary>
    public static string Category(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var u)) return "";
        var host = u.Host.ToLowerInvariant();
        if (host.StartsWith("www.")) host = host[4..];
        var hostPath = host + u.AbsolutePath.ToLowerInvariant();
        // Сначала точные сайты (music.youtube.com важнее youtube.com — поэтому ищем самое длинное совпадение)
        string best = ""; int bestLen = 0;
        foreach (var (name, hosts, _) in Categories)
            foreach (var h in hosts)
            {
                bool hit = h.EndsWith('.') ? host.StartsWith(h) : h.Contains('/') ? hostPath.StartsWith(h) : host == h || host.EndsWith("." + h);
                if (hit && h.Length > bestLen) { best = name; bestLen = h.Length; }
            }
        if (best.Length > 0) return best;
        foreach (var (name, _, words) in Categories)
            if (words.Any(w => u.AbsolutePath.Contains(w, StringComparison.OrdinalIgnoreCase))) return name;
        return "";
    }

    /// <summary>Раскладка: имя группы для каждой вкладки (или null). Тема — если таких вкладок 2+; иначе общий сайт — если 2+ вкладки с одного сайта.</summary>
    public static Dictionary<int, string> Plan(IReadOnlyList<string> urls)
    {
        var res = new Dictionary<int, string>();
        var cats = urls.Select(Category).ToList();
        foreach (var g in cats.Select((c, i) => (c, i)).Where(x => x.c.Length > 0).GroupBy(x => x.c).Where(g => g.Count() >= 2))
            foreach (var (c, i) in g) res[i] = c;
        var rest = urls.Select((u, i) => (u, i)).Where(x => !res.ContainsKey(x.i) && Uri.TryCreate(x.u, UriKind.Absolute, out _))
            .GroupBy(x => AdBlock.BaseDomain(new Uri(x.u).Host)).Where(g => g.Count() >= 2 && g.Key.Length > 0);
        foreach (var g in rest)
            foreach (var (_, i) in g) res[i] = g.Key;
        return res;
    }
}
