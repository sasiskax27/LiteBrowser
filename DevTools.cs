using System.Text.Json;
using Microsoft.Web.WebView2.Core;

namespace LiteBrowser;

sealed class NetEntry
{
    public string Id { get; set; } = "";
    public long Seq { get; set; }
    public string Url { get; set; } = "";
    public string Method { get; set; } = "";
    public string Type { get; set; } = "";
    public int Status { get; set; }
    public string Mime { get; set; } = "";
    public string Ip { get; set; } = "";
    public string Protocol { get; set; } = "";
    public long Size { get; set; }
    public double Start { get; set; }      // секунды (CDP timestamp)
    public double Ms { get; set; }         // длительность
    public string Error { get; set; } = "";
    public bool Blocked { get; set; }
    public bool Cached { get; set; }
    public bool Done { get; set; }
    public bool ThirdParty { get; set; }
}

sealed class ConsoleEntry
{
    public long Seq { get; set; }
    public string Level { get; set; } = "log";   // log | info | warn | error | debug | result
    public string Text { get; set; } = "";
    public string Source { get; set; } = "";
    public DateTime Time { get; set; } = DateTime.Now;
}

/// <summary>
/// Лёгкие инструменты разработчика: сетевые запросы и консоль вкладки через протокол DevTools (CDP).
/// Подключается только пока открыта страница lite://devtools; буферы ограничены.
/// </summary>
sealed class DevMonitor
{
    const int MaxNet = 1500, MaxConsole = 1000;
    static long seq;
    readonly CoreWebView2 core;
    readonly Dictionary<string, NetEntry> byId = new();
    readonly List<(CoreWebView2DevToolsProtocolEventReceiver r, EventHandler<CoreWebView2DevToolsProtocolEventReceivedEventArgs> h)> subs = new();
    public readonly List<NetEntry> Net = new();
    public readonly List<ConsoleEntry> Console = new();
    public DateTime LastPoll = DateTime.Now;
    public bool PreserveLog;
    public int Gen;
    public bool Attached { get; private set; }
    string pageHost = "";

    public DevMonitor(CoreWebView2 core) { this.core = core; }

    public async Task AttachAsync()
    {
        if (Attached) return;
        Attached = true;
        On("Network.requestWillBeSent", OnRequest);
        On("Network.responseReceived", OnResponse);
        On("Network.loadingFinished", OnFinished);
        On("Network.loadingFailed", OnFailed);
        On("Network.requestServedFromCache", j => { if (Get(j) is { } e) { e.Cached = true; Touch(e); } });
        On("Runtime.consoleAPICalled", OnConsole);
        On("Runtime.exceptionThrown", OnException);
        On("Log.entryAdded", OnLog);
        try
        {
            await core.CallDevToolsProtocolMethodAsync("Network.enable", "{}");
            await core.CallDevToolsProtocolMethodAsync("Runtime.enable", "{}");
            await core.CallDevToolsProtocolMethodAsync("Log.enable", "{}");
            await core.CallDevToolsProtocolMethodAsync("Performance.enable", "{}");
        }
        catch { }
        pageHost = Uri.TryCreate(core.Source, UriKind.Absolute, out var u) ? u.Host : "";
    }

    public void Detach()
    {
        if (!Attached) return;
        Attached = false;
        foreach (var (r, h) in subs) r.DevToolsProtocolEventReceived -= h;
        subs.Clear();
        foreach (var m in new[] { "Network.disable", "Log.disable", "Performance.disable" })
            try { _ = core.CallDevToolsProtocolMethodAsync(m, "{}"); } catch { }
    }

    void On(string name, Action<JsonElement> handler)
    {
        var r = core.GetDevToolsProtocolEventReceiver(name);
        EventHandler<CoreWebView2DevToolsProtocolEventReceivedEventArgs> h = (_, e) =>
        {
            try { using var d = JsonDocument.Parse(e.ParameterObjectAsJson); handler(d.RootElement); }
            catch { }
        };
        r.DevToolsProtocolEventReceived += h;
        subs.Add((r, h));
    }

    static string S(JsonElement j, string name) => j.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
    static double D(JsonElement j, string name) => j.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : 0;
    NetEntry? Get(JsonElement j) => byId.GetValueOrDefault(S(j, "requestId"));
    static void Touch(NetEntry e) => e.Seq = Interlocked.Increment(ref seq);

    public void Clear() { Net.Clear(); byId.Clear(); Console.Clear(); Gen++; }

