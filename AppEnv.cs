using System.Net.Sockets;
using System.Text;
using Microsoft.Web.WebView2.Core;

namespace LiteBrowser;

/// <summary>РћР±С‰РёРµ РґР»СЏ РІСЃРµС… РѕРєРѕРЅ: РїР°РїРєР° РґР°РЅРЅС‹С…, РЅР°СЃС‚СЂРѕР№РєРё Рё РґРІРёР¶РѕРє WebView2.</summary>
static class AppEnv
{
    public const string Version = "3.4.1";
    /// <summary>РџР°РїРєР° РґР°РЅРЅС‹С…. LITEBROWSER_DATA вЂ” РѕС‚РґРµР»СЊРЅР°СЏ РїР°РїРєР° РґР»СЏ С‚РµСЃС‚РѕРІ (С‚РѕРіРґР° Рё РѕС‚РґРµР»СЊРЅС‹Р№ СЌРєР·РµРјРїР»СЏСЂ Р±СЂР°СѓР·РµСЂР°).</summary>
    public static readonly string DataDir = Environment.GetEnvironmentVariable("LITEBROWSER_DATA") is { Length: > 0 } d ? d
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LiteBrowser");
    public static string InstanceName => "LiteBrowser-" + Environment.UserName + (Environment.GetEnvironmentVariable("LITEBROWSER_DATA") is { Length: > 0 } d2 ? "-" + Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(Encoding.UTF8.GetBytes(d2)))[..8] : "");

    /// <summary>Р–СѓСЂРЅР°Р» РґР»СЏ РѕС‚Р»Р°РґРєРё вЂ” РїРёС€РµС‚СЃСЏ С‚РѕР»СЊРєРѕ РµСЃР»Рё Р·Р°РґР°РЅР° РїРµСЂРµРјРµРЅРЅР°СЏ LITEBROWSER_DEBUG.</summary>
    public static void Log(string s)
    {
        if (Environment.GetEnvironmentVariable("LITEBROWSER_DEBUG") is not { Length: > 0 }) return;
        try { File.AppendAllText(Path.Combine(DataDir, "debug.log"), $"{DateTime.Now:HH:mm:ss.fff} {s}\r\n"); } catch { }
    }

    public static AppSettings Settings { get; private set; } = new();
    /// <summary>РЎ РєР°РєРёРјРё РЅР°СЃС‚СЂРѕР№РєР°РјРё РїСЂРѕРєСЃРё СЂРµР°Р»СЊРЅРѕ Р·Р°РїСѓС‰РµРЅ РґРІРёР¶РѕРє.</summary>
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
        // РџРѕСЃР»Рµ РїРµСЂРµР·Р°РїСѓСЃРєР° СЃС‚Р°СЂС‹Р№ РїСЂРѕС†РµСЃСЃ РґРІРёР¶РєР° РјРѕР¶РµС‚ РµС‰С‘ Р·Р°РєСЂС‹РІР°С‚СЊСЃСЏ вЂ” РїСЂРѕР±СѓРµРј РЅРµСЃРєРѕР»СЊРєРѕ СЂР°Р·.
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
            // РўРѕР»СЊРєРѕ РІС‹Р±СЂР°РЅРЅС‹Рµ СЃР°Р№С‚С‹ вЂ” С‡РµСЂРµР· PAC-СЃРєСЂРёРїС‚, РѕСЃС‚Р°Р»СЊРЅС‹Рµ РЅР°РїСЂСЏРјСѓСЋ
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
        // РќРµ РґР°С‘Рј WebRTC СЂР°СЃРєСЂС‹С‚СЊ РЅР°СЃС‚РѕСЏС‰РёР№ IP РІ РѕР±С…РѕРґ РїСЂРѕРєСЃРё
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
