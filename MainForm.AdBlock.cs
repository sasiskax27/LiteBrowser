using System.Text;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Text.Json;
using System.Web;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace LiteBrowser;

/// <summary>Блокировщик рекламы на страницах.</summary>
sealed partial class MainForm
{
    // ================= Блокировщик рекламы =================

    static bool SiteAllowed(string host) =>
        host.Length > 0 && (Cfg.AdblockAllow.Contains(PasswordTools.HostKey(host)) || Trusted(host));

    static bool Trusted(string host) => host.Length > 0 && Cfg.TrustedSites.Contains(PasswordTools.HostKey(host));
    static bool Untrusted(string host) => host.Length > 0 && Cfg.UntrustedSites.Contains(PasswordTools.HostKey(host));

    static readonly (CoreWebView2PermissionKind kind, string name)[] PermissionNames =
    {
        (CoreWebView2PermissionKind.Camera, "Камера"),
        (CoreWebView2PermissionKind.Microphone, "Микрофон"),
        (CoreWebView2PermissionKind.Geolocation, "Местоположение"),
        (CoreWebView2PermissionKind.ClipboardRead, "Буфер обмена"),
        (CoreWebView2PermissionKind.MultipleAutomaticDownloads, "Несколько загрузок"),
        (CoreWebView2PermissionKind.Notifications, "Уведомления"),
    };

