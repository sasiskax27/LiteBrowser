using System.Text.Json;
using Microsoft.Web.WebView2.Core;

namespace LiteBrowser;

/// <summary>Центр безопасности: статистика защиты за сеанс, карта трекеров и проверка настроек.</summary>
sealed partial class MainForm
{
    /// <summary>Кому принадлежат известные трекеры (для карты трекеров).</summary>
    static readonly (string part, string owner)[] TrackerOwners =
    {
        ("google-analytics", "Google"), ("googletagmanager", "Google"), ("doubleclick", "Google"), ("googlesyndication", "Google"),
        ("googleadservices", "Google"), ("adservice.google", "Google"), ("app-measurement", "Google"),
        ("mc.yandex", "Яндекс"), ("an.yandex", "Яндекс"), ("yandex.ru/ads", "Яндекс"), ("adfox", "Яндекс"), ("yabs.yandex", "Яндекс"),
        ("facebook", "Meta"), ("fbcdn", "Meta"), ("connect.facebook", "Meta"), ("instagram", "Meta"),
        ("top-fwz1.mail.ru", "VK"), ("ad.mail.ru", "VK"), ("vk.com/rtrg", "VK"), ("tns-counter", "Mediascope"),
        ("tiktok", "TikTok"), ("bytedance", "TikTok"), ("criteo", "Criteo"), ("amazon-adsystem", "Amazon"),
        ("scorecardresearch", "Comscore"), ("hotjar", "Hotjar"), ("clarity.ms", "Microsoft"), ("bing.com", "Microsoft"),
        ("adnxs", "Microsoft Xandr"), ("taboola", "Taboola"), ("outbrain", "Outbrain"), ("twitter", "X"), ("ads-twitter", "X"),
        ("liveinternet", "LiveInternet"), ("rambler", "Rambler"), ("top100", "Rambler"), ("pubmatic", "PubMatic"), ("rubiconproject", "Magnite"),
    };

    static string TrackerOwner(string host)
    {
        foreach (var (part, owner) in TrackerOwners)
            if (host.Contains(part, StringComparison.OrdinalIgnoreCase)) return owner;
        return "";
    }

    static readonly string[] RiskyExtPermissions = { "<all_urls>", "*://*/*", "http://*/*", "https://*/*", "webRequest", "cookies", "history", "tabs", "debugger", "proxy", "nativeMessaging", "clipboardRead" };

    object SecurityReport()
    {
        var items = new List<AuditItem>();
        void Add(string level, string title, string text, string? action = null, string? label = null) => items.Add(new AuditItem(level, title, text, action, label));

        // Соединение
        if (Cfg.HttpsOnly) Add("ok", "Только HTTPS", "Сайты открываются по защищённому соединению");
        else Add("bad", "HTTPS не обязателен", "Данные на сайтах без HTTPS могут перехватить в публичной сети Wi-Fi", "https", "Включить");
        if (httpAllowed.Count > 0) Add("warn", $"Исключения HTTPS: {httpAllowed.Count}", "Вы разрешили открывать без шифрования: " + string.Join(", ", httpAllowed.Take(5)), "clearHttp", "Сбросить");

        // Блокировка
        if (!Cfg.BlockAds) Add("bad", "Блокировка рекламы и трекеров выключена", "Сайты могут следить за вами и показывать навязчивую рекламу", "adblock", "Включить");
        else
        {
            Add("ok", "Блокировка рекламы и трекеров", $"Правил в фильтрах: {AdBlock.PatternRules:N0}, опасных доменов: {Blocklist.ExtendedCount:N0}");
            if (!Cfg.ListEasyPrivacy) Add("warn", "Защита от слежки (EasyPrivacy) выключена", "Счётчики и аналитика сайтов не блокируются", "privacy", "Включить");
            if (!Cfg.BlockPopups) Add("warn", "Всплывающие окна разрешены", "Сайты могут открывать окна без вашего клика", "popups", "Блокировать");
            if (!Cfg.ExtendedBlocklist) Add("warn", "Список опасных сайтов выключен", "Не будет предупреждения о мошеннических и вредоносных сайтах", "extended", "Включить");
            var oldest = AdBlock.Lists.Where(l => AdBlock.Enabled(Cfg, l.Id)).Select(l => AdBlock.Status[l.Id].Updated ?? DateTime.MinValue).DefaultIfEmpty(DateTime.Now).Min();
            if (Blocklist.Updated is { } bu && bu < oldest) oldest = bu;
            if (DateTime.Now - oldest > TimeSpan.FromDays(14))
                Add("warn", "Списки защиты устарели", oldest == DateTime.MinValue ? "Списки ещё не загружены" : $"Последнее обновление: {oldest:dd.MM.yyyy}", "updateLists", "Обновить");
        }

