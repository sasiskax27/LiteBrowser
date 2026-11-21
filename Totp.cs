using System.Security.Cryptography;
using System.Text;
using System.Web;

namespace LiteBrowser;

/// <summary>Одноразовые коды двухфакторной защиты (TOTP, RFC 6238) — как в Google Authenticator.</summary>
static class Totp
{
    public sealed record Spec(byte[] Key, int Digits, int Period, string Algorithm);

    public static Spec? Parse(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var s = value.Trim();
        string? secret;
        int digits = 6, period = 30;
        string alg = "SHA1";
        if (s.StartsWith("otpauth://", StringComparison.OrdinalIgnoreCase))
        {
            if (!Uri.TryCreate(s, UriKind.Absolute, out var u)) return null;
            var q = HttpUtility.ParseQueryString(u.Query);
            secret = q["secret"];
            if (int.TryParse(q["digits"], out var d) && d is 6 or 7 or 8) digits = d;
            if (int.TryParse(q["period"], out var p) && p is >= 10 and <= 120) period = p;
            var a = (q["algorithm"] ?? "SHA1").ToUpperInvariant();
            if (a is "SHA256" or "SHA512") alg = a;
        }
        else secret = s;
        var key = Base32(secret ?? "");
        return key is { Length: >= 5 } ? new Spec(key, digits, period, alg) : null;
    }

    public static byte[]? Base32(string s)
    {
        s = s.Replace(" ", "").Replace("-", "").TrimEnd('=').ToUpperInvariant();
        const string abc = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
        var bytes = new List<byte>();
        int buffer = 0, bits = 0;
        foreach (var c in s)
        {
            int v = abc.IndexOf(c);
            if (v < 0) return null;
            buffer = (buffer << 5) | v;
            bits += 5;
            if (bits >= 8) { bytes.Add((byte)(buffer >> (bits - 8))); bits -= 8; }
        }
        return bytes.ToArray();
    }

    public static string? Code(string? value, out int remaining) => CodeAt(value, DateTimeOffset.UtcNow.ToUnixTimeSeconds(), out remaining);

    public static string? CodeAt(string? value, long unix, out int remaining)
    {
        remaining = 0;
        var spec = Parse(value);
        if (spec == null) return null;
        long counter = unix / spec.Period;
        remaining = (int)(spec.Period - unix % spec.Period);
        var msg = BitConverter.GetBytes(counter);
        if (BitConverter.IsLittleEndian) Array.Reverse(msg);
        byte[] hash = spec.Algorithm switch
        {
            "SHA256" => HMACSHA256.HashData(spec.Key, msg),
            "SHA512" => HMACSHA512.HashData(spec.Key, msg),
            _ => HMACSHA1.HashData(spec.Key, msg),
        };
        int o = hash[^1] & 0x0F;
        int bin = ((hash[o] & 0x7F) << 24) | (hash[o + 1] << 16) | (hash[o + 2] << 8) | hash[o + 3];
        int mod = (int)Math.Pow(10, spec.Digits);
        return (bin % mod).ToString().PadLeft(spec.Digits, '0');
    }

    /// <summary>Скрипт: вставить код в поле одноразового кода на странице.</summary>
    public static string FillScript(string code) => $$"""
(() => {
  const visible = el => !!(el.offsetWidth || el.offsetHeight || el.getClientRects().length);
  const inputs = [...document.querySelectorAll('input')].filter(i => visible(i) && !i.disabled && !/hidden|password|checkbox|radio|submit/.test(i.type));
  let f = inputs.find(i => (i.autocomplete || '').includes('one-time-code'))
       || inputs.find(i => /otp|totp|2fa|mfa|code|token|pin|verification/i.test((i.name || '') + (i.id || '') + (i.placeholder || '')));
  if (!f && document.activeElement && document.activeElement.tagName === 'INPUT') f = document.activeElement;
  if (!f) return 'none';
  f.focus();
  Object.getOwnPropertyDescriptor(HTMLInputElement.prototype, 'value').set.call(f, {{System.Text.Json.JsonSerializer.Serialize(code)}});
  f.dispatchEvent(new Event('input', { bubbles: true }));
  f.dispatchEvent(new Event('change', { bubbles: true }));
  return 'ok';
})()
""";
}

/// <summary>
/// Проверка утечек через Have I Been Pwned (k-анонимность): отправляются только первые 5 символов SHA-1 хеша пароля,
/// сам пароль и полный хеш никуда не уходят.
/// </summary>
static class BreachCheck
{
    public static async Task<Dictionary<string, int>> CheckAsync(IEnumerable<PasswordEntry> entries)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("LiteBrowser-PasswordCheck");
        http.DefaultRequestHeaders.Add("Add-Padding", "true");
        var cache = new Dictionary<string, Dictionary<string, int>>();
        var result = new Dictionary<string, int>();
        foreach (var e in entries)
        {
            if (e.Password.Length == 0) continue;
            var hash = Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(e.Password)));
            var prefix = hash[..5];
            if (!cache.TryGetValue(prefix, out var map))
            {
                map = new Dictionary<string, int>();
                var text = await http.GetStringAsync("https://api.pwnedpasswords.com/range/" + prefix);
                foreach (var line in text.Split('\n'))
                {
                    var parts = line.Trim().Split(':');
                    if (parts.Length == 2 && int.TryParse(parts[1], out var n) && n > 0) map[parts[0]] = n;
                }
                cache[prefix] = map;
            }
            if (map.TryGetValue(hash[5..], out var count)) result[e.Id] = count;
        }
        return result;
    }
}