    void OnRequest(JsonElement j)
    {
        var id = S(j, "requestId");
        var type = S(j, "type");
        var req = j.GetProperty("request");
        var url = S(req, "url");
        if (url.StartsWith("data:", StringComparison.Ordinal)) url = url.Length > 120 ? url[..120] + "…" : url;
        if (byId.TryGetValue(id, out var existing))
        {
            // Перенаправление: старая запись остаётся, новая продолжает тот же requestId
            existing.Done = true;
            if (j.TryGetProperty("redirectResponse", out var rr)) existing.Status = (int)D(rr, "status");
            byId.Remove(id);
            Touch(existing);
        }
        var e = new NetEntry
        {
            Id = id + "#" + Net.Count, Url = url, Method = S(req, "method"), Type = type, Start = D(j, "timestamp"),
            ThirdParty = Uri.TryCreate(url, UriKind.Absolute, out var ru) && pageHost.Length > 0 && AdBlock.BaseDomain(ru.Host) != AdBlock.BaseDomain(pageHost),
        };
        Touch(e);
        byId[id] = e;
        Net.Add(e);
        if (Net.Count > MaxNet)
        {
            var old = Net[0];
            Net.RemoveAt(0);
            foreach (var kv in byId.Where(kv => kv.Value == old).ToList()) byId.Remove(kv.Key);
        }
    }

    /// <summary>Вкладка начала открывать новую страницу (вызывается из NavigationStarting).</summary>
    public void OnNavigation(string url)
    {
        if (Uri.TryCreate(url, UriKind.Absolute, out var u)) pageHost = u.Host;
        if (!PreserveLog) { Net.Clear(); byId.Clear(); Gen++; AddConsole("info", "Переход на " + url, ""); }
    }

    void OnResponse(JsonElement j)
    {
        if (Get(j) is not { } e) return;
        var r = j.GetProperty("response");
        e.Status = (int)D(r, "status");
        e.Mime = S(r, "mimeType");
        e.Ip = S(r, "remoteIPAddress");
        e.Protocol = S(r, "protocol");
        if (r.TryGetProperty("fromDiskCache", out var c) && c.ValueKind == JsonValueKind.True) e.Cached = true;
        if (e.Status == 403 && S(r, "statusText") is "Blocked" or "Data saver") e.Blocked = true;
        Touch(e);
    }

    void OnFinished(JsonElement j)
    {
        if (Get(j) is not { } e) return;
        e.Size = (long)D(j, "encodedDataLength");
        e.Ms = Math.Max(0, (D(j, "timestamp") - e.Start) * 1000);
        e.Done = true;
        Touch(e);
    }

    void OnFailed(JsonElement j)
    {
        if (Get(j) is not { } e) return;
        e.Error = S(j, "errorText");
        var br = S(j, "blockedReason");
        if (br.Length > 0) { e.Blocked = true; e.Error = br; }
        if (j.TryGetProperty("canceled", out var c) && c.ValueKind == JsonValueKind.True) e.Error = "отменён";
        e.Ms = Math.Max(0, (D(j, "timestamp") - e.Start) * 1000);
        e.Done = true;
        Touch(e);
    }

    void AddConsole(string level, string text, string source)
    {
        if (text.Length > 4000) text = text[..4000] + "…";
        Console.Add(new ConsoleEntry { Seq = Interlocked.Increment(ref seq), Level = level, Text = text, Source = source });
        if (Console.Count > MaxConsole) Console.RemoveAt(0);
    }

    static string Where(JsonElement j)
    {
        if (j.TryGetProperty("stackTrace", out var st) && st.TryGetProperty("callFrames", out var cf) && cf.GetArrayLength() > 0)
        {
            var f = cf[0];
            var url = S(f, "url");
            return url.Length == 0 ? "" : Path.GetFileName(new Uri(url, UriKind.RelativeOrAbsolute).IsAbsoluteUri ? new Uri(url).AbsolutePath : url) + ":" + ((int)D(f, "lineNumber") + 1);
        }
        return "";
    }