        // Пароли
        if (!Cfg.PasswordManager) Add("info", "Менеджер паролей выключен", "Используйте уникальные пароли — менеджер их запомнит", "open:settings.html#passwords", "Настроить");
        else if (!Vault.Exists) Add("info", "Хранилище паролей не создано", "Создайте его, чтобы браузер помогал с надёжными паролями", "open:passwords.html", "Создать");
        else if (!Vault.Unlocked) Add("info", "Хранилище паролей заблокировано", "Разблокируйте его, чтобы проверить слабые и повторяющиеся пароли", "open:passwords.html", "Открыть");
        else
        {
            var all = Vault.All();
            int weak = all.Count(e => e.Password.Length < 10 || e.Password.All(char.IsLetter) || e.Password.All(char.IsDigit));
            int reused = all.GroupBy(e => e.Password).Where(g => g.Count() > 1).Sum(g => g.Count());
            if (weak + reused == 0) Add("ok", "Пароли в порядке", $"Сохранено: {all.Count}, слабых и повторяющихся нет");
            else Add("warn", "Есть ненадёжные пароли", $"Слабых: {weak}, повторяющихся: {reused}. Проверьте и утечки паролей", "open:passwords.html", "Проверить");
        }
        if (Cfg.PasswordManager && Cfg.AutoLockMinutes == 0) Add("warn", "Хранилище не блокируется само", "Пока браузер открыт, пароли доступны любому за компьютером", "autolock", "Через 15 мин");

        // Разрешения сайтам
        int allowed = Cfg.SitePermissions.Sum(s => s.Value.Count(p => p.Value == "allow"));
        if (allowed > 0) Add("info", $"Сайтам выдано разрешений: {allowed}", "Список ниже — уберите те, что больше не нужны");

        // Расширения
        var meta = Extensions.LoadMeta();
        foreach (var e in meta.Values)
        {
            var risky = e.Permissions.Where(p => RiskyExtPermissions.Contains(p)).ToList();
            if (risky.Count > 0)
                Add("warn", $"Расширение «{e.Name}» видит много", "Доступ: " + string.Join(", ", risky.Take(6)) + ". Оставьте, только если доверяете автору", "open:extensions.html", "Управлять");
        }

        // Приватность
        if (Cfg.SearchSuggestions) Add("info", "Подсказки поиска включены", "Всё, что вы набираете в адресной строке, сразу уходит поисковику", "suggestOff", "Выключить");
        if (Cfg.AiProvider == "openai" && Cfg.AiCloudConsent)
        {
            var aiHost = Uri.TryCreate(Cfg.AiBaseUrl, UriKind.Absolute, out var au) ? au.Host : Cfg.AiBaseUrl;
            Add("info", "ИИ в облаке", $"Текст страниц, о которых вы спрашиваете, отправляется на {aiHost}. Встроенный ИИ работает без интернета", "aiLocal", "Встроенный ИИ");
        }
        else if (Cfg.AiEnabled && Cfg.AiProvider == "local") Add("ok", "ИИ работает на компьютере", "Lite AI не отправляет страницы в интернет");
        if (Cfg.ProxyMode != "off" && !(Cfg.ProxyHost is "127.0.0.1" or "localhost" or "::1"))
            Add("info", "Трафик идёт через внешний прокси", $"{Cfg.ProxyHost}:{Cfg.ProxyPort} видит, какие сайты вы открываете", "open:settings.html#proxy", "Настроить");
        if (Cfg.SyncEnabled) Add("ok", "Синхронизация зашифрована", "Данные в облачной папке защищены вашим паролем (AES-256)");

        // Обновления
        if (!Cfg.AutoUpdate) Add("warn", "Проверка обновлений выключена", "Исправления безопасности не придут автоматически", "autoupdate", "Включить");
        var env = AppEnv.GetEnvironment();
        if (env.IsCompletedSuccessfully) Add("ok", "Движок Chromium " + env.Result.BrowserVersionString, "Обновляется вместе с Windows (WebView2)");

        int bad = items.Count(i => i.Level == "bad"), warn = items.Count(i => i.Level == "warn");
        int score = Math.Clamp(100 - bad * 20 - warn * 6, 0, 100);

