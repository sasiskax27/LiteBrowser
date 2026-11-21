using System.Net.Sockets;
using System.Text;
using Microsoft.Web.WebView2.Core;

namespace LiteBrowser;

/// <summary>Общие для всех окон: папка данных, настройки и движок WebView2.</summary>
static class AppEnv
{
    public const string Version = "3.2.0";
    /// <summary>Папка данных. LITEBROWSER_DATA — отдельная папка для тестов (тогда и отдельный экземпляр браузера).</summary>
    public static readonly string DataDir = Environment.GetEnvironmentVariable("LITEBROWSER_DATA") is { Length: > 0 } d ? d
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LiteBrowser");
    public static string InstanceName => "LiteBrowser-" + Environment.UserName + (Environment.GetEnvironmentVariable("LITEBROWSER_DATA") is { Length: > 0 } d2 ? "-" + Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(Encoding.UTF8.GetBytes(d2)))[..8] : "");

    /// <summary>Журнал для отладки — пишется только если задана переменная LITEBROWSER_DEBUG.</summary>
    public static void Log(string s)
    {
        if (Environment.GetEnvironmentVariable("LITEBROWSER_DEBUG") is not { Length: > 0 }) return;
        try { File.AppendAllText(Path.Combine(DataDir, "debug.log"), $"{DateTime.Now:HH:mm:ss.fff} {s}\r\n"); } catch { }
    }

    public static AppSettings Settings { get; private set; } = new();
    /// <summary>С какими настройками прокси реально запущен движок.</summary>
    public static string RunningSignature { get; private set; } = "off";
    public static bool RestartNeeded => Settings.EngineSignature() != RunningSignature;
    public static event Action? SettingsChanged;

    static Task<CoreWebView2Environment>? envTask;

    public static void Init()
    {
        Settings = AppSettings.Load(DataDir);
        Store.Load(DataDir);
        Blocklist.LoadCached(DataDir);
        Downloads.Load();
    }

    public static void UpdateSettings(AppSettings s)
    {
        Settings = s.Normalize();
        Settings.Save(DataDir);
        SettingsChanged?.Invoke();
    }

    public static Task<CoreWebView2Environment> GetEnvironment() => envTask ??= CreateEnvironment();

    static async Task<CoreWebView2Environment> CreateEnvironment()
    {
        var s = Settings;
        var options = new CoreWebView2EnvironmentOptions
        {
            EnableTrackingPrevention = true,
            AreBrowserExtensionsEnabled = true,
            AdditionalBrowserArguments = BrowserArguments(s),
        };
        // После перезапуска старый процесс движка может ещё закрываться — пробуем несколько раз.
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                var env = await CoreWebView2Environment.CreateAsync(null, DataDir, options);
                RunningSignature = s.EngineSignature();
                return env;
            }
            catch (Exception) when (attempt < 20)
            {
                await Task.Delay(500);
            }
        }
    }

    static string BrowserArguments(AppSettings s)
    {
        if (s.ProxyMode == "off") return "";
        var sb = new StringBuilder();
        string hostPort = $"{s.ProxyHost}:{s.ProxyPort}";
        if (s.ProxyMode == "all")
        {
            sb.Append($"--proxy-server={(s.ProxyType == "http" ? "http" : "socks5")}://{hostPort}");
        }
        else
        {
            // Только выбранные сайты — через PAC-скрипт, остальные напрямую
            string target = (s.ProxyType == "http" ? "PROXY " : "SOCKS5 ") + hostPort;
            string list = string.Join(",", s.ProxySites.Select(d => "\"" + d + "\""));
            string pac =
                "function FindProxyForURL(url, host) {" +
                $" var d = [{list}]; host = host.toLowerCase();" +
                " for (var i = 0; i < d.length; i++) { if (host === d[i] || dnsDomainIs(host, '.' + d[i])) return '" + target + "'; }" +
                " return 'DIRECT'; }";
            sb.Append("--proxy-pac-url=data:application/x-ns-proxy-autoconfig;base64,")
              .Append(Convert.ToBase64String(Encoding.UTF8.GetBytes(pac)));
        }
        // Не даём WebRTC раскрыть настоящий IP в обход прокси
        sb.Append(" --force-webrtc-ip-handling-policy=disable_non_proxied_udp");
        return sb.ToString();
    }

    public static async Task<(bool ok, int ms)> TestProxy(string host, int port)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            using var tcp = new TcpClient();
            using var cts = new CancellationTokenSource(2000);
            await tcp.ConnectAsync(host, port, cts.Token);
            return (true, (int)sw.ElapsedMilliseconds);
        }
        catch { return (false, 0); }
    }
}
