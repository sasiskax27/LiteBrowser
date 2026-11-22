using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace LiteBrowser;

sealed record FilterList(string Id, string Name, string Url, string Description);

sealed class ListStatus
{
    public int Rules { get; set; }
    public DateTime? Updated { get; set; }
    public string? Error { get; set; }
}

/// <summary>Скомпилированные правила всех включённых списков.</summary>
sealed class FilterEngine
{
    public int Version;
    public readonly HashSet<string> Any = new(StringComparer.OrdinalIgnoreCase);
    public readonly HashSet<string> ThirdParty = new(StringComparer.OrdinalIgnoreCase);
    public readonly HashSet<string> Exceptions = new(StringComparer.OrdinalIgnoreCase);
    public readonly HashSet<string> Generic = new();
    public readonly Dictionary<string, List<string>> Specific = new(StringComparer.OrdinalIgnoreCase);
    public readonly Dictionary<string, HashSet<string>> SpecificExceptions = new(StringComparer.OrdinalIgnoreCase);
    public readonly PatternIndex Block = new();
    public readonly PatternIndex Allow = new();
    public readonly HashSet<string> DocumentAllowed = new(StringComparer.OrdinalIgnoreCase); // @@||site^$document
    public readonly HashSet<string> GenericHide = new(StringComparer.OrdinalIgnoreCase);     // @@||site^$generichide
    public string GenericCss = "";
    public int NetworkRules, CosmeticRules;
}

/// <summary>
/// Блокировщик рекламы в стиле uBlock/AdBlock: списки EasyList, EasyPrivacy, RU AdList и др.
/// Поддерживает сетевые правила вида ||домен^ (с $third-party и исключениями @@)
/// и правила скрытия элементов ##селектор (общие и для конкретных сайтов, с исключениями #@#).
/// </summary>
static class AdBlock
{
    public static readonly FilterList[] Lists =
    {
        new("easylist", "EasyList", "https://easylist.to/easylist/easylist.txt", "Основной мировой список рекламы"),
        new("easyprivacy", "EasyPrivacy", "https://easylist.to/easylist/easyprivacy.txt", "Трекеры, счётчики и аналитика"),
        new("ruadlist", "RU AdList", "https://easylist-downloads.adblockplus.org/advblock.txt", "Реклама на русскоязычных сайтах"),
        new("cookies", "Баннеры cookies", "https://secure.fanboy.co.nz/fanboy-cookiemonster.txt", "Прячет окна «Мы используем cookies»"),
    };

    static volatile FilterEngine engine = new();
    static int version;
    public static FilterEngine Engine => engine;
    public static readonly Dictionary<string, ListStatus> Status = Lists.ToDictionary(l => l.Id, _ => new ListStatus());
    public static bool Updating { get; private set; }
    public static event Action? Changed;

    static readonly Regex HostRx = new(@"^[a-z0-9]([a-z0-9\-\.]*[a-z0-9])?$", RegexOptions.IgnoreCase);
    static string Dir => Path.Combine(AppEnv.DataDir, "filters");
    static string FileFor(string id) => Path.Combine(Dir, id + ".txt");

    public static bool Enabled(AppSettings s, string id) => id switch
    {
        "easylist" => s.ListEasyList,
        "easyprivacy" => s.ListEasyPrivacy,
        "ruadlist" => s.ListRuAdList,
        "cookies" => s.ListCookies,
        _ => false,
    };

    public static bool AnyEnabled(AppSettings s) => Lists.Any(l => Enabled(s, l.Id));

    /// <summary>При запуске: сразу собрать правила из кэша, потом обновить устаревшие списки.</summary>
    public static async Task InitAsync(AppSettings s)
    {
        await Task.Run(() => Rebuild(s));
        Changed?.Invoke();
        await UpdateAsync(s, force: false);
    }

