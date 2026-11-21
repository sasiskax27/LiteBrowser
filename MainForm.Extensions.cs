using System.Text;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Text.Json;
using System.Web;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace LiteBrowser;

/// <summary>Обновления, браузер по умолчанию и расширения.</summary>
sealed partial class MainForm
{
    // ================= Обновления и браузер по умолчанию =================

    /// <summary>Через несколько секунд после запуска: проверить обновления и предложить стать основным браузером.</summary>
    public async Task RunStartupChecks()
    {
        await Task.Delay(4000);
        if (IsDisposed || Incognito) return;
        if (Cfg.AutoUpdate)
        {
            try
            {
                var r = await Updater.CheckAsync();
                if (r != null && r.Tag != Cfg.SkippedUpdate && !IsDisposed) { ShowUpdate(r); return; }
            }
            catch { /* нет сети — проверим в другой раз */ }
        }
        if (!IsDisposed && !Cfg.DefaultPromptDismissed && !SystemIntegration.IsDefaultBrowser()) ShowDefaultPrompt();
    }

    /// <summary>После неожиданного закрытия — предложить вернуть вкладки.</summary>
    public void OfferCrashRestore(Session s)
    {
        BeginInvoke(() => infoBar.ShowBar("crash", "", $"LiteBrowser был закрыт неожиданно. Вернуть вкладки ({s.Tabs.Count})?",
            ("Восстановить", () =>
            {
                infoBar.HideBar();
                var first = active;
                foreach (var u in s.Tabs)
                {
                    string title = u.StartsWith(LitePages.Prefix) ? "Новая вкладка" : Uri.TryCreate(u, UriKind.Absolute, out var x) ? x.Host : u;
                    CreateTab(u, select: false, title);
                }
                // Пустую стартовую вкладку убираем, если она одна и ничего не открыто
                if (first is { IsHome: true } && tabs.Count > 1) CloseTab(first);
                SelectTab(tabs[Math.Clamp(s.Active, 0, tabs.Count - 1)]);
            }, true),
            ("Не нужно", () => infoBar.HideBar(), false)));
    }

    /// <summary>Синхронизация при запуске (если включена).</summary>
    public async Task RunStartupSync()
    {
        await Task.Delay(6000);
        if (IsDisposed || Incognito || !Cfg.SyncEnabled || !Sync.HasPassword || string.IsNullOrWhiteSpace(Cfg.SyncFolder)) return;
        try { await Sync.RunAsync(); }
        catch (Exception ex)
        {
            if (!IsDisposed && infoBar.Key == null)
                infoBar.ShowBar("sync", "", "Синхронизация не удалась: " + ex.Message,
                    ("Настройки", () => { OpenLitePage(LitePages.Settings, "#sync"); infoBar.HideBar(); }, true));
        }
    }

    void ShowDefaultPrompt()
    {
        infoBar.ShowBar("default", "", "Сделать LiteBrowser основным браузером? Ссылки из Telegram, Discord и других программ будут открываться здесь.",
            ("Сделать основным", () =>
            {
                try { SystemIntegration.RegisterAsBrowser(); infoBar.SetText("Выберите LiteBrowser в открывшемся окне Windows → «Веб-браузер»"); }
                catch (Exception ex) { MessageBox.Show(this, ex.Message, "LiteBrowser"); }
            }, true),
            ("Не сейчас", () => infoBar.HideBar(), false),
            ("Больше не спрашивать", () => { CopyAndSave(x => x.DefaultPromptDismissed = true); infoBar.HideBar(); }, false));
    }

    void ShowUpdate(ReleaseInfo r)
    {
        infoBar.ShowBar("update", "", $"Доступна новая версия LiteBrowser {r.Version.ToString(2)} (у вас {AppEnv.Version})",
            ("Обновить и перезапустить", () => _ = DoUpdate(r), true),
            ("Что нового", () => _ = AddTab(r.PageUrl), false),
            ("Пропустить", () => { CopyAndSave(x => x.SkippedUpdate = r.Tag); infoBar.HideBar(); }, false));
    }

    async Task DoUpdate(ReleaseInfo r)
    {
        try
        {
            infoBar.SetText("Скачиваю обновление…");
            var prog = new Progress<double>(p => infoBar.SetText($"Скачиваю обновление… {p:P0}"));
            await Updater.InstallAsync(r, prog);
            infoBar.SetText("Готово, перезапускаю…");
            await Task.Delay(500);
            BrowserApp.Instance.Restart();
        }
        catch (Exception ex) { infoBar.SetText("Не удалось обновить: " + ex.Message); }
    }

    // ================= Расширения =================

    CoreWebView2Profile? MainProfile => tabs.FirstOrDefault(t => t.Core != null)?.Core?.Profile;

