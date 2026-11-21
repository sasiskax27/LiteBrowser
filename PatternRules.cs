using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Web.WebView2.Core;

namespace LiteBrowser;

[Flags]
enum ResType
{
    None = 0, Script = 1, Image = 2, Stylesheet = 4, Xhr = 8, Subdocument = 16, Media = 32, Font = 64, Websocket = 128, Ping = 256, Other = 512,
    All = 1023,
}

/// <summary>Сетевое правило по адресу: /ads/*, ||yandex.ru/ads/^, с условиями $domain=, $third-party и типами ресурсов.</summary>
sealed class PatternRule
{
    public string Pattern = "";
    public bool Important;
    public int Party;                 // 0 — любой, 1 — только сторонний, -1 — только свой сайт
    public ResType Types = ResType.All;
    public string[]? Include, Exclude;
    HashSet<string>? inc, exc;
    Regex? rx;
    bool broken;

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

    public bool AppliesTo(string pageHost, bool thirdParty, ResType type)
    {
        if ((Types & type) == 0) return false;
        if (Party == 1 && !thirdParty) return false;
        if (Party == -1 && thirdParty) return false;
        if (Exclude != null && pageHost.Length > 0)
        {
            exc ??= new HashSet<string>(Exclude, StringComparer.OrdinalIgnoreCase);
            if (InSet(exc, pageHost)) return false;
        }
        if (Include != null)
        {
            if (pageHost.Length == 0) return false;
            inc ??= new HashSet<string>(Include, StringComparer.OrdinalIgnoreCase);
            if (!InSet(inc, pageHost)) return false;
        }
        return true;
    }

    string[]? literals;

    public bool MatchUrl(string url)
    {
        if (broken) return false;
        // Быстрая предпроверка: все текстовые куски правила должны встречаться в адресе по порядку
        literals ??= Pattern.TrimStart('|').TrimEnd('|').Split(new[] { '*', '^' }, StringSplitOptions.RemoveEmptyEntries);
        int pos = 0;
        foreach (var lit in literals)
        {
            int at = url.IndexOf(lit, pos, StringComparison.Ordinal);
            if (at < 0) return false;
            pos = at + lit.Length;
        }
        try
        {
            rx ??= Build(Pattern);
            return rx.IsMatch(url);
        }
        catch { broken = true; return false; }
    }

    /// <summary>Перевод синтаксиса Adblock в регулярное выражение (лениво — только для правил, которые реально проверяются).</summary>
    static Regex Build(string p)
    {
        var sb = new StringBuilder();
        int i = 0;
        if (p.StartsWith("||")) { sb.Append(@"^[a-z][a-z0-9+.\-]*://(?:[^/?#]*\.)?"); i = 2; }
        else if (p.StartsWith('|')) { sb.Append('^'); i = 1; }
        bool end = p.Length > i && p.EndsWith('|');
        int stop = end ? p.Length - 1 : p.Length;
        for (; i < stop; i++)
        {
            char c = p[i];
            if (c == '*') sb.Append(".*");
            else if (c == '^') sb.Append(@"(?:[^a-z0-9_\-.%]|$)");
            else sb.Append(Regex.Escape(c.ToString()));
        }
        if (end) sb.Append('$');
        return new Regex(sb.ToString(), RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(20));
    }
}

/// <summary>
/// Индекс правил по «токенам» (как в uBlock): для каждого адреса проверяются только правила,
/// в которых есть слово из этого адреса. Поэтому десятки тысяч правил проверяются за микросекунды.
/// </summary>
sealed class PatternIndex
{
    static readonly HashSet<string> BadTokens = new() { "http", "https", "www", "com", "net", "org", "ru", "js", "html", "php", "png", "jpg", "gif", "css" };
    readonly Dictionary<string, List<PatternRule>> byToken = new();
    // Правила без опорного слова проверяем только на сайтах из их $domain=
    readonly Dictionary<string, List<PatternRule>> bySite = new(StringComparer.OrdinalIgnoreCase);
    public int Count { get; private set; }
    public int Skipped { get; private set; }

    public static bool HostMatches(string host, string domain) =>
        host.Equals(domain, StringComparison.OrdinalIgnoreCase) ||
        host.EndsWith("." + domain, StringComparison.OrdinalIgnoreCase);

