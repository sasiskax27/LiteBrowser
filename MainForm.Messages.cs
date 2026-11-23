using System.Text;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Text.Json;
using System.Web;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace LiteBrowser;

/// <summary>Сообщения от своих страниц (lite.home).</summary>
sealed partial class MainForm
{
    // ================= Сообщения от своих страниц =================

    static void Post(CoreWebView2 core, object msg) => core.PostWebMessageAsJson(JsonSerializer.Serialize(msg, AppSettings.Json));

    /// <summary>Настройки для гостевого окна: только оформление и поисковик, без ваших плиток и данных.</summary>
    static AppSettings GuestSettings() => new()
    {
        UiTheme = Cfg.UiTheme, Accent = Cfg.Accent, Engine = Cfg.Engine, Language = Cfg.Language, HomeWidgets = Cfg.HomeWidgets, HomeAnim = Cfg.HomeAnim,
        WidgetCity = Cfg.WidgetCity, ImportOffered = true,
    };

    /// <summary>История: совпадения в названии/адресе, а затем — страницы, где запрос встречается в тексте (с отрывком).</summary>
    static List<object> HistoryWithText(string q)
    {
        var items = Store.SearchHistory(q, 600).Select(h => (object)new { title = h.Title, url = h.Url, time = h.Time, snippet = "" }).ToList();
        if (q.Trim().Length < 2 || !Cfg.FullTextHistory) return items;
        var seen = new HashSet<string>(Store.SearchHistory(q, 600).Select(h => h.Url));
        foreach (var (p, snip) in PageIndex.Search(q, 100))
            if (seen.Add(p.Url)) items.Add(new { title = p.Title, url = p.Url, time = p.Time, snippet = snip });
        return items;
    }

    void SendSettings(CoreWebView2 core) => Post(core, new
    {
        type = "settings",
        settings = Guest ? GuestSettings() : Cfg,
        version = AppEnv.Version,
        browserVersion = AppEnv.GetEnvironment().IsCompletedSuccessfully ? AppEnv.GetEnvironment().Result.BrowserVersionString : "",
        incognito = Incognito,
        startupMs = AppEnv.StartupMs,
        portable = AppEnv.Portable,
        dataDir = AppEnv.Portable ? AppEnv.DataDir : "",
        isDefault = SystemIntegration.IsDefaultBrowser(),
        sync = new
        {
            hasPassword = Sync.HasPassword, status = Sync.Status, running = Sync.Running,
            clouds = Sync.CloudFolders().Select(c => new { c.name, c.path }),
        },
        accents = Theme.Accents.Select(a => new { id = a.Key, name = a.Value.name, color = ColorTranslator.ToHtml(a.Value.a) }),
        importSuggest = !Incognito && !Cfg.ImportOffered && Store.Bookmarks.Count == 0,
        restartNeeded = AppEnv.RestartNeeded,
        proxyRunning = AppEnv.RunningSignature != "off",
        defaultSites = AppSettings.DefaultProxySites,
        blocklist = new { count = Blocklist.ExtendedCount, updated = Blocklist.Updated?.ToString("dd.MM.yyyy HH:mm"), updating = Blocklist.Updating },
        adblock = new
        {
            updating = AdBlock.Updating,
            total = Store.BlockedTotal,
            rules = AdBlock.Engine.NetworkRules + AdBlock.Engine.CosmeticRules,
            lists = AdBlock.Lists.Select(l => new
            {
                id = l.Id, name = l.Name, desc = l.Description,
                rules = AdBlock.Status[l.Id].Rules,
                updated = AdBlock.Status[l.Id].Updated?.ToString("dd.MM.yyyy"),
                error = AdBlock.Status[l.Id].Error,
            }),
        },
        engines = SearchEngines.All.Select(x => new { id = x.Id, name = x.Name, desc = x.Description, @private = x.Private }),
    });

