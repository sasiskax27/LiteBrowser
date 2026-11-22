using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace LiteBrowser;

/// <summary>
/// Перенос паролей из браузеров на Chromium (Edge, Chrome, Brave, Opera, Vivaldi) прямо с этого компьютера.
/// Пароли там зашифрованы ключом, который защищён вашей учётной записью Windows (DPAPI), — расшифровать их можно
/// только под вашим входом в Windows. Новые версии Chrome (127+) дополнительно «привязывают» пароли к самому Chrome (v20) —
/// такие записи прочитать нельзя, для них браузер подскажет экспорт в CSV. Яндекс Браузер шифрует пароли по-своему — тоже через CSV.
/// </summary>
static class PasswordImport
{
    public sealed record Result(List<(string origin, string username, string password, string totp)> Items, int Locked, string Note);

    public static bool Supported(BrowserProfile p) => !p.Firefox && p.Browser != "Яндекс Браузер" && File.Exists(Path.Combine(p.Dir, "Login Data"));

    public static Result Read(BrowserProfile p)
    {
        var items = new List<(string, string, string, string)>();
        if (!Supported(p)) return new(items, 0, L.T("Из этого браузера пароли переносятся через CSV-файл"));
        var key = MasterKey(p.Dir);
        int locked = 0;
        Importer.WithDb(Path.Combine(p.Dir, "Login Data"), cmd =>
        {
            cmd.CommandText = "SELECT origin_url, username_value, password_value FROM logins WHERE blacklisted_by_user = 0";
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                var url = r.IsDBNull(0) ? "" : r.GetString(0);
                var user = r.IsDBNull(1) ? "" : r.GetString(1);
                if (r.IsDBNull(2)) continue;
                var blob = (byte[])r.GetValue(2);
                var origin = PasswordTools.Origin(url);
                if (origin == null || blob.Length == 0) continue;
                var pass = Decrypt(blob, key, out bool appBound);
                if (appBound) { locked++; continue; }
                if (pass is { Length: > 0 }) items.Add((origin, user, pass, ""));
            }
        });
        var note = locked > 0 ? string.Format(L.T("{0} паролей защищены самим браузером (Chrome 127+) — их перенесите через «Экспорт паролей» в CSV"), locked) : "";
        return new(items, locked, note);
    }

    /// <summary>Ключ AES из файла Local State (зашифрован DPAPI текущего пользователя Windows).</summary>
    static byte[]? MasterKey(string profileDir)
    {
        foreach (var dir in new[] { profileDir, Path.GetDirectoryName(profileDir) ?? profileDir })
        {
            var ls = Path.Combine(dir, "Local State");
            if (!File.Exists(ls)) continue;
            try
            {
                using var doc = JsonDocument.Parse(Importer.ReadShared(ls));
                var b64 = doc.RootElement.GetProperty("os_crypt").GetProperty("encrypted_key").GetString();
                if (b64 == null) return null;
                var enc = Convert.FromBase64String(b64);
                if (enc.Length < 6 || Encoding.ASCII.GetString(enc, 0, 5) != "DPAPI") return null;
                return ProtectedData.Unprotect(enc[5..], null, DataProtectionScope.CurrentUser);
            }
            catch { return null; }
        }
        return null;
    }

    /// <summary>v10/v11 — AES-256-GCM (12 байт nonce, в конце 16 байт тега); старые записи — DPAPI напрямую; v20 — привязано к Chrome.</summary>
    internal static string? Decrypt(byte[] blob, byte[]? key, out bool appBound)
    {
        appBound = false;
        try
        {
            if (blob.Length > 3 && blob[0] == 'v' && blob[1] == '2' && blob[2] == '0') { appBound = true; return null; }
            if (blob.Length > 3 + 12 + 16 && blob[0] == 'v' && blob[1] == '1' && (blob[2] == '0' || blob[2] == '1'))
            {
                if (key == null) return null;
                var nonce = blob.AsSpan(3, 12);
                var cipher = blob.AsSpan(15, blob.Length - 15 - 16);
                var tag = blob.AsSpan(blob.Length - 16);
                var plain = new byte[cipher.Length];
                using var gcm = new AesGcm(key, 16);
                gcm.Decrypt(nonce, cipher, tag, plain);
                return Encoding.UTF8.GetString(plain);
            }
            return Encoding.UTF8.GetString(ProtectedData.Unprotect(blob, null, DataProtectionScope.CurrentUser));
        }
        catch { return null; }
    }
}