        // Карта трекеров за сеанс
        var hosts = SecurityStats.BlockedHosts.ToArray();
        var owners = hosts.Select(h => (owner: TrackerOwner(h.Key), h.Value)).Where(x => x.owner.Length > 0)
            .GroupBy(x => x.owner).Select(g => new { owner = g.Key, count = g.Sum(x => x.Value) }).OrderByDescending(x => x.count).Take(12);

        return new
        {
            type = "secReport",
            score,
            since = SecurityStats.Since.ToString("HH:mm"),
            stats = new
            {
                blocked = SecurityStats.BlockedRequests, popups = SecurityStats.PopupsBlocked, https = SecurityStats.HttpsUpgrades,
                dangerous = SecurityStats.DangerousBlocked, parental = SecurityStats.ParentalBlocked, denied = SecurityStats.PermissionsDenied,
                total = Store.BlockedTotal, trackers = hosts.Length,
            },
            owners,
            topHosts = hosts.OrderByDescending(h => h.Value).Take(15).Select(h => new { host = h.Key, count = h.Value, owner = TrackerOwner(h.Key) }),
            tabs = tabs.Where(t => t.PageHost.Length > 0 && t.PageHost != LitePages.Host).Select(t => new { title = t.Title, host = t.PageHost, blocked = t.Blocked })
                .OrderByDescending(t => t.blocked).Take(10),
            items = items.OrderBy(i => i.Level switch { "bad" => 0, "warn" => 1, "info" => 2, _ => 3 })
                .Select(i => new { level = i.Level, title = i.Title, text = i.Text, action = i.Action, label = i.ActionLabel }),
            permissions = Cfg.SitePermissions.SelectMany(s => s.Value.Select(p => new { site = s.Key, kind = p.Key, value = p.Value }))
                .OrderBy(p => p.site).ToList(),
        };
    }

    async Task HandleSecurityMessage(CoreWebView2 core, string type, JsonElement root)
    {
        string Str(string name) => root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
        switch (type)
        {
            case "sec:report":
                Post(core, SecurityReport());
                break;
            case "sec:fix":
                switch (Str("action"))
                {
                    case "https": CopyAndSave(x => x.HttpsOnly = true); break;
                    case "adblock": CopyAndSave(x => x.BlockAds = true); break;
                    case "popups": CopyAndSave(x => x.BlockPopups = true); break;
                    case "extended": CopyAndSave(x => x.ExtendedBlocklist = true); await Blocklist.UpdateAsync(AppEnv.DataDir, Cfg, force: false); break;
                    case "privacy": CopyAndSave(x => x.ListEasyPrivacy = true); await AdBlock.ReloadAsync(Cfg); break;
                    case "autolock": CopyAndSave(x => x.AutoLockMinutes = 15); break;
                    case "autoupdate": CopyAndSave(x => x.AutoUpdate = true); break;
                    case "suggestOff": CopyAndSave(x => x.SearchSuggestions = false); break;
                    case "aiLocal": CopyAndSave(x => x.AiProvider = "local"); break;
                    case "clearHttp": httpAllowed.Clear(); break;
                    case "updateLists":
                        Post(core, new { type = "secBusy", text = "Обновляю списки…" });
                        await Task.WhenAll(AdBlock.UpdateAsync(Cfg, force: true), Blocklist.UpdateAsync(AppEnv.DataDir, Cfg, force: true));
                        break;
                }
                Post(core, SecurityReport());
                break;
            case "sec:revoke":
                var site = Str("site"); var kind = Str("kind");
                if (site.Length > 0) CopyAndSave(x => { if (kind.Length > 0) SetPerm(x, site, kind, null); else x.SitePermissions.Remove(site); });
                Post(core, SecurityReport());
                break;
            case "sec:resetStats":
                Interlocked.Exchange(ref SecurityStats.BlockedRequests, 0); Interlocked.Exchange(ref SecurityStats.PopupsBlocked, 0);
                Interlocked.Exchange(ref SecurityStats.HttpsUpgrades, 0); Interlocked.Exchange(ref SecurityStats.DangerousBlocked, 0);
                Interlocked.Exchange(ref SecurityStats.ParentalBlocked, 0); Interlocked.Exchange(ref SecurityStats.PermissionsDenied, 0);
                SecurityStats.BlockedHosts.Clear();
                Post(core, SecurityReport());
                break;
        }
    }
}
