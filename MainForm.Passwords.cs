using System.Text;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Text.Json;
using System.Web;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace LiteBrowser;

/// <summary>Менеджер паролей в окне.</summary>
sealed partial class MainForm
{
    // ================= Менеджер паролей =================

    void OnVaultChanged()
    {
        if (IsDisposed) return;
        if (InvokeRequired) { BeginInvoke(OnVaultChanged); return; }
        UpdateKey();
    }

    string? ActiveOrigin => active?.Core?.Source is { } s && !s.StartsWith(LitePages.Prefix) ? PasswordTools.Origin(s) : null;

    bool PendingFor(string? origin) =>
        origin != null && pendingCapture is { } p && PasswordTools.HostKey(p.origin) == PasswordTools.HostKey(origin) &&
        (DateTime.Now - p.time).TotalMinutes < 10;

    void UpdateKey()
    {
        var origin = ActiveOrigin;
        if (!Cfg.PasswordManager || origin == null || Guest) { address.SetKey(false, false); return; }
        bool saved = Vault.Unlocked && Vault.ForOrigin(origin).Count > 0;
        bool pending = PendingFor(origin);
        address.SetKey(active!.HasLoginForm || saved || pending, saved || pending);
    }

    /// <summary>Сообщение от скрипта паролей на обычной странице. Адрес сайта берём из движка, а не из сообщения.</summary>
    DateTime lastTranslate;