    public static string Describe(JsonElement a)
    {
        var t = S(a, "type");
        if (a.TryGetProperty("value", out var v))
            return v.ValueKind switch
            {
                JsonValueKind.String => v.GetString() ?? "",
                JsonValueKind.Object or JsonValueKind.Array => v.GetRawText(),
                _ => v.ToString(),
            };
        if (t == "undefined") return "undefined";
        if (a.TryGetProperty("unserializableValue", out var uv)) return uv.ToString();
        if (a.TryGetProperty("preview", out var p) && p.TryGetProperty("properties", out var props))
        {
            bool arr = S(p, "subtype") == "array";
            var parts = props.EnumerateArray().Take(12).Select(x => arr ? S(x, "value") : S(x, "name") + ": " + S(x, "value"));
            var body = string.Join(", ", parts) + (p.TryGetProperty("overflow", out var of) && of.ValueKind == JsonValueKind.True ? ", …" : "");
            return arr ? "[" + body + "]" : S(a, "className") is { Length: > 0 } cn && cn != "Object" ? cn + " {" + body + "}" : "{" + body + "}";
        }
        return S(a, "description") is { Length: > 0 } d ? d : t;
    }

    void OnConsole(JsonElement j)
    {
        var type = S(j, "type");
        var level = type switch { "error" or "assert" => "error", "warning" => "warn", "info" => "info", "debug" => "debug", _ => "log" };
        var args = j.TryGetProperty("args", out var a) ? a.EnumerateArray().Select(Describe) : Enumerable.Empty<string>();
        var text = string.Join(" ", args);
        if (type == "clear") { Console.Clear(); text = "Консоль очищена страницей"; level = "info"; }
        AddConsole(level, text, Where(j));
    }

    void OnException(JsonElement j)
    {
        var d = j.GetProperty("exceptionDetails");
        var text = d.TryGetProperty("exception", out var ex) && S(ex, "description") is { Length: > 0 } desc ? desc : S(d, "text");
        var url = S(d, "url");
        var src = url.Length > 0 ? Path.GetFileName(Uri.TryCreate(url, UriKind.Absolute, out var u) ? u.AbsolutePath : url) + ":" + ((int)D(d, "lineNumber") + 1) : Where(d);
        AddConsole("error", text, src);
    }

    void OnLog(JsonElement j)
    {
        var e = j.GetProperty("entry");
        var level = S(e, "level") switch { "error" => "error", "warning" => "warn", "verbose" => "debug", _ => "info" };
        var url = S(e, "url");
        AddConsole(level, S(e, "text") + (url.Length > 0 && !S(e, "text").Contains(url) ? " — " + url : ""), S(e, "source"));
    }

    public async Task<(string text, bool error)> EvaluateAsync(string expr)
    {
        var json = await core.CallDevToolsProtocolMethodAsync("Runtime.evaluate", JsonSerializer.Serialize(new
        {
            expression = expr, replMode = true, generatePreview = true, userGesture = true, awaitPromise = true, timeout = 5000,
        }));
        using var d = JsonDocument.Parse(json);
        var root = d.RootElement;
        if (root.TryGetProperty("exceptionDetails", out var ex))
            return (ex.TryGetProperty("exception", out var e) && S(e, "description") is { Length: > 0 } s ? s : S(ex, "text"), true);
        return (root.TryGetProperty("result", out var r) ? Describe(r) : "", false);
    }

    public async Task<Dictionary<string, double>> MetricsAsync()
    {
        var res = new Dictionary<string, double>();
        try
        {
            using var d = JsonDocument.Parse(await core.CallDevToolsProtocolMethodAsync("Performance.getMetrics", "{}"));
            foreach (var m in d.RootElement.GetProperty("metrics").EnumerateArray()) res[S(m, "name")] = D(m, "value");
        }
        catch { }
        return res;
    }

    public const string TimingScript = @"(() => {
      const n = performance.getEntriesByType('navigation')[0];
      const p = performance.getEntriesByType('paint');
      const fcp = p.find(x => x.name === 'first-contentful-paint');
      const res = performance.getEntriesByType('resource');
      return JSON.stringify({
        url: location.href, title: document.title,
        ttfb: n ? n.responseStart - n.requestStart : 0, dom: n ? n.domContentLoadedEventEnd : 0, load: n ? n.loadEventEnd : 0,
        fcp: fcp ? fcp.startTime : 0, transfer: n ? n.transferSize : 0, resources: res.length,
        resBytes: res.reduce((a, r) => a + (r.transferSize || 0), 0), dom_nodes: document.getElementsByTagName('*').length,
        cookies: document.cookie ? document.cookie.split(';').length : 0, storage: (() => { try { return localStorage.length; } catch (e) { return -1; } })(),
      });
    })()";
}
