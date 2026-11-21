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

    static readonly Regex HostRx = new(@"^[a-z0-9]([a-z0-9\-\.]*[a-z0-9])?$", RegexOptions.Compiled | RegexOptions.IgnoreCase);
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

        // Сетевые правила: только надёжный и быстрый вид ||домен^
        bool exception = line.StartsWith("@@");
        if (exception) line = line[2..];
        if (!line.StartsWith("||")) return;
        string opts = "";
        int di = line.IndexOf('$');
        if (di >= 0) { opts = line[(di + 1)..]; line = line[..di]; }
        var body = line[2..].TrimEnd('|');
        if (body.EndsWith('^')) body = body[..^1];
        else if (body.IndexOfAny(new[] { '/', '*', '^' }) >= 0) return;
        if (!HostRx.IsMatch(body) || !body.Contains('.')) return;

        var o = opts.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (o.Any(x => x.StartsWith("domain=") || x.StartsWith("denyallow=") || x.StartsWith("from=") || x is "popup" or "~third-party" or "1p" or "first-party" ||
                       x.StartsWith("redirect") || x.StartsWith("removeparam") || x.StartsWith("csp") || x.StartsWith("header") || x.StartsWith("permissions") ||
                       x.StartsWith("rewrite") || x.StartsWith("replace") || x is "generichide" or "elemhide" or "specifichide" or "genericblock"))
            return;

        if (exception) e.Exceptions.Add(body);
        else if (o.Any(x => x is "third-party" or "3p")) e.ThirdParty.Add(body);
        else e.Any.Add(body);
        e.NetworkRules++;
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

    public static bool ShouldBlock(string host, string pageHost)
    {
        var e = engine;
        if (Match(e.Exceptions, host)) return false;
        if (Match(e.Any, host)) return true;
        return pageHost.Length > 0 && BaseDomain(host) != BaseDomain(pageHost) && Match(e.ThirdParty, host);
    }

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
        if (exceptions.Overlaps(e.Generic))
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
