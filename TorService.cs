using System.Diagnostics;
using System.Formats.Tar;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace LiteBrowser;

/// <summary>
/// Tor одной кнопкой: скачивает официальный Tor Expert Bundle с dist.torproject.org (с проверкой SHA-256),
/// запускает tor.exe локально и даёт браузеру SOCKS5-прокси 127.0.0.1:9155. При блокировке Tor — мосты (obfs4 / snowflake).
/// </summary>
static class TorService
{
    public const int SocksPort = 9155;
    static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(5) };
    static Process? proc;
    public static string Dir => Path.Combine(AppEnv.DataDir, "tor");
    static string TorExe => Path.Combine(Dir, "tor", "tor.exe");
    public static bool Installed => File.Exists(TorExe);
    public static bool Running => proc is { HasExited: false };
    public static int Progress { get; private set; }          // 0..100 — загрузка Tor-сети
    public static string Status { get; private set; } = "";
    public static bool Busy { get; private set; }

    /// <summary>Скачать Tor (последняя стабильная версия со сборкой для Windows).</summary>
    public static async Task InstallAsync(CancellationToken ct = default)
    {
        if (Installed) return;
        Status = "Ищу последнюю версию Tor…";
        var index = await Http.GetStringAsync("https://dist.torproject.org/torbrowser/", ct);
        var versions = Regex.Matches(index, @"href=""(\d+\.\d+(?:\.\d+)?)/""").Select(m => m.Groups[1].Value)
            .Distinct().OrderByDescending(v => Version.Parse(v.Count(c => c == '.') == 1 ? v + ".0" : v)).ToList();
        foreach (var v in versions)
        {
            string sums;
            try { sums = await Http.GetStringAsync($"https://dist.torproject.org/torbrowser/{v}/sha256sums-signed-build.txt", ct); }
            catch { continue; }
            var name = $"tor-expert-bundle-windows-x86_64-{v}.tar.gz";
            var line = sums.Split('\n').FirstOrDefault(l => l.TrimEnd().EndsWith(name));
            if (line == null) continue;
            var expected = line.Split(' ', StringSplitOptions.RemoveEmptyEntries)[0].ToLowerInvariant();
            Status = $"Скачиваю Tor {v}…";
            var bytes = await Http.GetByteArrayAsync($"https://dist.torproject.org/torbrowser/{v}/{name}", ct);
            var actual = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
            if (actual != expected) throw new InvalidOperationException("Контрольная сумма Tor не совпала — файл повреждён или подменён");
            Status = "Распаковываю…";
            if (Directory.Exists(Dir)) Directory.Delete(Dir, true);
            Directory.CreateDirectory(Dir);
            using var gz = new GZipStream(new MemoryStream(bytes), CompressionMode.Decompress);
            await TarFile.ExtractToDirectoryAsync(gz, Dir, overwriteFiles: true, ct);
            if (!Installed) throw new InvalidOperationException("В архиве Tor нет tor.exe");
            Status = "Tor скачан";
            return;
        }
        throw new InvalidOperationException("Не нашлась сборка Tor для Windows");
    }

    /// <summary>Строки мостов и транспорта из pt_config.json (идёт в комплекте с Tor).</summary>
    static IEnumerable<string> BridgeConfig(string kind)
    {
        var ptDir = Path.Combine(Dir, "tor", "pluggable_transports");
        var cfgPath = Path.Combine(ptDir, "pt_config.json");
        if (!File.Exists(cfgPath)) yield break;
        using var d = JsonDocument.Parse(File.ReadAllText(cfgPath));
        var transport = kind == "snowflake" ? "snowflake" : "lyrebird";
        if (d.RootElement.GetProperty("pluggableTransports").TryGetProperty(transport, out var t))
            yield return t.GetString()!.Replace("${pt_path}", ShortPath(ptDir) + Path.DirectorySeparatorChar);
        if (d.RootElement.GetProperty("bridges").TryGetProperty(kind, out var list))
            foreach (var b in list.EnumerateArray()) yield return "Bridge " + b.GetString();
        yield return "UseBridges 1";
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    static extern uint GetShortPathName(string longPath, StringBuilder shortPath, uint size);

    /// <summary>Короткий путь Windows (без пробелов) — в строке транспорта Tor пробелы ломают разбор.</summary>
    static string ShortPath(string path)
    {
        var sb = new StringBuilder(1024);
        return GetShortPathName(path, sb, (uint)sb.Capacity) > 0 ? sb.ToString() : path;
    }

    /// <summary>Запустить Tor. bridges: "" — без мостов, "obfs4" или "snowflake" — если Tor в стране заблокирован.</summary>
    public static async Task StartAsync(string bridges, CancellationToken ct = default) => await StartAsync(bridges, autoBridges: bridges.Length == 0, ct);

    /// <summary>autoBridges: если без мостов не подключилось за минуту — сам переключается на snowflake (он проходит почти везде).</summary>
    public static async Task StartAsync(string bridges, bool autoBridges, CancellationToken ct = default)
    {
        if (Running) return;
        Busy = true;
        try
        {
            await InstallAsync(ct);
            var data = Path.Combine(Dir, "state");
            Directory.CreateDirectory(data);
            var torrc = new StringBuilder();
            torrc.AppendLine($"SocksPort 127.0.0.1:{SocksPort}");
            // Пути без кавычек и пробелов: в кавычках Tor считает «\» началом спецсимвола
            torrc.AppendLine($"DataDirectory {ShortPath(data)}");
            torrc.AppendLine($"GeoIPFile {ShortPath(Path.Combine(Dir, "data", "geoip"))}");
            torrc.AppendLine($"GeoIPv6File {ShortPath(Path.Combine(Dir, "data", "geoip6"))}");
            torrc.AppendLine("ClientOnly 1");
            torrc.AppendLine("AvoidDiskWrites 1");
            if (bridges is "obfs4" or "snowflake") foreach (var l in BridgeConfig(bridges)) torrc.AppendLine(l);
            var rc = Path.Combine(Dir, "torrc");
            await File.WriteAllTextAsync(rc, torrc.ToString(), ct);
            Progress = 0;
            Status = "Подключаюсь к сети Tor…";
            var done = new TaskCompletionSource<bool>();
            proc = new Process
            {
                StartInfo = new ProcessStartInfo(TorExe, $"-f \"{rc}\"")
                {
                    UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
                    WorkingDirectory = Path.GetDirectoryName(TorExe)!,
                },
                EnableRaisingEvents = true,
            };
            proc.OutputDataReceived += (_, e) =>
            {
                if (e.Data == null) return;
                AppEnv.Log("tor: " + e.Data);
                var m = Regex.Match(e.Data, @"Bootstrapped (\d+)%(?:\s*\(\w+\))?:?\s*(.*)");
                if (m.Success)
                {
                    Progress = int.Parse(m.Groups[1].Value);
                    Status = Progress >= 100 ? "Tor подключён" : $"Подключаюсь к сети Tor… {Progress}%";
                    if (Progress >= 100) done.TrySetResult(true);
                }
                else if (e.Data.Contains("[err]")) Status = "Ошибка Tor: " + e.Data[(e.Data.IndexOf("[err]") + 5)..].Trim();
            };
            proc.Exited += (_, _) => { done.TrySetResult(false); if (Progress < 100) Status = "Tor остановился"; };
            proc.Start();
            proc.BeginOutputReadLine();
            proc.BeginErrorReadLine();
            // Ждём подключения (через мосты бывает долго)
            var finished = await Task.WhenAny(done.Task, Task.Delay(autoBridges ? TimeSpan.FromSeconds(60) : TimeSpan.FromMinutes(3), ct));
            if (autoBridges && (finished != done.Task || !done.Task.Result) && Progress < 100)
            {
                Stop();
                Status = "Без мостов не вышло — подключаюсь через snowflake…";
                Busy = false;
                await StartAsync("snowflake", autoBridges: false, ct);
                return;
            }
            if (finished != done.Task || !done.Task.Result)
            {
                if (Progress < 100) Status = bridges.Length == 0
                    ? "Tor не подключился — похоже, он заблокирован. Включите мосты"
                    : "Tor не подключился даже через мосты — попробуйте другой тип моста";
            }
        }
        finally { Busy = false; }
    }

    public static void Stop()
    {
        try { if (Running) { proc!.Kill(true); proc.WaitForExit(3000); } } catch { }
        proc = null;
        Progress = 0;
        Status = "Tor выключен";
    }
}
