using System.Xml;
using System.Xml.Linq;

namespace LiteBrowser;

/// <summary>
/// Новости на стартовой странице: подписки на сайты по RSS/Atom, без сторонних сервисов.
/// Ленты скачивает сам браузер раз в 15 минут; показываем свежие заголовки со всех подписок.
/// </summary>
static class Feeds
{
    public sealed record Item(string Title, string Link, string Source, DateTime Time);

    static readonly HttpClient http = new() { Timeout = TimeSpan.FromSeconds(10) };
    static List<Item> cache = new();
    static DateTime cachedAt = DateTime.MinValue;
    static string cachedSig = "";

    static Feeds() => http.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) LiteBrowser/" + AppEnv.Version);

    /// <summary>Свежие новости со всех лент (по 15 минут кэш).</summary>
    public static async Task<List<Item>> LatestAsync(IReadOnlyList<string> feeds, int count = 8)
    {
        var sig = string.Join("|", feeds);
        if (sig == cachedSig && DateTime.Now - cachedAt < TimeSpan.FromMinutes(15)) return cache.Take(count).ToList();
        var tasks = feeds.Take(20).Select(async f => { try { return Parse(await http.GetStringAsync(f), f); } catch { return new List<Item>(); } });
        var all = (await Task.WhenAll(tasks)).SelectMany(x => x).ToList();
        cache = all.OrderByDescending(i => i.Time).DistinctBy(i => i.Link).Take(50).ToList();
        cachedAt = DateTime.Now; cachedSig = sig;
        return cache.Take(count).ToList();
    }

    public static void Invalidate() => cachedSig = "";

    /// <summary>RSS 2.0, RSS 1.0 (RDF) и Atom.</summary>
    internal static List<Item> Parse(string xml, string feedUrl)
    {
        var list = new List<Item>();
        // Без внешних сущностей и DTD — защита от «XML-бомб»
        using var reader = XmlReader.Create(new StringReader(xml), new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore, XmlResolver = null, MaxCharactersInDocument = 20_000_000 });
        var doc = XDocument.Load(reader);
        var root = doc.Root;
        if (root == null) return list;
        string source = "";
        try { source = new Uri(feedUrl).Host.Replace("www.", ""); } catch { }
        var channelTitle = root.Descendants().FirstOrDefault(e => e.Name.LocalName == "title")?.Value?.Trim();
        if (!string.IsNullOrEmpty(channelTitle) && channelTitle.Length < 60) source = channelTitle;
        foreach (var e in root.Descendants().Where(e => e.Name.LocalName is "item" or "entry"))
        {
            string El(string n) => e.Elements().FirstOrDefault(x => x.Name.LocalName == n)?.Value?.Trim() ?? "";
            var title = System.Net.WebUtility.HtmlDecode(El("title"));
            var link = El("link");
            if (link.Length == 0) link = e.Elements().FirstOrDefault(x => x.Name.LocalName == "link" && ((string?)x.Attribute("rel") ?? "alternate") == "alternate")?.Attribute("href")?.Value ?? "";
            if (link.Length == 0) link = e.Elements().FirstOrDefault(x => x.Name.LocalName == "link")?.Attribute("href")?.Value ?? "";
            if (title.Length == 0 || !Uri.TryCreate(link, UriKind.Absolute, out var lu) || lu.Scheme is not ("http" or "https")) continue;
            var dateText = El("pubDate"); if (dateText.Length == 0) dateText = El("updated"); if (dateText.Length == 0) dateText = El("published"); if (dateText.Length == 0) dateText = El("date");
            var time = ParseDate(dateText);
            list.Add(new Item(title.Length > 200 ? title[..200] : title, lu.ToString(), source, time));
        }
        return list;
    }

    static DateTime ParseDate(string s)
    {
        if (DateTimeOffset.TryParse(s, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AllowWhiteSpaces, out var d)) return d.LocalDateTime;
        // RFC 822 с названием зоны (GMT, MSK…) — убираем зону
        var parts = s.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length > 4 && DateTime.TryParse(string.Join(' ', parts.Take(5)), System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AllowWhiteSpaces, out var d2)) return d2;
        return DateTime.MinValue;
    }

    /// <summary>Скрипт: адрес ленты сайта из &lt;link rel="alternate"&gt;.</summary>
    public const string FindFeedJs = """
(() => {
  const l = document.querySelector('link[rel="alternate"][type*="rss"], link[rel="alternate"][type*="atom"]');
  return l ? new URL(l.getAttribute('href'), location.href).href : '';
})()
""";
}
