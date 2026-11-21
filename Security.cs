using System.Collections.Concurrent;
using System.Security.Cryptography;

namespace LiteBrowser;

/// <summary>Счётчики защиты за текущий сеанс (для Центра безопасности).</summary>
static class SecurityStats
{
    public static int BlockedRequests, PopupsBlocked, HttpsUpgrades, DangerousBlocked, ParentalBlocked, PermissionsDenied;
    public static readonly ConcurrentDictionary<string, int> BlockedHosts = new(StringComparer.OrdinalIgnoreCase);
    public static readonly DateTime Since = DateTime.Now;

    public static void Blocked(string host)
    {
        Interlocked.Increment(ref BlockedRequests);
        if (BlockedHosts.Count < 5000) BlockedHosts.AddOrUpdate(host, 1, (_, v) => v + 1);
    }
}

/// <summary>Защита загрузок: тип файла, SHA-256, предупреждение об исполняемых файлах.</summary>
static class DownloadGuard
{
    static readonly HashSet<string> Executable = new(StringComparer.OrdinalIgnoreCase)
    {
        ".exe", ".msi", ".msix", ".appx", ".bat", ".cmd", ".com", ".scr", ".pif", ".ps1", ".vbs", ".vbe", ".js", ".jse", ".wsf", ".wsh",
        ".hta", ".jar", ".lnk", ".reg", ".dll", ".cpl", ".msc", ".iso", ".img", ".vhd", ".apk",
    };
    static readonly HashSet<string> Archives = new(StringComparer.OrdinalIgnoreCase) { ".zip", ".rar", ".7z", ".tar", ".gz", ".cab" };

    public static string Risk(string path)
    {
        var ext = Path.GetExtension(path);
        if (Executable.Contains(ext)) return "exec";
        if (Archives.Contains(ext)) return "archive";
        if (ext.Equals(".docm", StringComparison.OrdinalIgnoreCase) || ext.Equals(".xlsm", StringComparison.OrdinalIgnoreCase)) return "macro";
        return "";
    }

    public static string RiskText(string risk) => risk switch
    {
        "exec" => "Программа или скрипт — запускайте, только если доверяете источнику",
        "archive" => "Архив — внутри могут быть программы, проверяйте содержимое",
        "macro" => "Документ с макросами — не включайте макросы без необходимости",
        _ => "",
    };

    /// <summary>Метка «скачано из интернета» (Zone.Identifier): Windows SmartScreen и Office проверят файл при запуске.</summary>
    public static void MarkOfTheWeb(string path, string url)
    {
        try
        {
            var safe = Uri.TryCreate(url, UriKind.Absolute, out var u) && u.Scheme is "http" or "https" ? u.GetLeftPart(UriPartial.Path) : "";
            File.WriteAllText(path + ":Zone.Identifier", "[ZoneTransfer]\r\nZoneId=3\r\n" + (safe.Length > 0 ? "HostUrl=" + safe + "\r\n" : ""));
        }
        catch { }
    }

    public static async Task<string> Sha256Async(string path)
    {
        await using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1 << 20, true);
        return Convert.ToHexString(await SHA256.HashDataAsync(fs)).ToLowerInvariant();
    }
}

sealed record AuditItem(string Level, string Title, string Text, string? Action = null, string? ActionLabel = null);