    async Task InstallExtension(string url, CoreWebView2? page)
    {
        void Status(string text, bool ok = true) { if (page != null) Post(page, new { type = "extStatus", text, ok }); }
        if (Incognito) { MessageBox.Show(this, "Расширения устанавливаются из обычного окна, не из инкогнито.", "Расширения"); return; }
        if (Cfg.ParentalEnabled) { Status("Установка расширений отключена родительским контролем", false); if (page == null) MessageBox.Show(this, "Установка расширений отключена родительским контролем.", "Расширения"); return; }
        var r = Extensions.Parse(url);
        var profile = MainProfile;
        if (r == null || profile == null) { Status("Не похоже на ссылку на расширение из Chrome Web Store, Edge Add-ons или Firefox Add-ons", false); return; }
        ExtensionInfo info;
        try
        {
            Status("Скачиваю из " + Extensions.StoreName(r.Store) + "…");
            installBtn.Enabled = false;
            var env = await AppEnv.GetEnvironment();
            info = await Extensions.Download(r, env.BrowserVersionString);
        }
        catch (Exception ex)
        {
            Status(ex.Message, false);
            if (page == null) MessageBox.Show(this, ex.Message, "Не удалось установить расширение", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        finally { installBtn.Enabled = true; }

        var rights = string.Join("\n", Extensions.DescribePermissions(info).Select(x => "  • " + x));
        var answer = MessageBox.Show(this,
            $"Установить «{info.Name}» {info.Version}?\n\nИсточник: {Extensions.StoreName(info.Store)}\n\nРасширение сможет:\n{rights}\n\n" +
            "Устанавливайте только расширения, которым доверяете: они работают внутри браузера.",
            "Установка расширения", MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2);
        if (answer != DialogResult.Yes) { Extensions.Discard(info); Status("Установка отменена", false); return; }
        try
        {
            await Extensions.Install(profile, info);
            Status($"«{info.Name}» установлено");
            if (page == null)
                MessageBox.Show(this, $"«{info.Name}» установлено.\n\nОткрыть окно и настройки расширения можно в меню ⋯ → «Расширения».", "Расширения");
            if (page != null) await SendExtensions(page);
        }
        catch (Exception ex)
        {
            Status(ex.Message, false);
            if (page == null) MessageBox.Show(this, ex.Message, "Не удалось установить расширение", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    async Task SendExtensions(CoreWebView2 page)
    {
        var profile = MainProfile;
        if (profile == null) return;
        var meta = Extensions.LoadMeta();
        var list = new List<object>();
        foreach (var ext in await profile.GetBrowserExtensionsAsync())
        {
            meta.TryGetValue(ext.Id, out var info);
            list.Add(new
            {
                id = ext.Id, name = info?.Name ?? ext.Name, enabled = ext.IsEnabled,
                version = info?.Version ?? "", desc = info?.Description ?? "",
                store = info != null ? Extensions.StoreName(info.Store) : "",
                popup = info?.Popup, options = info?.Options,
                icon = info != null ? Extensions.IconData(info) : null,
                rights = info != null ? Extensions.DescribePermissions(info) : new List<string>(),
            });
        }
        Post(page, new
        {
            type = "extList", items = list,
            recommended = Extensions.Recommended.Select(x => new { x.name, x.desc, x.url }),
        });
    }

    async Task HandleExtensionMessage(CoreWebView2 core, string type, JsonElement root)
    {
        string Str(string name) => root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
        var profile = MainProfile;
        switch (type)
        {
            case "ext:list":
                await SendExtensions(core);
                break;
            case "ext:install":
                await InstallExtension(Str("url"), core);
                break;
            case "ext:toggle":
                if (profile == null) break;
                bool on = root.TryGetProperty("enabled", out var en) && en.ValueKind == JsonValueKind.True;
                foreach (var ext in await profile.GetBrowserExtensionsAsync())
                    if (ext.Id == Str("id")) await ext.EnableAsync(on);
                await SendExtensions(core);
                break;
            case "ext:remove":
                if (profile == null) break;
                await Extensions.Remove(profile, Str("id"));
                await SendExtensions(core);
                Post(core, new { type = "extStatus", text = "Расширение удалено", ok = true });
                break;
            case "ext:open":
                var id = Str("id");
                if (!System.Text.RegularExpressions.Regex.IsMatch(id, "^[a-p]{32}$")) break;
                var info = Extensions.LoadMeta().GetValueOrDefault(id);
                var path = Str("page") == "options" ? info?.Options : info?.Popup;
                if (string.IsNullOrEmpty(path)) break;
                _ = AddTab($"chrome-extension://{id}/{path.TrimStart('/')}");
                break;
            case "ext:store":
                var storeUrl = Str("store") switch
                {
                    "edge" => "https://microsoftedge.microsoft.com/addons/category/Edge-Extensions",
                    "firefox" => "https://addons.mozilla.org/ru/firefox/extensions/",
                    _ => "https://chromewebstore.google.com/category/extensions",
                };
                _ = AddTab(storeUrl);
                break;
        }
    }

}