    static bool IsTokenChar(char c) => c is >= 'a' and <= 'z' or >= '0' and <= '9' or '%';

    public void Add(PatternRule r)
    {
        var token = BestToken(r.Pattern);
        if (token == null)
        {
            if (r.Include == null) { Skipped++; return; } // правило для всех сайтов без опорного слова слишком дорогое
            foreach (var d in r.Include)
            {
                if (!bySite.TryGetValue(d, out var sl)) bySite[d] = sl = new List<PatternRule>();
                sl.Add(r);
            }
        }
        else
        {
            if (!byToken.TryGetValue(token, out var list)) byToken[token] = list = new List<PatternRule>();
            list.Add(r);
        }
        Count++;
    }

    /// <summary>Самое длинное слово правила, которое точно целиком встретится в подходящем адресе.</summary>
    static string? BestToken(string p)
    {
        string? best = null;
        int i = 0;
        while (i < p.Length)
        {
            if (!IsTokenChar(p[i])) { i++; continue; }
            int s = i;
            while (i < p.Length && IsTokenChar(p[i])) i++;
            int len = i - s;
            if (len < 2 || s == 0 || i >= p.Length) continue;           // на краю правила слово может быть обрезано
            if (p[s - 1] == '*' || p[i] == '*') continue;
            var t = p.Substring(s, len);
            if (BadTokens.Contains(t)) continue;
            if (best == null || t.Length > best.Length) best = t;
        }
        return best;
    }

    public PatternRule? Find(string url, string pageHost, bool thirdParty, ResType type)
    {
        if (bySite.Count > 0 && pageHost.Length > 0)
        {
            for (string h = pageHost; ;)
            {
                if (bySite.TryGetValue(h, out var sl))
                    foreach (var r in sl)
                        if (r.AppliesTo(pageHost, thirdParty, type) && r.MatchUrl(url)) return r;
                int dot = h.IndexOf('.');
                if (dot < 0) break;
                h = h[(dot + 1)..];
            }
        }
        int i = 0;
        HashSet<string>? seen = null;
        while (i < url.Length)
        {
            if (!IsTokenChar(url[i])) { i++; continue; }
            int s = i;
            while (i < url.Length && IsTokenChar(url[i])) i++;
            if (i - s < 2) continue;
            var t = url.Substring(s, i - s);
            if (!byToken.TryGetValue(t, out var list)) continue;
            seen ??= new HashSet<string>();
            if (!seen.Add(t)) continue;
            foreach (var r in list)
                if (r.AppliesTo(pageHost, thirdParty, type) && r.MatchUrl(url)) return r;
        }
        return null;
    }

    public static ResType FromContext(CoreWebView2WebResourceContext c) => c switch
    {
        CoreWebView2WebResourceContext.Script => ResType.Script,
        CoreWebView2WebResourceContext.Image => ResType.Image,
        CoreWebView2WebResourceContext.Stylesheet => ResType.Stylesheet,
        CoreWebView2WebResourceContext.XmlHttpRequest or CoreWebView2WebResourceContext.Fetch or CoreWebView2WebResourceContext.EventSource => ResType.Xhr,
        CoreWebView2WebResourceContext.Document => ResType.Subdocument,
        CoreWebView2WebResourceContext.Media => ResType.Media,
        CoreWebView2WebResourceContext.Font => ResType.Font,
        CoreWebView2WebResourceContext.Websocket => ResType.Websocket,
        CoreWebView2WebResourceContext.Ping => ResType.Ping,
        _ => ResType.Other,
    };

    public static ResType? ParseType(string o) => o switch
    {
        "script" => ResType.Script,
        "image" => ResType.Image,
        "stylesheet" or "css" => ResType.Stylesheet,
        "xmlhttprequest" or "xhr" => ResType.Xhr,
        "subdocument" or "frame" => ResType.Subdocument,
        "media" => ResType.Media,
        "font" => ResType.Font,
        "websocket" => ResType.Websocket,
        "ping" or "beacon" => ResType.Ping,
        "other" or "object" or "object-subrequest" => ResType.Other,
        _ => null,
    };
}