    async Task HandleMessage(CoreWebView2 core, string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        string Str(string name) => root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

        var msgType = Str("type");
        if (msgType.StartsWith("pw:"))
        {
            // Менеджер паролей доступен только со своей страницы паролей
            if (core.Source.StartsWith(LitePages.Passwords, StringComparison.Ordinal)) await HandleVaultMessage(core, msgType, root);
            return;
        }
        if (msgType.StartsWith("ai:"))
        {
            // ИИ-помощник — только со своей страницы помощника
            if (core.Source.StartsWith(LitePages.Ai, StringComparison.Ordinal)) await HandleAiMessage(core, msgType, root);
            return;
        }
        if (msgType.StartsWith("off:"))
        {
            if (core.Source.StartsWith(LitePages.OfflinePage, StringComparison.Ordinal)) HandleOfflineMessage(core, msgType, root);
            return;
        }
        if (msgType.StartsWith("dev:"))
        {
            // Инструменты разработчика — только со своей страницы
            if (core.Source.StartsWith(LitePages.DevTools, StringComparison.Ordinal)) await HandleDevMessage(core, msgType, root);
            return;
        }
        if (msgType.StartsWith("sec:"))
        {
            // Центр безопасности — только со своей страницы
            if (core.Source.StartsWith(LitePages.Security, StringComparison.Ordinal)) await HandleSecurityMessage(core, msgType, root);
            return;
        }
        if (msgType.StartsWith("ext:"))
        {
            // Управление расширениями — только со страницы расширений
            if (core.Source.StartsWith(LitePages.ExtensionsPage, StringComparison.Ordinal) || core.Source.StartsWith(LitePages.ExtPanel, StringComparison.Ordinal))
                await HandleExtensionMessage(core, msgType, root);
            return;
        }
        // Гостю — только чтение стартовой страницы, без ваших плиток, заметки и настроек
        if (Guest && msgType is not ("get" or "home:widgets")) return;
        switch (msgType)
        {
            case "get":
                SendSettings(core);
                break;

            case "save":
                var ns = root.GetProperty("settings").Deserialize<AppSettings>(AppSettings.Json);
                if (ns == null) return;
                // Родительский контроль меняется только отдельно и с PIN-кодом
                ns.ParentalEnabled = Cfg.ParentalEnabled; ns.ParentalPinHash = Cfg.ParentalPinHash;
                ns.LockPinHash = Cfg.LockPinHash; ns.LockHello = Cfg.LockHello; // пароль на браузер меняется только через lock:*
                ns.ParentalAdult = Cfg.ParentalAdult; ns.ParentalBlocked = Cfg.ParentalBlocked.ToList();
                bool wantBlocklist = ns.ExtendedBlocklist && ns.BlockAds && Blocklist.ExtendedCount == 0;
                bool listsChanged = AdBlock.Lists.Any(l => AdBlock.Enabled(ns, l.Id) != AdBlock.Enabled(Cfg, l.Id));
                AppEnv.UpdateSettings(ns);
                Post(core, new { type = "saved", restartNeeded = AppEnv.RestartNeeded });
                if (listsChanged) { await AdBlock.ReloadAsync(Cfg); SendSettings(core); }
                if (wantBlocklist) { await Blocklist.UpdateAsync(AppEnv.DataDir, Cfg, force: true); SendSettings(core); }
                break;

            case "clear":
                await core.Profile.ClearBrowsingDataAsync();
                Store.ClearHistory();
                Post(core, new { type = "done", text = "Данные удалены" });
                break;

            case "testProxy":
                int port = root.TryGetProperty("port", out var p) && p.TryGetInt32(out var pp) ? pp : 0;
                var host = Str("host");
                var (ok, ms) = port is > 0 and < 65536 && host.Length > 0 ? await AppEnv.TestProxy(host, port) : (false, 0);
                Post(core, new { type = "proxyTest", ok, ms });
                break;

            case "restart":
                BrowserApp.Instance.Restart();
                break;

            // ---- Прочитать позже ----
            case "rl:list":
                Post(core, ReadLaterList());
                break;
            case "rl:open":
                var rlu = Str("url");
                if (rlu.StartsWith("http")) { ReadLater.MarkVisited(rlu); Navigate(rlu); }
                break;
            case "rl:read":
                ReadLater.SetRead(Str("url"), root.TryGetProperty("read", out var rr) && rr.ValueKind == JsonValueKind.True);
                Post(core, ReadLaterList());
                break;
            case "rl:remove":
                ReadLater.Remove(Str("url"));
                Post(core, ReadLaterList());
                break;

            // ---- Встроенный Tor ----
            case "tor:status":
                Post(core, TorStatus());
                break;
            case "tor:enable":
                var bridges = Str("bridges") is "obfs4" or "snowflake" ? Str("bridges") : "";
                CopyAndSave(x => { x.TorEnabled = true; x.TorBridges = bridges; });
                TorService.Stop();
                _ = Task.Run(async () =>
                {
                    try { await TorService.StartAsync(bridges); }
                    catch (Exception ex) { BeginInvoke(() => Post(core, new { type = "torError", text = ex.Message })); }
                });
                Post(core, TorStatus());
                break;
            case "tor:disable":
                CopyAndSave(x => x.TorEnabled = false);
                TorService.Stop();
                Post(core, TorStatus());
                break;

            case "updateBlocklist":
                var adTask = AdBlock.UpdateAsync(Cfg, force: true);
                SendSettings(core);
                bool updated = await Blocklist.UpdateAsync(AppEnv.DataDir, Cfg, force: true);
                await adTask;
                SendSettings(core);
                bool adErrors = AdBlock.Lists.Any(l => AdBlock.Enabled(Cfg, l.Id) && AdBlock.Status[l.Id].Error != null);
                Post(core, new { type = "done", text = updated && !adErrors ? "Списки обновлены" : "Часть списков не удалось обновить" });
                break;

            case "shortcuts":
                SystemIntegration.CreateShortcuts();
                Post(core, new { type = "done", text = "Ярлыки созданы на рабочем столе и в меню «Пуск»" });
                break;

            case "defaultBrowser":
                SystemIntegration.RegisterAsBrowser();
                Post(core, new { type = "done", text = "Выберите LiteBrowser в открывшемся окне Windows" });
                break;

            case "checkUpdate":
                try
                {
                    var rel = await Updater.CheckAsync();
                    if (rel == null) Post(core, new { type = "done", text = $"У вас последняя версия ({AppEnv.Version})" });
                    else { ShowUpdate(rel); Post(core, new { type = "done", text = $"Найдена версия {rel.Version.ToString(2)} — смотрите плашку сверху" }); }
                }
                catch (Exception ex) { Post(core, new { type = "error", text = "Не удалось проверить: " + ex.Message }); }
                break;

            case "historyGet":
                Post(core, new
                {
                    type = "history",
                    saving = Cfg.SaveHistory,
                    items = HistoryWithText(Str("q")),
                });
                break;

            case "historyDelete":
                if (root.TryGetProperty("time", out var tm) && tm.TryGetDateTime(out var time)) Store.DeleteHistory(Str("url"), time);
                break;

            case "historyClear":
                Store.ClearHistory();
                Post(core, new { type = "done", text = "История очищена" });
                break;

            // ---- Перенос из других браузеров ----
            case "importScan":
                var found = await Task.Run(Importer.Scan);
                Post(core, new
                {
                    type = "importList",
                    items = found.Select(x => new { id = x.Id, browser = x.Browser, profile = x.Profile, bookmarks = x.HasBookmarks, history = x.HasHistory }),
                });
                break;

            case "importRun":
                var prof = (await Task.Run(Importer.Scan)).FirstOrDefault(x => x.Id == Str("id"));
                if (prof == null) { Post(core, new { type = "importResult", ok = false, text = "Профиль не найден" }); break; }
                bool wantB = root.TryGetProperty("bookmarks", out var wb) && wb.ValueKind == JsonValueKind.True;
                bool wantH = root.TryGetProperty("history", out var wh) && wh.ValueKind == JsonValueKind.True;
                try
                {
                    var bms = wantB ? await Task.Run(() => Importer.ReadBookmarks(prof)) : new();
                    var hist = wantH ? await Task.Run(() => Importer.ReadHistory(prof)) : new();
                    int nb = Store.ImportBookmarks(bms), nh = Store.ImportHistory(hist);
                    CopyAndSave(x => x.ImportOffered = true);
                    Post(core, new { type = "importResult", ok = true, text = $"Перенесено из {prof.Browser}: закладок — {nb}, записей истории — {nh}" });
                }
                catch (Exception ex)
                {
                    Post(core, new { type = "importResult", ok = false, text = ex.Message });
                }
                break;

            case "exportHtml":
                BeginInvoke(() =>
                {
                    using var dlg = new SaveFileDialog
                    {
                        Title = "Сохранить закладки",
                        Filter = "HTML-файл закладок|*.html",
                        FileName = $"LiteBrowser-закладки-{DateTime.Now:yyyy-MM-dd}.html",
                    };
                    if (dlg.ShowDialog(this) != DialogResult.OK) return;
                    File.WriteAllText(dlg.FileName, Importer.ExportHtml(Store.Bookmarks), new System.Text.UTF8Encoding(false));
                    Post(core, new { type = "done", text = $"Сохранено закладок: {Store.Bookmarks.Count}" });
                });
                break;

            case "importHtml":
                BeginInvoke(() =>
                {
                    using var dlg = new OpenFileDialog { Title = "Выберите файл закладок", Filter = "HTML-файл закладок|*.html;*.htm" };
                    if (dlg.ShowDialog(this) != DialogResult.OK) return;
                    try
                    {
                        if (new FileInfo(dlg.FileName).Length > 50_000_000) throw new IOException("Файл слишком большой");
                        int n = Store.ImportBookmarks(Importer.ParseHtml(File.ReadAllText(dlg.FileName)));
                        Post(core, new { type = "importResult", ok = true, text = $"Добавлено закладок: {n}" });
                    }
                    catch (Exception ex) { Post(core, new { type = "importResult", ok = false, text = ex.Message }); }
                });
                break;

            case "dismissImport":
                CopyAndSave(x => x.ImportOffered = true);
                break;

            case "openAi":
                OpenAi();
                break;

            // ---- Родительский контроль ----
            case "parental:set":
            {
                var pin = Str("pin");
                bool want = root.TryGetProperty("enabled", out var pe) && pe.ValueKind == JsonValueKind.True;
                bool adultOn = !root.TryGetProperty("adult", out var pa) || pa.ValueKind != JsonValueKind.False;
                var blocked = root.TryGetProperty("blocked", out var pb) && pb.ValueKind == JsonValueKind.Array
                    ? pb.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!).ToList() : new List<string>();
                if (Cfg.ParentalEnabled && !Parental.CheckPin(pin)) { Post(core, new { type = "error", text = "Неверный PIN-код" }); break; }
                if (!Cfg.ParentalEnabled && want && (pin.Length < 4 || !pin.All(char.IsDigit))) { Post(core, new { type = "error", text = "Придумайте PIN-код из 4 и более цифр" }); break; }
                var newPin = Str("newPin");
                CopyAndSave(x =>
                {
                    x.ParentalEnabled = want;
                    x.ParentalAdult = adultOn;
                    x.ParentalBlocked = blocked;
                    if (!Cfg.ParentalEnabled && want) x.ParentalPinHash = Parental.Hash(pin);
                    if (newPin.Length >= 4 && newPin.All(char.IsDigit)) x.ParentalPinHash = Parental.Hash(newPin);
                    if (!want) x.ParentalPinHash = "";
                });
                if (want && adultOn) _ = Parental.InitAsync();
                foreach (var t in tabs) ApplyFilters(t);
                Post(core, new { type = "done", text = want ? "Родительский контроль включён" : "Родительский контроль выключен" });
                SendSettings(core);
                break;
            }

            // ---- Синхронизация ----
            case "sync:pick":
                BeginInvoke(() =>
                {
                    using var dlg = new FolderBrowserDialog { Description = "Папка на облачном диске (Google Диск, Яндекс Диск, OneDrive, Dropbox)", UseDescriptionForTitle = true };
                    if (dlg.ShowDialog(this) != DialogResult.OK) return;
                    var folder = dlg.SelectedPath;
                    CopyAndSave(x => x.SyncFolder = folder);
                    SendSettings(core);
                });
                break;
            case "sync:folder":
                var sf = Str("path");
                if (Path.IsPathRooted(sf)) { CopyAndSave(x => x.SyncFolder = sf); SendSettings(core); }
                break;
            case "sync:webdav":
            {
                var url = Str("url").Trim();
                var user = Str("user").Trim();
                var pass = Str("pass");
                if (!WebDav.Allowed(url)) { Post(core, new { type = "error", text = L.T("Нужен адрес https:// (http — только для своего компьютера или домашней сети)") }); break; }
                try
                {
                    await WebDav.TestAsync(url, user, pass);
                    WebDav.SaveCredentials(user, pass);
                    CopyAndSave(x => x.SyncFolder = url);
                    Post(core, new { type = "done", text = L.T("Сервер подключён. Задайте пароль синхронизации и нажмите «Синхронизировать»") });
                }
                catch (Exception ex) { Post(core, new { type = "error", text = ex.Message }); }
                SendSettings(core);
                break;
            }
            case "sync:password":
                Sync.SetPassword(Str("password"));
                Post(core, new { type = "done", text = "Пароль синхронизации сохранён. Задайте такой же на других компьютерах" });
                SendSettings(core);
                break;
            case "sync:forget":
                Sync.Forget();
                WebDav.Forget();
                CopyAndSave(x => x.SyncEnabled = false);
                SendSettings(core);
                break;
            case "sync:now":
                try
                {
                    Post(core, new { type = "done", text = "Синхронизирую…" });
                    var st = await Sync.RunAsync();
                    Post(core, new { type = "done", text = st });
                }
                catch (Exception ex) { Post(core, new { type = "error", text = ex.Message }); }
                SendSettings(core);
                break;

            // ---- Оформление стартовой страницы ----
            case "pickWallpaper":
                BeginInvoke(() =>
                {
                    using var dlg = new OpenFileDialog { Title = L.T("Картинка или видео для стартовой страницы"), Filter = L.T("Картинки и видео") + "|*.jpg;*.jpeg;*.png;*.webp;*.gif;*.mp4;*.webm" };
                    if (dlg.ShowDialog(this) != DialogResult.OK) return;
                    bool video = Path.GetExtension(dlg.FileName).ToLowerInvariant() is ".mp4" or ".webm";
                    if (new FileInfo(dlg.FileName).Length > (video ? 80_000_000 : 25_000_000)) { Post(core, new { type = "error", text = video ? L.T("Видео больше 80 МБ") : L.T("Картинка больше 25 МБ") }); return; }
                    File.Copy(dlg.FileName, Path.Combine(AppEnv.DataDir, "wallpaper.img"), true);
                    CopyAndSave(x => { x.Wallpaper = true; x.WallpaperVideo = video; x.WallpaperVersion++; });
                    Post(core, new { type = "done", text = "Обои установлены — откройте новую вкладку" });
                    SendSettings(core);
                });
                break;
            case "removeWallpaper":
                try { File.Delete(Path.Combine(AppEnv.DataDir, "wallpaper.img")); } catch { }
                CopyAndSave(x => { x.Wallpaper = false; x.WallpaperVideo = false; });
                SendSettings(core);
                break;
            // ---- Пароль на браузер ----
            case "lock:set":
            {
                var pin = Str("pin");
                if (AppLock.Enabled && !AppLock.Check(Str("old"))) { Post(core, new { type = "lockResult", ok = false, text = L.T("Неверный текущий PIN-код") }); break; }
                if (pin.Length < 4 || pin.Length > 32) { Post(core, new { type = "lockResult", ok = false, text = L.T("PIN-код — от 4 до 32 символов") }); break; }
                var hash = AppLock.Hash(pin);
                CopyAndSave(x => x.LockPinHash = hash);
                Post(core, new { type = "lockResult", ok = true, enabled = true, text = L.T("Пароль на браузер включён") });
                break;
            }
            case "lock:hello":
            {
                bool on = root.TryGetProperty("on", out var hv) && hv.ValueKind == JsonValueKind.True;
                if (!on) { CopyAndSave(x => x.LockHello = false); Post(core, new { type = "lockResult", ok = true, enabled = AppLock.Enabled, hello = false, text = L.T("Вход через Windows Hello выключен") }); break; }
                if (!AppLock.Enabled) { Post(core, new { type = "lockResult", ok = false, text = L.T("Сначала включите PIN — он нужен, если Windows Hello не сработает") }); break; }
                // Включаем только после успешной проверки — так видно, что Hello настроен и работает
                var r = await WindowsHello.VerifyAsync(Handle, L.T("Подтвердите, что это вы"));
                if (r != WindowsHello.Result.Verified) { Post(core, new { type = "lockResult", ok = false, text = WindowsHello.Describe(r) }); break; }
                CopyAndSave(x => x.LockHello = true);
                Post(core, new { type = "lockResult", ok = true, enabled = true, hello = true, text = L.T("Вход через Windows Hello включён") });
                break;
            }
            case "lock:clear":
                if (AppLock.Enabled && !AppLock.Check(Str("old"))) { Post(core, new { type = "lockResult", ok = false, text = L.T("Неверный текущий PIN-код") }); break; }
                CopyAndSave(x => { x.LockPinHash = ""; x.LockHello = false; });
                Post(core, new { type = "lockResult", ok = true, enabled = false, text = L.T("Пароль на браузер выключен") });
                break;

            // ---- Виджеты стартовой страницы ----
            case "home:widgets":
                if (!Cfg.HomeWidgets) { Post(core, new { type = "widgets", enabled = false }); break; }
                object? weather = null, currency = null;
                try { weather = await Widgets.WeatherAsync(Cfg.WidgetCity); } catch { weather = new { error = L.T("Нет связи с сервисом погоды") }; }
                try { currency = await Widgets.CurrencyAsync(); } catch { }
                Post(core, new { type = "widgets", enabled = true, city = Cfg.WidgetCity, note = Guest ? "" : Cfg.HomeNote, weather, currency });
                break;
            case "home:city":
                var city = Str("city").Trim();
                if (city.Length > 60) city = city[..60];
                CopyAndSave(x => x.WidgetCity = city);
                goto case "home:widgets";
            case "home:note":
                var note = Str("note");
                if (note.Length > 2000) note = note[..2000];
                if (!Incognito) CopyAndSave(x => x.HomeNote = note);
                break;
            case "home:hideWidgets":
                CopyAndSave(x => x.HomeWidgets = false);
                break;

            case "tiles:save":
                if (!root.TryGetProperty("tiles", out var tl) || tl.ValueKind != JsonValueKind.Array) break;
                var newTiles = tl.EnumerateArray().Select(x => new HomeTile
                {
                    Name = x.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "",
                    Url = x.TryGetProperty("url", out var u) ? u.GetString() ?? "" : "",
                }).ToList();
                foreach (var nt in newTiles) if (!nt.Url.Contains("://")) nt.Url = "https://" + nt.Url;
                CopyAndSave(x => x.HomeTiles = newTiles);
                SendSettings(core);
                break;

            // ---- Загрузки ----
            case "dl:list":
                Post(core, new
                {
                    type = "dlList",
                    folder = Downloads.DefaultFolder,
                    items = Downloads.Items.Select(i => new
                    {
                        id = i.Id, name = i.Name, url = i.Url, path = i.Path, received = i.Received, total = i.Total,
                        state = i.State.ToString(), canceled = i.Canceled, time = i.Time, speed = (long)i.Speed,
                        paused = i.Paused, canPause = i.CanPause, threads = i.Threads,
                        exists = i.State == DlState.Completed && File.Exists(i.Path),
                        risk = DownloadGuard.Risk(i.Path), riskText = DownloadGuard.RiskText(DownloadGuard.Risk(i.Path)),
                    }),
                });
                break;
            case "dl:open":
                if (Downloads.Get(Str("id")) is { } d1) Downloads.Open(d1);
                break;
            case "dl:folder":
                Downloads.ShowInFolder(Downloads.Get(Str("id")));
                break;
            case "dl:pause":
                Downloads.Get(Str("id"))?.Pause();
                break;
            case "dl:resume":
                Downloads.Get(Str("id"))?.Resume();
                break;
            case "dl:cancel":
                Downloads.Get(Str("id"))?.Cancel();
                break;
            case "dl:remove":
                Downloads.Remove(Str("id"));
                break;
            case "dl:clear":
                Downloads.Clear();
                break;
            case "dl:hash":
                if (Downloads.Get(Str("id")) is { } dh && File.Exists(dh.Path))
                {
                    var hid = dh.Id;
                    try { Post(core, new { type = "dlHash", id = hid, sha256 = await DownloadGuard.Sha256Async(dh.Path) }); }
                    catch (Exception ex) { Post(core, new { type = "dlHash", id = hid, error = ex.Message }); }
                }
                break;

            // ---- Заметки ----
            case "notes:list":
                Post(core, new { type = "notesList", items = Store.Highlights.AsEnumerable().Reverse().Select(h => new { id = h.Id, url = h.Url, title = h.Title, text = h.Text, note = h.Note, time = h.Time }) });
                break;
            case "notes:remove":
                Store.RemoveHighlight(Str("id"));
                Post(core, new { type = "notesList", items = Store.Highlights.AsEnumerable().Reverse().Select(h => new { id = h.Id, url = h.Url, title = h.Title, text = h.Text, note = h.Note, time = h.Time }) });
                break;

            // ---- Закладки ----
            case "bm:list":
                Post(core, new { type = "bmList", items = Store.Bookmarks.Select((b, i) => new { i, title = b.Title, url = b.Url, icon = b.Icon }) });
                break;
            case "bm:remove":
                var bmr = Store.FindBookmark(Str("url"));
                if (bmr != null) Store.RemoveBookmark(bmr);
                Post(core, new { type = "bmList", items = Store.Bookmarks.Select((b, i) => new { i, title = b.Title, url = b.Url, icon = b.Icon }) });
                break;
            case "bm:rename":
                var bmn = Store.FindBookmark(Str("url"));
                if (bmn != null && Str("title").Trim().Length > 0) Store.RenameBookmark(bmn, Str("title").Trim());
                break;
            case "bm:add":
                var bu = Str("url").Trim();
                if (!bu.Contains("://")) bu = "https://" + bu;
                if (Uri.TryCreate(bu, UriKind.Absolute, out var buu) && buu.Scheme is "http" or "https")
                    Store.AddBookmark(Str("title").Trim().Length > 0 ? Str("title").Trim() : buu.Host, buu.ToString(), null);
                Post(core, new { type = "bmList", items = Store.Bookmarks.Select((b, i) => new { i, title = b.Title, url = b.Url, icon = b.Icon }) });
                break;
        }
    }


    static object TorStatus() => new
    {
        type = "torStatus", enabled = AppEnv.Settings.TorEnabled, bridges = AppEnv.Settings.TorBridges,
        installed = TorService.Installed, running = TorService.Running, busy = TorService.Busy,
        progress = TorService.Progress, status = TorService.Status, restartNeeded = AppEnv.RestartNeeded,
    };

    static object ReadLaterList() => new
    {
        type = "rlList",
        items = ReadLater.All.Select(i => new { url = i.Url, title = i.Title, added = i.Added, read = i.Read }),
    };

    void AddToReadLater()
    {
        var t = active;
        var url = t?.Core?.Source ?? "";
        if (!url.StartsWith("http")) return;
        ReadLater.Add(url, t!.Title);
        infoBar.ShowBar("readlater", "\uE7BC", L.T("Добавлено в «Прочитать позже»"), ("Открыть список", () => { infoBar.HideBar(); OpenLitePage(LitePages.ReadLaterPage); }, false), ("OK", () => infoBar.HideBar(), true));
    }
}
