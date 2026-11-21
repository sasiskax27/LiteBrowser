using System.Text.Json;
using Microsoft.Web.WebView2.Core;

namespace LiteBrowser;

/// <summary>Лёгкие инструменты разработчика: сеть, консоль, производительность для текущей вкладки.</summary>
sealed partial class MainForm
{
    System.Windows.Forms.Timer? devTimer;

    void OpenDevTools()
    {
        ToggleSidePanel(true);
        if (sidePanel.Core?.Source?.StartsWith(LitePages.DevTools) != true) sidePanel.Navigate(LitePages.DevTools);
    }

    /// <summary>Какую вкладку исследовать: последнюю активную с обычным сайтом.</summary>
    BrowserTab? DevTarget(CoreWebView2 caller) =>
        tabs.Where(t => t.Core != null && t.Core != caller && !t.Core.Source.StartsWith(LitePages.Prefix, StringComparison.Ordinal))
            .OrderByDescending(t => t == active).ThenByDescending(t => t.LastActive).FirstOrDefault();

    void EnsureDevTimer()
    {
        if (devTimer != null) return;
        // Отключаемся от вкладок, если страница инструментов закрыта (нет опроса 30 секунд)
        devTimer = new System.Windows.Forms.Timer { Interval = 10_000 };
        devTimer.Tick += (_, _) =>
        {
            bool any = false;
            foreach (var t in tabs)
            {
                if (t.Dev is not { Attached: true } d) continue;
                if (DateTime.Now - d.LastPoll > TimeSpan.FromSeconds(30)) d.Detach(); else any = true;
            }
            if (!any) { devTimer!.Stop(); devTimer.Dispose(); devTimer = null; }
        };
        devTimer.Start();
    }

    async Task HandleDevMessage(CoreWebView2 core, string type, JsonElement root)
    {
        string Str(string name) => root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
        long Long(string name) => root.TryGetProperty(name, out var v) && v.TryGetInt64(out var l) ? l : 0;

        var tab = DevTarget(core);
        if (tab?.Core == null) { Post(core, new { type = "devNoTarget" }); return; }
        tab.Dev ??= new DevMonitor(tab.Core);
        var dev = tab.Dev;
        dev.LastPoll = DateTime.Now;
        if (!dev.Attached) { await dev.AttachAsync(); EnsureDevTimer(); }

        switch (type)
        {
            case "dev:poll":
                dev.PreserveLog = root.TryGetProperty("preserve", out var pr) && pr.ValueKind == JsonValueKind.True;
                long since = Long("since");
                Post(core, new
                {
                    type = "devData",
                    target = tab.GetHashCode(),
                    gen = dev.Gen,
                    title = tab.Title,
                    url = tab.Core.Source,
                    net = dev.Net.Where(n => n.Seq > since).Select(n => new
                    {
                        id = n.Id, seq = n.Seq, url = n.Url, method = n.Method, rtype = n.Type, status = n.Status, mime = n.Mime, ip = n.Ip,
                        proto = n.Protocol, size = n.Size, ms = Math.Round(n.Ms), error = n.Error, blocked = n.Blocked, cached = n.Cached,
                        done = n.Done, third = n.ThirdParty,
                    }),
                    console = dev.Console.Where(c => c.Seq > since).Select(c => new { seq = c.Seq, level = c.Level, text = c.Text, src = c.Source, time = c.Time.ToString("HH:mm:ss") }),
                });
                break;

            case "dev:eval":
                var expr = Str("expr");
                if (expr.Length == 0 || expr.Length > 20000) return;
                try
                {
                    var (text, error) = await dev.EvaluateAsync(expr);
                    Post(core, new { type = "devEval", expr, text, error });
                }
                catch (Exception ex) { Post(core, new { type = "devEval", expr, text = ex.Message, error = true }); }
                break;

            case "dev:clear":
                dev.Clear();
                break;

            case "dev:perf":
                var metrics = await dev.MetricsAsync();
                string timing = "";
                try { timing = JsonSerializer.Deserialize<string>(await tab.Core.ExecuteScriptAsync(DevMonitor.TimingScript)) ?? ""; } catch { }
                Post(core, new
                {
                    type = "devPerf",
                    heap = metrics.GetValueOrDefault("JSHeapUsedSize"), heapTotal = metrics.GetValueOrDefault("JSHeapTotalSize"),
                    nodes = metrics.GetValueOrDefault("Nodes"), listeners = metrics.GetValueOrDefault("JSEventListeners"),
                    documents = metrics.GetValueOrDefault("Documents"), frames = metrics.GetValueOrDefault("Frames"),
                    layouts = metrics.GetValueOrDefault("LayoutCount"), styles = metrics.GetValueOrDefault("RecalcStyleCount"),
                    scriptMs = metrics.GetValueOrDefault("ScriptDuration") * 1000, layoutMs = metrics.GetValueOrDefault("LayoutDuration") * 1000,
                    timing,
                });
                break;

            case "dev:native":
                tab.Core.OpenDevToolsWindow();
                break;

            case "dev:open":
                var u = Str("url");
                if (Uri.TryCreate(u, UriKind.Absolute, out var nu) && nu.Scheme is "http" or "https") _ = AddTab(nu.ToString());
                break;
        }
    }
}