    public static async Task UpdateAsync(AppSettings s, bool force)
    {
        if (Updating) return;
        Updating = true;
        Changed?.Invoke();
        bool downloaded = false;
        try
        {
            var handler = new HttpClientHandler { AutomaticDecompression = DecompressionMethods.All };
            if (s.ProxyMode == "all")
                handler.Proxy = new WebProxy($"{(s.ProxyType == "http" ? "http" : "socks5")}://{s.ProxyHost}:{s.ProxyPort}");
            using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(60) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("LiteBrowser/" + AppEnv.Version);
            Directory.CreateDirectory(Dir);
            foreach (var list in Lists.Where(l => Enabled(s, l.Id)))
            {
                var file = FileFor(list.Id);
                bool stale = !File.Exists(file) || (DateTime.Now - File.GetLastWriteTime(file)).TotalDays >= 4;
                if (!force && !stale) continue;
                try
                {
                    var text = await http.GetStringAsync(list.Url);
                    if (text.Length < 10_000) throw new InvalidDataException("Список слишком короткий");
                    await File.WriteAllTextAsync(file + ".tmp", text);
                    File.Move(file + ".tmp", file, true);
                    Status[list.Id].Error = null;
                    downloaded = true;
                }
                catch (Exception ex)
                {
                    Status[list.Id].Error = ex is TaskCanceledException ? "нет ответа от сервера" : ex.Message;
                }
            }
            if (downloaded || force) await Task.Run(() => Rebuild(s));
        }
        finally
        {
            Updating = false;
            Changed?.Invoke();
        }
    }

    /// <summary>Пересобрать правила из сохранённых списков (после смены настроек).</summary>
    public static async Task ReloadAsync(AppSettings s)
    {
        await Task.Run(() => Rebuild(s));
        Changed?.Invoke();
        if (Lists.Any(l => Enabled(s, l.Id) && !File.Exists(FileFor(l.Id)))) await UpdateAsync(s, force: false);
    }

    static void Rebuild(AppSettings s)
    {
        var e = new FilterEngine();
        foreach (var list in Lists)
        {
            var st = Status[list.Id];
            var file = FileFor(list.Id);
            if (!File.Exists(file)) { st.Rules = 0; st.Updated = null; continue; }
            st.Updated = File.GetLastWriteTime(file);
            if (!Enabled(s, list.Id)) { st.Rules = 0; continue; }
            int before = e.NetworkRules + e.CosmeticRules;
            try { foreach (var line in File.ReadLines(file)) Parse(e, line); }
            catch (Exception ex) { st.Error = ex.Message; }
            st.Rules = e.NetworkRules + e.CosmeticRules - before;
        }
        e.GenericCss = ToCss(e.Generic);
        e.Version = Interlocked.Increment(ref version);
        engine = e;
    }

    static readonly string[] BadSelectorParts =
    {
        ":-abp-", ":xpath", ":contains", ":has-text", ":matches-", ":upward", ":remove", ":style(", ":min-text", ":watch-attr", ":others",
        "{", "}", "<", "\\",
    };

    static bool SafeSelector(string s) => s.Length is > 0 and < 500 && !BadSelectorParts.Any(s.Contains);