    void HandlePageMessage(BrowserTab tab, CoreWebView2 core, string source, string json)
    {
        if (json.Length > 4000) return;
        var origin = PasswordTools.Origin(source);
        if (origin == null) return;

        // Перевод выделенного текста (по клику на кнопку у выделения; не чаще раза в секунду)
        if (json.Contains("\"lite-translate\""))
        {
            if (!Cfg.SelectionTranslate || (DateTime.Now - lastTranslate).TotalMilliseconds < 800) return;
            lastTranslate = DateTime.Now;
            using var td = JsonDocument.Parse(json);
            var text = td.RootElement.TryGetProperty("text", out var tv) ? tv.GetString() ?? "" : "";
            if (text.Length is 0 or > 1500) return;
            _ = Task.Run(async () =>
            {
                string result, note;
                try
                {
                    var (tr, from) = await SelectionTranslate.TranslateAsync(text, L.Lang);
                    result = tr;
                    note = "Google " + L.T("Переводчик") + (from.Length > 0 ? " · " + from : "");
                }
                catch { result = L.T("Не удалось перевести — нет связи с переводчиком"); note = ""; }
                BeginInvoke(() => { try { _ = core.ExecuteScriptAsync($"window.__liteTr && window.__liteTr({JsonSerializer.Serialize(result)}, {JsonSerializer.Serialize(note)})"); } catch { } });
            });
            return;
        }

        // Стикеры: правка и удаление — только стикеров этой же страницы
        if (json.Contains("\"sticky:save\"") || json.Contains("\"sticky:del\""))
        {
            if (Incognito) return;
            using var sd = JsonDocument.Parse(json);
            var r = sd.RootElement;
            var key = Store.PageKey(core.Source);
            var id = r.TryGetProperty("id", out var iv) ? iv.GetString() ?? "" : "";
            if (r.GetProperty("type").GetString() == "sticky:del") Stickies.Delete(key, id);
            else Stickies.Update(key, id, r.TryGetProperty("text", out var tv) ? tv.GetString() ?? "" : "",
                                 r.TryGetProperty("x", out var xv) && xv.TryGetInt32(out var x) ? x : 0, r.TryGetProperty("y", out var yv) && yv.TryGetInt32(out var y) ? y : 0);
            return;
        }

        // Жесты мышью (не чаще раза в 400 мс — на случай, если страница сама шлёт такие сообщения)
        if (json.Contains("\"gesture\""))
        {
            if (!Cfg.MouseGestures || tab != active || (DateTime.Now - lastGesture).TotalMilliseconds < 400) return;
            lastGesture = DateTime.Now;
            using var gd = JsonDocument.Parse(json);
            var g = gd.RootElement.TryGetProperty("g", out var gv) ? gv.GetString() ?? "" : "";
            BeginInvoke(() =>
            {
                switch (g)
                {
                    case "L": core.GoBack(); break;
                    case "R": core.GoForward(); break;
                    case "U": _ = AddTab(homeUrl); break;
                    case "D": core.Reload(); break;
                    case "DR": CloseTab(tab); break;
                    case "UD": ReopenClosedTab(); break;
                    case "UL": { int i = tabs.IndexOf(tab); if (tabs.Count > 1) SelectTab(tabs[(i - 1 + tabs.Count) % tabs.Count]); break; }
                    case "UR": { int i = tabs.IndexOf(tab); if (tabs.Count > 1) SelectTab(tabs[(i + 1) % tabs.Count]); break; }
                }
            });
            return;
        }

        // Блокировщик: страница просит правила скрытия рекламы для своего сайта
        if (json.Contains("\"ab:css\""))
        {
            var host = new Uri(origin).Host;
            var script = !Cfg.BlockAds || !Cfg.CosmeticFiltering || SiteAllowed(host) ? AdScripts.Disable : AdBlock.PageScript(host);
            _ = core.ExecuteScriptAsync(script);
            return;
        }

        if (!Cfg.PasswordManager || Untrusted(new Uri(origin).Host)) return; // ненадёжным сайтам пароли не даём
        bool secure = origin.StartsWith("https://") || Uri.TryCreate(origin, UriKind.Absolute, out var lu) && lu.IsLoopback;
        if (!secure) return; // по незащищённому соединению пароли не сохраняем

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        string Str(string name, int max) =>
            root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String && (v.GetString()?.Length ?? 0) <= max ? v.GetString() ?? "" : "";

        switch (Str("type", 20))
        {
            case "pw:form":
                tab.HasLoginForm = true;
                if (tab == active) UpdateKey();
                break;

            case "pw:user":
                var u = Str("username", 256).Trim();
                if (u.Length > 0) tab.LastUser = (origin, u);
                break;

            case "pw:capture":
                if (Incognito) return; // в инкогнито ничего не запоминаем
                var pass = Str("password", 512);
                var user = Str("username", 256).Trim();
                if (pass.Length == 0) return;
                if (user.Length == 0 && tab.LastUser is { } lu2 && PasswordTools.HostKey(lu2.origin) == PasswordTools.HostKey(origin)) user = lu2.user;
                if (Cfg.NeverSave.Contains(PasswordTools.HostKey(origin))) return;
                if (Vault.Unlocked && Vault.ForOrigin(origin).Any(x => x.Username == user && x.Password == pass)) return;
                pendingCapture = (origin, user, pass, DateTime.Now);
                if (tab == active)
                {
                    UpdateKey();
                    passwordPopup.ShowFor(address, origin, user, pass);
                }
                break;
        }
    }

