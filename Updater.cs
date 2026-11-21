using System.Runtime.InteropServices;
using System.Text.Json;

namespace LiteBrowser;

sealed record ReleaseInfo(Version Version, string Tag, string Notes, string PageUrl, string AssetUrl, long Size);

/// <summary>Автообновление: проверяет последний релиз на GitHub и меняет exe на новый.</summary>
static class Updater
{
    public const string Repo = "sasiskax27/LiteBrowser";
    public static Version Current => Version.Parse(AppEnv.Version.Count(c => c == '.') == 0 ? AppEnv.Version + ".0" : AppEnv.Version);

    /// <summary>Полная версия (без .NET) или маленькая — чтобы скачать такую же.</summary>
    public static bool IsSelfContained => !RuntimeEnvironment.GetRuntimeDirectory().Contains(@"\dotnet\shared\", StringComparison.OrdinalIgnoreCase);

    static HttpClient Http()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("LiteBrowser-Updater/" + AppEnv.Version);
        http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return http;
    }

    public static async Task<ReleaseInfo?> CheckAsync()
    {
        using var http = Http();
        var json = await http.GetStringAsync($"https://api.github.com/repos/{Repo}/releases/latest");
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var tag = root.GetProperty("tag_name").GetString() ?? "";
        if (!Version.TryParse(tag.TrimStart('v', 'V'), out var v) || v <= Current) return null;
        string want = IsSelfContained ? "-full" : "-small";
        foreach (var a in root.GetProperty("assets").EnumerateArray())
        {
            var name = a.GetProperty("name").GetString() ?? "";
            var url = a.GetProperty("browser_download_url").GetString() ?? "";
            if (!name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || !name.Contains(want, StringComparison.OrdinalIgnoreCase)) continue;
            // Скачиваем только из релизов этого репозитория
            if (!url.StartsWith($"https://github.com/{Repo}/releases/download/", StringComparison.OrdinalIgnoreCase)) continue;
            return new ReleaseInfo(v, tag, root.TryGetProperty("body", out var b) ? b.GetString() ?? "" : "",
                root.GetProperty("html_url").GetString() ?? "", url, a.GetProperty("size").GetInt64());
        }
        return null;
    }

    /// <summary>Скачивает новую версию рядом с текущей и подменяет exe. После этого нужен перезапуск.</summary>
    public static async Task InstallAsync(ReleaseInfo r, IProgress<double>? progress)
    {
        var exe = Environment.ProcessPath!;
        var dir = Path.GetDirectoryName(exe)!;
        var tmp = Path.Combine(dir, "LiteBrowser.new");
        using var http = Http();
        http.Timeout = TimeSpan.FromMinutes(10);
        using (var resp = await http.GetAsync(r.AssetUrl, HttpCompletionOption.ResponseHeadersRead))
        {
            resp.EnsureSuccessStatusCode();
            if (resp.RequestMessage?.RequestUri?.Scheme != "https") throw new InvalidOperationException("Загрузка не по HTTPS — отменено");
            await using var src = await resp.Content.ReadAsStreamAsync();
            await using var dst = File.Create(tmp);
            var buf = new byte[81920];
            long got = 0;
            int n;
            while ((n = await src.ReadAsync(buf)) > 0)
            {
                await dst.WriteAsync(buf.AsMemory(0, n));
                got += n;
                if (r.Size > 0) progress?.Report((double)got / r.Size);
            }
        }
        var fi = new FileInfo(tmp);
        using (var fs = fi.OpenRead())
        {
            if (fs.ReadByte() != 'M' || fs.ReadByte() != 'Z') { fs.Close(); File.Delete(tmp); throw new InvalidDataException("Скачанный файл — не программа"); }
        }
        if (r.Size > 0 && fi.Length != r.Size) { File.Delete(tmp); throw new InvalidDataException("Файл скачался не полностью"); }
        var old = Path.ChangeExtension(exe, ".old");
        if (File.Exists(old)) File.Delete(old);
        File.Move(exe, old);       // работающий exe можно переименовать
        File.Move(tmp, exe);
    }
}