    /// <summary>Панель сайта по клику на замок: соединение, уровень доверия, разрешения, cookies.</summary>
    void ShowSiteMenu()
    {
        var tab = active;
        var src = tab?.Core?.Source ?? "";
        if (tab?.Core == null || !src.StartsWith("http")) return;
        var core = tab.Core;
        var host = new Uri(src).Host;
        var key = PasswordTools.HostKey(host);
        bool https = src.StartsWith("https://");
        var menu = Theme.NewMenu();
        menu.Items.Add(new ToolStripMenuItem(https ? "🔒  Соединение защищено (HTTPS)" : "⚠  Соединение НЕ защищено")
        { Enabled = false, Font = Theme.GetFont("Segoe UI", 10f, FontStyle.Bold), Padding = new Padding(6, 6, 6, 1) });
        menu.Items.Add(new ToolStripMenuItem(https ? $"   {host}: данные шифруются" : $"   {host}: пароли и данные могут перехватить")
        { Enabled = false, Padding = new Padding(6, 0, 6, 4) });
        menu.Items.Add(new ToolStripSeparator());

        menu.Items.Add(new ToolStripMenuItem(L.T("Уровень доверия")) { Enabled = false, Padding = new Padding(6, 4, 6, 2) });
        int level = Trusted(host) ? 1 : Untrusted(host) ? -1 : 0;
        void SetLevel(int l)
        {
            CopyAndSave(x =>
            {
                x.TrustedSites.Remove(key);
                x.UntrustedSites.Remove(key);
                if (l == 1) x.TrustedSites.Add(key);
                if (l == -1) x.UntrustedSites.Add(key);
            });
            core.Reload();
        }
        menu.Items.Add(Theme.Item((level == 0 ? "●  " : "○  ") + "Обычный — стандартная защита", () => SetLevel(0)));
        menu.Items.Add(Theme.Item((level == 1 ? "●  " : "○  ") + "Доверенный — без блокировки рекламы и окон", () => SetLevel(1)));
        menu.Items.Add(Theme.Item((level == -1 ? "●  " : "○  ") + "Ненадёжный — без JavaScript, окон и разрешений", () => SetLevel(-1)));
        menu.Items.Add(new ToolStripSeparator());

        menu.Items.Add(Theme.Item((DarkFor(host) ? "●  " : "○  ") + "Тёмная тема для этого сайта", ToggleDarkForSite));
        if (Cfg.SiteZoom.TryGetValue(PasswordTools.HostKey(host), out var sz))
            menu.Items.Add(Theme.Item($"Масштаб этого сайта: {sz}% — сбросить", () => { CopyAndSave(x => x.SiteZoom.Remove(PasswordTools.HostKey(host))); ApplySiteZoom(tab); }));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem(L.T("Разрешения")) { Enabled = false, Padding = new Padding(6, 4, 6, 2) });
        Cfg.SitePermissions.TryGetValue(key, out var perms);
        if (perms == null || perms.Count == 0)
            menu.Items.Add(new ToolStripMenuItem(L.T("   Сайт ничего не запрашивал")) { Enabled = false });
        else
            foreach (var (kind, name) in PermissionNames)
            {
                if (!perms.TryGetValue(kind.ToString(), out var v)) continue;
                var k = kind.ToString();
                var item = new ToolStripMenuItem($"   {name}: {(v == "allow" ? "разрешено" : "запрещено")}") { ForeColor = Theme.Text, Padding = new Padding(6, 4, 6, 4) };
                item.DropDown = Theme.NewMenu();
                item.DropDownItems.Add(Theme.Item("Разрешить", () => CopyAndSave(x => SetPerm(x, key, k, "allow"))));
                item.DropDownItems.Add(Theme.Item("Запретить", () => CopyAndSave(x => SetPerm(x, key, k, "deny"))));
                item.DropDownItems.Add(Theme.Item("Спрашивать снова", () => CopyAndSave(x => SetPerm(x, key, k, null))));
                menu.Items.Add(item);
            }
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(Theme.Item("Удалить cookies этого сайта", async () =>
        {
            int n = 0;
            foreach (var u in new[] { "https://" + key, "https://www." + key, "https://" + host })
            {
                foreach (var c in await core.CookieManager.GetCookiesAsync(u)) { core.CookieManager.DeleteCookie(c); n++; }
            }
            MessageBox.Show(this, n > 0 ? $"Удалено cookies: {n}. Возможно, сайт попросит войти заново." : "У этого сайта нет cookies.", host);
        }));
        menu.Items.Add(Theme.Item("Все сайты и разрешения…", () => OpenLitePage(LitePages.Settings, "#sites")));
        var r = RectangleToClient(address.LockScreenRect);
        menu.Show(this, new Point(r.Left, r.Bottom + Theme.S(6)));
    }

    static void SetPerm(AppSettings s, string site, string kind, string? value)
    {
        if (!s.SitePermissions.TryGetValue(site, out var d)) s.SitePermissions[site] = d = new Dictionary<string, string>();
        if (value == null) d.Remove(kind); else d[kind] = value;
        if (d.Count == 0) s.SitePermissions.Remove(site);
    }

    void UpdateShield()
    {
        var host = active?.PageHost ?? "";
        bool web = active?.Core?.Source is { } s && s.StartsWith("http") && !s.StartsWith(LitePages.Prefix);
        address.SetBlocked(active?.Blocked ?? 0, web && Cfg.BlockAds && SiteAllowed(host));
    }

    /// <summary>Скрипты скрытия рекламы и YouTube в странице: пересоздаются при смене списков или настроек.</summary>
    async void ApplyAdScripts(BrowserTab tab)
    {
        var core = tab.Core;
        if (core == null) return;
        bool cosmetic = Cfg.BlockAds && Cfg.CosmeticFiltering;
        bool yt = Cfg.BlockAds && Cfg.YouTubeAds;
        var eng = AdBlock.Engine;
        string sig = (cosmetic ? eng.Version : -1) + "|" + yt;
        if (sig == tab.AdSig) return;
        tab.AdSig = sig;
        try
        {
            if (tab.AdScriptId != null) { core.RemoveScriptToExecuteOnDocumentCreated(tab.AdScriptId); tab.AdScriptId = null; }
            if (tab.YtScriptId != null) { core.RemoveScriptToExecuteOnDocumentCreated(tab.YtScriptId); tab.YtScriptId = null; }
            if (cosmetic) tab.AdScriptId = await core.AddScriptToExecuteOnDocumentCreatedAsync(AdScripts.Cosmetic(eng.GenericCss));
            if (yt) tab.YtScriptId = await core.AddScriptToExecuteOnDocumentCreatedAsync(AdScripts.YouTube);
        }
        catch { tab.AdSig = ""; }
    }

    void OnAdBlockChanged()
    {
        if (IsDisposed) return;
        if (InvokeRequired) { BeginInvoke(OnAdBlockChanged); return; }
        foreach (var t in tabs) { ApplyFilters(t); ApplyAdScripts(t); }
    }

    void ShowShieldMenu()
    {
        var tab = active;
        if (tab == null) return;
        var host = tab.PageHost;
        bool web = tab.Core?.Source is { } s && s.StartsWith("http") && !s.StartsWith(LitePages.Prefix);
        bool off = SiteAllowed(host);
        var menu = Theme.NewMenu();
        menu.Items.Add(new ToolStripMenuItem(off ? $"Блокировка на {host} выключена" : $"Заблокировано на этой странице: {tab.Blocked}")
        { Enabled = false, Padding = new Padding(6, 6, 6, 3) });
        if (!off)
        {
            if (tab.PopupsBlocked > 0)
                menu.Items.Add(new ToolStripMenuItem($"   всплывающих окон: {tab.PopupsBlocked}") { Enabled = false });
            foreach (var kv in tab.BlockedHosts.OrderByDescending(x => x.Value).Take(8))
                menu.Items.Add(new ToolStripMenuItem($"   {kv.Key}  ×{kv.Value}") { Enabled = false });
        }
        menu.Items.Add(new ToolStripSeparator());
        if (web && host.Length > 0)
        {
            var key = PasswordTools.HostKey(host);
            menu.Items.Add(Theme.Item(off ? $"Включить блокировку на {key}" : $"Выключить блокировку на {key}", () =>
            {
                CopyAndSave(x => { if (off) x.AdblockAllow.Remove(key); else x.AdblockAllow.Add(key); });
                tab.Core?.Reload();
            }));
        }
        menu.Items.Add(Theme.Item("Настройки блокировки", () => OpenLitePage(LitePages.Settings, "#adblock")));
        menu.Items.Add(Theme.Item("Центр безопасности", () => OpenLitePage(LitePages.Security)));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem($"Всего заблокировано: {Store.BlockedTotal:N0}") { Enabled = false, Padding = new Padding(6, 3, 6, 6) });
        var r = RectangleToClient(address.PillScreenRect);
        menu.Show(this, new Point(r.Right - menu.PreferredSize.Width, r.Bottom + Theme.S(6)));
    }

    /// <summary>Фильтры запросов: свой список (быстро) или «все запросы» для списков фильтров.</summary>
    static void ApplyFilters(BrowserTab tab)
    {
        var core = tab.Core;
        if (core == null) return;
        string mode = Cfg.DataSaver || Cfg.ParentalEnabled ? "all" : !Cfg.BlockAds ? "none" : Cfg.ExtendedBlocklist || AdBlock.AnyEnabled(Cfg) ? "all" : "builtin";
        if (mode == tab.FilterMode) return;
        ForEachFilter(tab.FilterMode, f => core.RemoveWebResourceRequestedFilter(f, CoreWebView2WebResourceContext.All, CoreWebView2WebResourceRequestSourceKinds.All));
        ForEachFilter(mode, f => core.AddWebResourceRequestedFilter(f, CoreWebView2WebResourceContext.All, CoreWebView2WebResourceRequestSourceKinds.All));
        tab.FilterMode = mode;
    }

    static void ForEachFilter(string mode, Action<string> f)
    {
        if (mode == "all") f("*");
        else if (mode == "builtin")
            foreach (var h in Blocklist.BuiltIn) { f($"*://{h}/*"); f($"*://*.{h}/*"); }
    }

}