    static void Parse(FilterEngine e, string raw)
    {
        var line = raw.Trim();
        if (line.Length < 3 || line[0] is '!' or '[') return;
        if (line.Contains("#?#") || line.Contains("#$#") || line.Contains("#%#") || line.Contains("#@$#") || line.Contains("$$")) return;

        int ex = line.IndexOf("#@#", StringComparison.Ordinal);
        if (ex >= 0)
        {
            var sel = line[(ex + 3)..].Trim();
            if (!SafeSelector(sel)) return;
            foreach (var d in line[..ex].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (d.StartsWith('~')) continue;
                if (!e.SpecificExceptions.TryGetValue(d, out var set)) e.SpecificExceptions[d] = set = new HashSet<string>();
                set.Add(sel);
            }
            return;
        }

        int ci = line.IndexOf("##", StringComparison.Ordinal);
        if (ci >= 0)
        {
            var sel = line[(ci + 2)..].Trim();
            if (!SafeSelector(sel)) return;
            var domains = line[..ci].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var positive = domains.Where(d => !d.StartsWith('~')).ToList();
            if (domains.Length == 0) e.Generic.Add(sel);
            else if (positive.Count == 0) return; // «везде, кроме…» — пропускаем, чтобы не ломать сайты
            else
                foreach (var d in positive)
                {
                    if (!e.Specific.TryGetValue(d, out var list)) e.Specific[d] = list = new List<string>();
                    list.Add(sel);
                }
            e.CosmeticRules++;
            return;
        }

        // ---- Сетевые правила ----
        bool exception = line.StartsWith("@@");
        if (exception) line = line[2..];
        string opts = "";
        int di = line.LastIndexOf('$');
        if (di > 0 && !line[di..].Contains('/')) { opts = line[(di + 1)..]; line = line[..di]; }
        var body = line.ToLowerInvariant();
        if (body.Length < 4 || body.Trim('*').Length < 3) return;
        if (body.Length > 2 && body.StartsWith('/') && body.EndsWith('/')) return; // регулярные выражения не поддерживаем

        var rule = new PatternRule();
        bool siteDocument = false, siteGenericHide = false;
        foreach (var raw2 in opts.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var o = raw2.ToLowerInvariant();
            bool neg = o.StartsWith('~');
            var name = neg ? o[1..] : o;
            if (name is "third-party" or "3p") rule.Party = neg ? -1 : 1;
            else if (name is "first-party" or "1p") rule.Party = neg ? 1 : -1;
            else if (o.StartsWith("domain="))
            {
                var ds = o[7..].Split('|', StringSplitOptions.RemoveEmptyEntries);
                var inc = ds.Where(d => !d.StartsWith('~')).ToArray();
                var exc = ds.Where(d => d.StartsWith('~')).Select(d => d[1..]).ToArray();
                if (inc.Any(d => d.Contains('*')) || exc.Any(d => d.Contains('*'))) return;
                rule.Include = inc.Length > 0 ? inc : null;
                rule.Exclude = exc.Length > 0 ? exc : null;
            }
            else if (name is "important") rule.Important = true;
            else if (name is "match-case") { }
            else if (name is "document" or "doc") { if (!exception || neg) return; siteDocument = true; }
            else if (name is "generichide" or "ghide" or "elemhide" or "ehide") { if (!exception) return; siteGenericHide = true; }
            else if (PatternIndex.ParseType(name) is ResType t)
            {
                if (neg) rule.Types = (rule.Types == ResType.All ? ResType.All : rule.Types) & ~t;
                else rule.Types = (rule.Types == ResType.All ? ResType.None : rule.Types) | t;
            }
            else return; // незнакомая опция — пропускаем правило, чтобы ничего не сломать
        }

        // Исключения для целого сайта
        if (siteDocument || siteGenericHide)
        {
            var h = SimpleHost(body);
            if (h == null) return;
            if (siteDocument) e.DocumentAllowed.Add(h);
            if (siteGenericHide) e.GenericHide.Add(h);
            e.NetworkRules++;
            return;
        }
        if (rule.Types == ResType.None) return;

        // Быстрый путь: ||домен^ без условий — в хеш-таблицу
        var simple = SimpleHost(body);
        bool plain = rule.Include == null && rule.Exclude == null && rule.Types == ResType.All && rule.Party >= 0 && !rule.Important;
        if (simple != null && plain)
        {
            if (exception) e.Exceptions.Add(simple);
            else if (rule.Party == 1) e.ThirdParty.Add(simple);
            else e.Any.Add(simple);
            e.NetworkRules++;
            return;
        }

        rule.Pattern = body;
        (exception ? e.Allow : e.Block).Add(rule);
        e.NetworkRules++;
    }

