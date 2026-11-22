using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;

namespace LiteBrowser;

/// <summary>
/// Синхронизация через свой сервер по WebDAV: Nextcloud/ownCloud, Яндекс Диск (webdav.yandex.ru), NAS (Synology и др.).
/// На сервер уходит тот же один файл, зашифрованный паролем синхронизации, — сервер содержимое не видит.
/// Логин и пароль сервера хранятся только на этом компьютере (DPAPI вашей учётной записи Windows).
/// </summary>
static class WebDav
{
    static string KeyFile => Path.Combine(AppEnv.DataDir, "webdav.key");
    public const string FileName = "LiteBrowser-sync.lbsync";

    public static bool IsUrl(string? folder) => folder != null && (folder.StartsWith("https://", StringComparison.OrdinalIgnoreCase) || folder.StartsWith("http://", StringComparison.OrdinalIgnoreCase));

    /// <summary>Только https; http — лишь для своего компьютера/локальной сети (пароль сервера иначе уйдёт открытым текстом).</summary>
    public static bool Allowed(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var u) && (u.Scheme == "https" || (u.Scheme == "http" && (u.IsLoopback || IsPrivate(u.Host))));

    static bool IsPrivate(string host) =>
        IPAddress.TryParse(host, out var ip) && ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork &&
        ip.GetAddressBytes() is var b && (b[0] == 10 || (b[0] == 192 && b[1] == 168) || (b[0] == 172 && b[1] >= 16 && b[1] <= 31));

    public static void SaveCredentials(string user, string pass) =>
        File.WriteAllBytes(KeyFile, ProtectedData.Protect(Encoding.UTF8.GetBytes(user + "\n" + pass), null, DataProtectionScope.CurrentUser));

    public static void Forget() { try { File.Delete(KeyFile); } catch { } }

    static (string user, string pass)? Credentials()
    {
        try
        {
            if (!File.Exists(KeyFile)) return null;
            var s = Encoding.UTF8.GetString(ProtectedData.Unprotect(File.ReadAllBytes(KeyFile), null, DataProtectionScope.CurrentUser));
            int i = s.IndexOf('\n');
            return i < 0 ? null : (s[..i], s[(i + 1)..]);
        }
        catch { return null; }
    }

    static HttpClient Client((string user, string pass)? cred = null)
    {
        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
        var c = cred ?? Credentials();
        if (c is { } x) http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes(x.user + ":" + x.pass)));
        http.DefaultRequestHeaders.UserAgent.ParseAdd("LiteBrowser/" + AppEnv.Version);
        return http;
    }

    static string Folder(string url) => url.TrimEnd('/') + "/";

    /// <summary>Проверить адрес и логин: создаём папку (если её нет) и смотрим, что доступ есть.</summary>
    public static async Task TestAsync(string url, string user, string pass)
    {
        using var http = Client((user, pass));
        await EnsureFolder(http, url);
        using var req = new HttpRequestMessage(new HttpMethod("PROPFIND"), Folder(url));
        req.Headers.Add("Depth", "0");
        using var resp = await http.SendAsync(req);
        if (resp.StatusCode == HttpStatusCode.Unauthorized) throw new UnauthorizedAccessException(L.T("Сервер не принял логин или пароль (для Яндекс Диска нужен пароль приложения)"));
        if (!resp.IsSuccessStatusCode && (int)resp.StatusCode != 207) throw new IOException(L.T("Сервер ответил: ") + (int)resp.StatusCode + " " + resp.ReasonPhrase);
    }

    static async Task EnsureFolder(HttpClient http, string url)
    {
        using var req = new HttpRequestMessage(new HttpMethod("MKCOL"), Folder(url));
        using var resp = await http.SendAsync(req);
        // 201 — создана, 405 — уже есть; остальное покажет следующий запрос
    }

    /// <summary>Скачать файл синхронизации в локальный файл. false — на сервере его ещё нет.</summary>
    public static async Task<bool> DownloadAsync(string url, string localFile)
    {
        using var http = Client();
        using var resp = await http.GetAsync(Folder(url) + FileName);
        if (resp.StatusCode == HttpStatusCode.NotFound) return false;
        if (resp.StatusCode == HttpStatusCode.Unauthorized) throw new UnauthorizedAccessException(L.T("Сервер не принял логин или пароль"));
        resp.EnsureSuccessStatusCode();
        await File.WriteAllBytesAsync(localFile, await resp.Content.ReadAsByteArrayAsync());
        return true;
    }

    public static async Task UploadAsync(string url, string localFile)
    {
        using var http = Client();
        await EnsureFolder(http, url);
        using var content = new ByteArrayContent(await File.ReadAllBytesAsync(localFile));
        content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        using var resp = await http.PutAsync(Folder(url) + FileName, content);
        if (resp.StatusCode == HttpStatusCode.Unauthorized) throw new UnauthorizedAccessException(L.T("Сервер не принял логин или пароль"));
        resp.EnsureSuccessStatusCode();
    }
}