    async Task<bool> EnsureUnlocked()
    {
        if (Vault.Unlocked) return true;
        if (!Vault.Exists) { OpenLitePage(LitePages.Passwords); return false; }
        while (true)
        {
            var m = InputDialog.AskPassword(this, "Мастер-пароль менеджера паролей");
            if (m == null) return false;
            var (ok, err) = await Task.Run(() => Vault.Unlock(m));
            if (ok) return true;
            MessageBox.Show(this, err, "Менеджер паролей", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    async Task ShowKeyMenu()
    {
        var origin = ActiveOrigin;
        if (origin == null || active?.Core == null) return;
        if (PendingFor(origin) && pendingCapture is { } p)
        {
            passwordPopup.ShowFor(address, p.origin, p.user, p.pass);
            return;
        }
        if (!await EnsureUnlocked()) return;
        var core = active.Core;
        var list = Vault.ForOrigin(origin);

        var menu = Theme.NewMenu();
        menu.Items.Add(new ToolStripMenuItem(L.T("Пароли для " + PasswordTools.Host(origin))) { Enabled = false, Padding = new Padding(6, 5, 6, 3) });
        if (list.Count == 0)
            menu.Items.Add(new ToolStripMenuItem(L.T("Сохранённых паролей нет")) { Enabled = false, Padding = new Padding(6, 4, 6, 4) });
        foreach (var e in list)
        {
            var entry = e;
            var label = (entry.Username.Length > 0 ? entry.Username : "(без логина)") + "   ••••••••";
            menu.Items.Add(Theme.Item(label, () => FillEntry(core, entry)));
        }
        var codes = Vault.Codes();
        foreach (var e in list.Where(x => codes.ContainsKey(x.Id)))
        {
            var (code, _) = codes[e.Id];
            var c = code;
            menu.Items.Add(Theme.Item($"Вставить код 2FA {c[..3]} {c[3..]}" + (list.Count > 1 ? $" ({e.Username})" : ""), async () =>
            {
                var r = await core.ExecuteScriptAsync(Totp.FillScript(c));
                if (r.Contains("none")) { PasswordTools.CopySecret(c); MessageBox.Show(this, "Поле для кода не найдено — код скопирован в буфер обмена.", "Код 2FA"); }
            }));
        }
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(Theme.Item("Вставить новый надёжный пароль", async () =>
        {
            var pw = PasswordTools.Generate();
            var n = await core.ExecuteScriptAsync(PasswordScripts.FillGenerated(pw));
            if (n == "0") { PasswordTools.CopySecret(pw); MessageBox.Show(this, "Пустых полей пароля не нашлось. Пароль скопирован в буфер обмена (очистится через 30 секунд).", "Генератор паролей"); }
        }));
        if (list.Count > 0)
            menu.Items.Add(Theme.Item("Скопировать пароль", () => PasswordTools.CopySecret(list[0].Password)));
        menu.Items.Add(Theme.Item("Открыть менеджер паролей", () => OpenLitePage(LitePages.Passwords)));
        var r = RectangleToClient(address.KeyScreenRect);
        menu.Show(this, new Point(r.Right - menu.PreferredSize.Width, r.Bottom + Theme.S(6)));
    }

    async void FillEntry(CoreWebView2 core, PasswordEntry entry)
    {
        // Перед заполнением ещё раз проверяем, что страница всё та же
        var now = PasswordTools.Origin(core.Source);
        if (now == null || PasswordTools.HostKey(now) != PasswordTools.HostKey(entry.Origin)) return;
        try { await core.ExecuteScriptAsync(PasswordScripts.Fill(entry.Username, entry.Password)); } catch { }
    }

    object VaultState() => new
    {
        type = "pwState",
        exists = Vault.Exists,
        unlocked = Vault.Unlocked,
        count = Vault.Unlocked ? Vault.Count : 0,
        enabled = Cfg.PasswordManager,
        autoLock = Cfg.AutoLockMinutes,
        never = Cfg.NeverSave,
    };

    static object VaultList()
    {
        var all = Vault.All();
        var reuse = all.GroupBy(e => e.Password).Where(g => g.Count() > 1).SelectMany(g => g).Select(e => e.Id).ToHashSet();
        static bool Weak(string p) => p.Length < 10 || p.All(char.IsLetter) || p.All(char.IsDigit) ||
                                      new[] { "password", "qwerty", "123456", "111111", "пароль", "admin" }.Any(w => p.Contains(w, StringComparison.OrdinalIgnoreCase));
        return new
        {
            type = "pwList",
            items = all.OrderBy(e => PasswordTools.HostKey(e.Origin)).ThenBy(e => e.Username).Select(e => new
            {
                id = e.Id, origin = e.Origin, host = PasswordTools.HostKey(e.Origin), username = e.Username,
                updated = e.Updated.ToString("dd.MM.yyyy"), weak = Weak(e.Password), reused = reuse.Contains(e.Id), length = e.Password.Length,
                hasTotp = e.Totp.Length > 0,
            }),
        };
    }

    async Task HandleVaultMessage(CoreWebView2 core, string type, JsonElement root)
    {
        string Str(string name) => root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
        switch (type)
        {
            case "pw:state": // не продлевает автоблокировку
                Post(core, VaultState());
                break;
            case "pw:create":
                var cm = Str("master");
                await Task.Run(() => Vault.Create(cm));
                Post(core, VaultState());
                Post(core, VaultList());
                break;
            case "pw:unlock":
                var um = Str("master");
                var (ok, err) = await Task.Run(() => Vault.Unlock(um));
                if (!ok) { Post(core, new { type = "pwError", text = err }); break; }
                Post(core, VaultState());
                Post(core, VaultList());
                break;
            case "pw:lock":
                Vault.Lock();
                Post(core, VaultState());
                break;
            case "pw:list":
                if (Vault.Unlocked) Post(core, VaultList());
                break;
            case "pw:reveal":
                var re = Vault.Get(Str("id"));
                if (re != null) Post(core, new { type = "pwReveal", id = re.Id, password = re.Password, totp = re.Totp });
                break;
            case "pw:codes": // не продлевает автоблокировку
                if (!Vault.Unlocked) break;
                Post(core, new { type = "pwCodes", codes = Vault.Codes().ToDictionary(k => k.Key, v => new { code = v.Value.code, left = v.Value.left }) });
                break;
            case "pw:copyCode":
                if (Vault.Codes().TryGetValue(Str("id"), out var cc))
                {
                    PasswordTools.CopySecret(cc.code);
                    Post(core, new { type = "done", text = "Код 2FA скопирован" });
                }
                break;
            case "pw:breach":
                Post(core, new { type = "done", text = "Проверяю пароли по базе утечек…" });
                try
                {
                    var found = await BreachCheck.CheckAsync(Vault.All());
                    Post(core, new { type = "pwBreach", found });
                }
                catch (Exception ex) { Post(core, new { type = "pwError", text = "Не удалось проверить: " + ex.Message }); }
                break;
            case "pw:change":
            {
                // Стандартный адрес смены пароля (/.well-known/change-password): сайты, которые его поддерживают, перенаправят на нужную страницу
                var che = Vault.Get(Str("id"));
                if (che == null || !Uri.TryCreate(che.Origin, UriKind.Absolute, out var cho) || cho.Scheme != "https") break;
                var newPw = PasswordTools.Generate(20);
                PasswordTools.CopySecret(newPw);
                _ = AddTab(cho.GetLeftPart(UriPartial.Authority) + "/.well-known/change-password");
                Post(core, new { type = "done", text = L.T("Новый надёжный пароль скопирован (буфер очистится через 30 секунд). Вставьте его на странице смены — браузер предложит обновить сохранённый") });
                break;
            }
            case "pw:copy":
                var ce = Vault.Get(Str("id"));
                if (ce == null) break;
                if (Str("field") == "user") Clipboard.SetText(ce.Username.Length > 0 ? ce.Username : " ");
                else PasswordTools.CopySecret(ce.Password);
                Post(core, new { type = "done", text = Str("field") == "user" ? "Логин скопирован" : "Пароль скопирован, буфер очистится через 30 секунд" });
                break;
            case "pw:save":
                var url = Str("url").Trim();
                var origin = PasswordTools.Origin(url.Contains("://") ? url : "https://" + url);
                if (origin == null) { Post(core, new { type = "pwError", text = "Укажите адрес сайта, например vk.com" }); break; }
                if (Str("password").Length == 0) { Post(core, new { type = "pwError", text = "Пароль не может быть пустым" }); break; }
                var sid = Str("id");
                var totpIn = Str("totp").Trim();
                if (totpIn.Length > 0 && Totp.Parse(totpIn) == null) { Post(core, new { type = "pwError", text = "Не похоже на ключ 2FA. Вставьте секретный ключ (буквы и цифры) или ссылку otpauth://" }); break; }
                Vault.Upsert(sid.Length > 0 ? sid : null, origin, Str("username").Trim(), Str("password"), totpIn);
                Post(core, VaultList());
                Post(core, new { type = "done", text = "Сохранено" });
                break;
            case "pw:delete":
                Vault.Delete(Str("id"));
                Post(core, VaultList());
                Post(core, new { type = "done", text = "Удалено" });
                break;
            case "pw:generate":
                int len = root.TryGetProperty("length", out var lv) && lv.TryGetInt32(out var li) ? li : 20;
                bool sym = !root.TryGetProperty("symbols", out var sv) || sv.ValueKind != JsonValueKind.False;
                Post(core, new { type = "pwGenerated", password = PasswordTools.Generate(len, sym) });
                break;
            case "pw:changeMaster":
                var om = Str("old"); var nm = Str("new");
                try
                {
                    await Task.Run(() => Vault.ChangeMaster(om, nm));
                    Post(core, new { type = "done", text = "Мастер-пароль изменён" });
                }
                catch (Exception ex) { Post(core, new { type = "pwError", text = ex.Message }); }
                Post(core, VaultState());
                break;
            case "pw:importCsv":
                BeginInvoke(() =>
                {
                    using var dlg = new OpenFileDialog { Title = "Файл паролей (CSV) из Chrome, Edge, Яндекса, Opera или Firefox", Filter = "CSV-файл|*.csv" };
                    if (dlg.ShowDialog(this) != DialogResult.OK) return;
                    try
                    {
                        if (new FileInfo(dlg.FileName).Length > 20_000_000) throw new IOException("Файл слишком большой");
                        int n = Vault.ImportMany(PasswordTools.ParseCsv(File.ReadAllText(dlg.FileName)));
                        Post(core, VaultList());
                        Post(core, new { type = "pwImported", count = n, file = dlg.FileName });
                    }
                    catch (Exception ex) { Post(core, new { type = "pwError", text = ex.Message }); }
                });
                break;
            case "pw:browsers":
                Post(core, new { type = "pwBrowsers", items = Importer.Scan().Where(PasswordImport.Supported).Select(p => new { id = p.Id, browser = p.Browser, profile = p.Profile }) });
                break;
            case "pw:importBrowser":
            {
                var id = root.TryGetProperty("id", out var bid) ? bid.GetString() ?? "" : "";
                var prof = Importer.Scan().FirstOrDefault(p => p.Id == id);
                if (prof == null || !PasswordImport.Supported(prof)) { Post(core, new { type = "pwError", text = L.T("Профиль не найден") }); break; }
                try
                {
                    var res = await Task.Run(() => PasswordImport.Read(prof));
                    int n = Vault.ImportMany(res.Items);
                    Post(core, VaultList());
                    Post(core, new { type = "pwBrowserImported", count = n, found = res.Items.Count, note = res.Note, browser = prof.Browser });
                }
                catch (Exception ex) { Post(core, new { type = "pwError", text = ex.Message }); }
                break;
            }
            case "pw:exportCsv":
                BeginInvoke(async () =>
                {
                    // Экспорт — только после повторного ввода мастер-пароля
                    var m = InputDialog.AskPassword(this, "Мастер-пароль для экспорта");
                    if (m == null) return;
                    var (okM, errM) = await Task.Run(() => Vault.Unlock(m));
                    if (!okM) { Post(core, new { type = "pwError", text = errM }); return; }
                    using var dlg = new SaveFileDialog
                    {
                        Title = "Экспорт паролей (формат Bitwarden)",
                        Filter = "CSV-файл|*.csv",
                        FileName = $"LiteBrowser-пароли-{DateTime.Now:yyyy-MM-dd}.csv",
                    };
                    if (dlg.ShowDialog(this) != DialogResult.OK) return;
                    File.WriteAllText(dlg.FileName, PasswordTools.ExportCsv(Vault.All()), new System.Text.UTF8Encoding(false));
                    Post(core, new { type = "pwExported", file = dlg.FileName, count = Vault.Count });
                });
                break;
            case "pw:deleteFile":
                var path = Str("file");
                if (path.EndsWith(".csv", StringComparison.OrdinalIgnoreCase) && File.Exists(path))
                {
                    File.Delete(path);
                    Post(core, new { type = "done", text = "CSV-файл удалён" });
                }
                break;
            case "pw:neverRemove":
                var host = Str("host");
                CopyAndSave(x => x.NeverSave.Remove(host));
                Post(core, VaultState());
                break;
        }
    }

}