    /// <summary>Если правило — просто ||домен^, вернуть домен.</summary>
    static string? SimpleHost(string body)
    {
        if (!body.StartsWith("||")) return null;
        var h = body[2..].TrimEnd('|');
        if (h.EndsWith('^')) h = h[..^1];
        return HostRx.IsMatch(h) && h.Contains('.') ? h : null;
    }

    /// <summary>Каждый селектор — отдельным правилом: ошибка в одном не ломает остальные.</summary>
    static string ToCss(IEnumerable<string> selectors)
    {
        var sb = new StringBuilder();
        foreach (var s in selectors) sb.Append(s).Append("{display:none!important}\n");
        return sb.ToString();
    }

    static bool Match(HashSet<string> set, string host)
    {
        if (set.Count == 0) return false;
        for (string h = host; ;)
        {
            if (set.Contains(h)) return true;
            int dot = h.IndexOf('.');
            if (dot < 0 || dot == h.Length - 1) return false;
            h = h[(dot + 1)..];
        }
    }

    static IEnumerable<string> Suffixes(string host)
    {
        for (string h = host; ;)
        {
            yield return h;
            int dot = h.IndexOf('.');
            if (dot < 0 || dot == h.Length - 1) yield break;
            h = h[(dot + 1)..];
        }
    }

    /// <summary>Упрощённый «основной домен»: последние две части (vk.com, mail.ru).</summary>
    public static string BaseDomain(string host)
    {
        var p = host.Split('.');
        return p.Length <= 2 ? host : p[^2] + "." + p[^1];
    }

    public static bool IsException(string host) => Match(engine.Exceptions, host);

    /// <summary>Сайт целиком в исключениях списков (@@||site^$document).</summary>
    public static bool PageAllowed(string pageHost) => pageHost.Length > 0 && Match(engine.DocumentAllowed, pageHost);

    /// <summary>Совместимость: проверка только по домену.</summary>
    public static bool ShouldBlock(string host, string pageHost) => ShouldBlock("https://" + host + "/", host, pageHost, ResType.Other);

    public static bool ShouldBlock(string url, string host, string pageHost, ResType type)
    {
        var e = engine;
        if (PageAllowed(pageHost)) return false;
        url = url.ToLowerInvariant();
        host = host.ToLowerInvariant();
        bool third = pageHost.Length > 0 && BaseDomain(host) != BaseDomain(pageHost.ToLowerInvariant());
        bool hostHit = Match(e.Any, host) || (third && Match(e.ThirdParty, host));
        var rule = hostHit ? null : e.Block.Find(url, pageHost, third, type);
        if (!hostHit && rule == null) return false;
        if (rule?.Important == true) return true;
        if (Match(e.Exceptions, host)) return false;
        return e.Allow.Find(url, pageHost, third, type) == null;
    }

    public static int PatternRules => engine.Block.Count + engine.Allow.Count;

    /// <summary>JS, который добавляет на страницу CSS для этого сайта (и убирает общие правила-исключения).</summary>
    public static string PageScript(string host)
    {
        var e = engine;
        var exceptions = new HashSet<string>();
        var specific = new List<string>();
        foreach (var h in Suffixes(host))
        {
            if (e.SpecificExceptions.TryGetValue(h, out var ex)) exceptions.UnionWith(ex);
            if (e.Specific.TryGetValue(h, out var sp)) specific.AddRange(sp);
        }
        var css = ToCss(specific.Where(s => !exceptions.Contains(s)).Distinct());
        var sb = new StringBuilder("(()=>{const add=(id,c)=>{let s=document.getElementById(id);if(!s){s=document.createElement('style');s.id=id;(document.head||document.documentElement).appendChild(s);}s.textContent=c;};");
        if (Match(e.GenericHide, host) || PageAllowed(host))
            sb.Append("{const g=document.getElementById('lite-ab-g');if(g)g.remove();}");
        else if (exceptions.Overlaps(e.Generic))
            sb.Append("add('lite-ab-g',").Append(JsonSerializer.Serialize(ToCss(e.Generic.Where(s => !exceptions.Contains(s))))).Append(");");
        if (css.Length > 0) sb.Append("add('lite-ab-s',").Append(JsonSerializer.Serialize(css)).Append(");");
        sb.Append("})()");
        return sb.ToString();
    }
}

/// <summary>Скрипты блокировщика, которые внедряются в страницы.</summary>
static class AdScripts
{
    /// <summary>Общие правила скрытия + запрос правил для конкретного сайта.</summary>
    public static string Cosmetic(string css) =>
        "(()=>{if(window.top!==window)return;try{const s=document.createElement('style');s.id='lite-ab-g';s.textContent=" + JsonSerializer.Serialize(css) +
        ";(document.head||document.documentElement).appendChild(s);}catch(e){}" +
        "if(window.top===window&&window.chrome&&window.chrome.webview)try{window.chrome.webview.postMessage({type:'ab:css'})}catch(e){}})();";

    public const string Disable = "(()=>{for(const id of ['lite-ab-g','lite-ab-s']){const s=document.getElementById(id);if(s)s.remove();}})()";

    /// <summary>
    /// Реклама YouTube: вырезаем рекламные блоки из ответов плеера, а если реклама всё же началась —
    /// жмём «Пропустить» и проматываем её. YouTube часто меняет сайт, поэтому это работает «как получится».
    /// </summary>
    public const string YouTube = """
(() => {
  if (!/(^|\.)youtube(-nocookie)?\.com$/.test(location.hostname)) return;
  const keys = ['adPlacements', 'playerAds', 'adSlots', 'adBreakHeartbeatParams'];
  const prune = o => {
    try {
      if (o && typeof o === 'object') {
        for (const k of keys) if (k in o) delete o[k];
        if (o.playerResponse) prune(o.playerResponse);
      }
    } catch (e) {}
    return o;
  };
  const parse = JSON.parse;
  JSON.parse = function () { return prune(parse.apply(this, arguments)); };
  let ipr;
  try { Object.defineProperty(window, 'ytInitialPlayerResponse', { configurable: true, get: () => ipr, set: v => { ipr = prune(v); } }); } catch (e) {}
  const origFetch = window.fetch;
  window.fetch = async function (...a) {
    const r = await origFetch.apply(this, a);
    try {
      const url = (a[0] && a[0].url) || String(a[0]);
      if (/\/youtubei\/v1\/(player|next)/.test(url) && r.ok) {
        const j = prune(await r.clone().json());
        const h = new Headers(r.headers); h.delete('content-length'); h.delete('content-encoding');
        return new Response(JSON.stringify(j), { status: r.status, statusText: r.statusText, headers: h });
      }
    } catch (e) {}
    return r;
  };
  setInterval(() => {
    const p = document.querySelector('.html5-video-player');
    if (p && p.classList.contains('ad-showing')) {
      const v = p.querySelector('video');
      if (v) { v.muted = true; v.playbackRate = 16; if (isFinite(v.duration) && v.duration > 0) v.currentTime = v.duration - 0.1; }
    }
    const skip = document.querySelector('.ytp-skip-ad-button, .ytp-ad-skip-button, .ytp-ad-skip-button-modern, .ytp-ad-skip-button-container button');
    if (skip) skip.click();
    for (const el of document.querySelectorAll('ytd-ad-slot-renderer, ytd-in-feed-ad-layout-renderer, ytd-promoted-sparkles-web-renderer, ytd-banner-promo-renderer, #player-ads, ytd-rich-item-renderer:has(ytd-ad-slot-renderer)'))
      el.style.setProperty('display', 'none', 'important');
  }, 300);
})();
""";
}
