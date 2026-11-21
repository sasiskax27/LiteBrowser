using System.Text.Json;
using System.Web;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace LiteBrowser;

sealed class MainForm : Form
{
    const string Version = "1.1";
    const string Host = "lite.home";
    const string HomeUrl = "https://lite.home/home.html";
    const string SettingsUrl = "https://lite.home/settings.html";

    // Рекламные и следящие домены — запросы к ним не уходят в сеть вообще.
    static readonly string[] BlockedHosts =
    {
        "doubleclick.net", "googlesyndication.com", "googleadservices.com", "google-analytics.com",
        "googletagmanager.com", "googletagservices.com", "adservice.google.com",
        "adnxs.com", "adsrvr.org", "criteo.com", "criteo.net", "taboola.com", "outbrain.com",
        "scorecardresearch.com", "quantserve.com", "moatads.com", "amazon-adsystem.com", "pubmatic.com",
        "rubiconproject.com", "openx.net", "casalemedia.com", "advertising.com", "adform.net",
        "hotjar.com", "mouseflow.com", "fullstory.com", "mixpanel.com", "segment.io", "branch.io",
        "connect.facebook.net", "ads-twitter.com", "analytics.tiktok.com",
        "mc.yandex.ru", "an.yandex.ru", "yabs.yandex.ru", "top-fwz1.mail.ru", "ad.mail.ru",
        "counter.yadro.ru", "adfox.ru", "adriver.ru", "popads.net", "propellerads.com", "exoclick.com",
    };

    readonly List<BrowserTab> tabs = new();
    readonly TabStrip strip;
    readonly BufferedPanel toolbar = new() { Dock = DockStyle.Top };
    readonly BufferedPanel content = new() { Dock = DockStyle.Fill };
    readonly AddressBox address = new();
    readonly IconButton back, forward, reload, home, cleanBtn, settingsBtn;
    readonly ToolTip tips = new() { InitialDelay = 500 };
    readonly System.Windows.Forms.Timer anim = new() { Interval = 16 };
    readonly HashSet<string> httpAllowed = new(StringComparer.OrdinalIgnoreCase);
    readonly HashSet<string> upgraded = new(StringComparer.OrdinalIgnoreCase);
    readonly string dataDir;
    AppSettings settings;
    CoreWebView2Environment? env;
    BrowserTab? active;
    FormWindowState stateBeforeFullscreen;
    float phase;

    readonly string? startArg;

    public MainForm(string? startArg = null)
    {
        this.startArg = startArg;
        Theme.Dpi = DeviceDpi / 96f;
        dataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LiteBrowser");
        settings = AppSettings.Load(dataDir);

        Text = "LiteBrowser";
        Icon = Theme.MakeAppIcon();
        BackColor = Theme.Frame;
        Size = new Size(Theme.S(1280), Theme.S(820));
        MinimumSize = new Size(Theme.S(560), Theme.S(380));
        StartPosition = FormStartPosition.CenterScreen;
        KeyPreview = true;

        strip = new TabStrip(tabs) { Dock = DockStyle.Top, Height = Theme.S(44) };
        strip.TabSelected += SelectTab;
        strip.TabClosed += CloseTab;
        strip.NewTab += () => _ = AddTab(HomeUrl);

        toolbar.Height = Theme.S(50);
        toolbar.BackColor = Theme.Toolbar;
        content.BackColor = Theme.Frame;

        back = new IconButton("", "Назад (Alt+←)", tips);
        forward = new IconButton("", "Вперёд (Alt+→)", tips);
        reload = new IconButton("", "Обновить (F5)", tips);
        home = new IconButton("", "Домашняя страница", tips);
        cleanBtn = new IconButton("", "Удалить историю, cookies и кэш", tips);
        settingsBtn = new IconButton("", "Настройки", tips);

        back.Click += (_, _) => active?.Core?.GoBack();
        forward.Click += (_, _) => active?.Core?.GoForward();
        reload.Click += (_, _) => ReloadOrStop();
        home.Click += (_, _) => active?.Core?.Navigate(HomeUrl);
        cleanBtn.Click += async (_, _) => await ClearDataWithConfirm();
        settingsBtn.Click += (_, _) => OpenSettings();

        address.Box.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Escape) { e.SuppressKeyPress = true; UpdateUi(force: true); active?.View.Focus(); return; }
            if (e.KeyCode != Keys.Enter) return;
            e.SuppressKeyPress = true;
            active?.Core?.Navigate(ToUrl(address.Box.Text));
            active?.View.Focus();
        };
        address.Box.Enter += (_, _) => BeginInvoke(address.Box.SelectAll);

        toolbar.Controls.AddRange(new Control[] { back, forward, reload, home, address, cleanBtn, settingsBtn });
        toolbar.Resize += (_, _) => LayoutToolbar();
        toolbar.Paint += PaintProgress;

        Controls.Add(content);
        Controls.Add(toolbar);
        Controls.Add(strip);

        anim.Tick += (_, _) => Animate();
        Load += async (_, _) => await Init();
        UpdatePlaceholder();
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        try { Theme.StyleTitleBar(Handle); } catch { }
    }

    void LayoutToolbar()
    {
        int s = Theme.S(36), y = (toolbar.Height - s) / 2, x = Theme.S(10);
        foreach (var b in new[] { back, forward, reload, home }) { b.SetBounds(x, y, s, s); x += s + Theme.S(2); }
        int right = toolbar.Width - Theme.S(10);
        foreach (var b in new[] { settingsBtn, cleanBtn }) { right -= s; b.SetBounds(right, y, s, s); right -= Theme.S(2); }
        int ah = Theme.S(36), ax = x + Theme.S(8);
        address.SetBounds(ax, (toolbar.Height - ah) / 2, Math.Max(Theme.S(120), right - Theme.S(8) - ax), ah);
    }

    // ---------- Анимация загрузки ----------

    void StartAnim() { if (!anim.Enabled) anim.Start(); }

    void Animate()
    {
        phase += 0.011f;
        strip.Spin = (strip.Spin + 9) % 360;
        strip.Invalidate();
        toolbar.Invalidate(new Rectangle(0, toolbar.Height - Theme.S(3), toolbar.Width, Theme.S(3)));
        if (tabs.Any(t => t.Loading)) return;
        anim.Stop();
        strip.Invalidate();
        toolbar.Invalidate();
    }

    void PaintProgress(object? sender, PaintEventArgs e)
    {
        if (active?.Loading != true) return;
        int w = toolbar.Width, h = Theme.S(2);
        float seg = w * 0.28f, x = (phase % 1.28f - 0.28f) * w;
        using var br = new System.Drawing.Drawing2D.LinearGradientBrush(
            new RectangleF(x - 1, 0, seg + 2, h), Theme.Accent, Theme.Accent2, 0f);
        e.Graphics.FillRectangle(br, x, toolbar.Height - h, seg, h);
    }

    // ---------- Вкладки ----------

    async Task Init()
    {
        try
        {
            var options = new CoreWebView2EnvironmentOptions { EnableTrackingPrevention = true };
            env = await CoreWebView2Environment.CreateAsync(null, dataDir, options);
            await AddTab(string.IsNullOrWhiteSpace(startArg) ? HomeUrl : ToUrl(startArg));
        }
        catch (WebView2RuntimeNotFoundException)
        {
            MessageBox.Show("Не найден компонент WebView2 Runtime. Установите его с сайта Microsoft.", "LiteBrowser",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
            Close();
        }
    }

    async Task<BrowserTab> AddTab(string? url)
    {
        var view = new WebView2 { Dock = DockStyle.Fill, DefaultBackgroundColor = Theme.Frame, Visible = false };
        var tab = new BrowserTab { View = view, IsHome = url == HomeUrl };
        tabs.Add(tab);
        content.Controls.Add(view);
        SelectTab(tab);

        await view.EnsureCoreWebView2Async(env);
        Configure(tab);
        if (url != null) tab.Core!.Navigate(url);
        if (tab == active) { view.Focus(); UpdateUi(); }
        return tab;
    }

    void SelectTab(BrowserTab tab)
    {
        active = tab;
        foreach (var t in tabs)
        {
            bool on = t == tab;
            t.View.Visible = on;
            if (t.Core == null) continue;
            // Фоновые вкладки — в режим экономии памяти
            try { t.Core.MemoryUsageTargetLevel = on ? CoreWebView2MemoryUsageTargetLevel.Normal : CoreWebView2MemoryUsageTargetLevel.Low; }
            catch { }
        }
        strip.Selected = tab;
        if (tab.Core != null) tab.View.Focus();
        UpdateUi(force: true);
    }

    void CloseTab(BrowserTab tab)
    {
        if (tabs.Count == 1) { Close(); return; }
        int idx = tabs.IndexOf(tab);
        tabs.Remove(tab);
        content.Controls.Remove(tab.View);
        tab.View.Dispose();
        tab.Icon?.Dispose();
        if (tab == active || active == null) SelectTab(tabs[Math.Min(idx, tabs.Count - 1)]);
        else strip.Invalidate();
    }

    void CycleTab(int dir)
    {
        if (active == null || tabs.Count < 2) return;
        int i = (tabs.IndexOf(active) + dir + tabs.Count) % tabs.Count;
        SelectTab(tabs[i]);
    }

    void OpenSettings()
    {
        var t = tabs.FirstOrDefault(x => x.Core?.Source?.StartsWith(SettingsUrl) == true);
        if (t != null) SelectTab(t); else _ = AddTab(SettingsUrl);
    }

    // ---------- Настройка движка ----------

    void Configure(BrowserTab tab)
    {
        var core = tab.Core!;

        // Безопасность
        var s = core.Settings;
        s.IsPasswordAutosaveEnabled = false;   // не хранить пароли
        s.IsGeneralAutofillEnabled = false;    // не хранить данные форм
        s.IsReputationCheckingRequired = true; // SmartScreen: защита от фишинга и вирусов
        s.AreHostObjectsAllowed = false;       // сайты не получают доступ к программе
        s.IsWebMessageEnabled = true;          // нужно своим страницам; сообщения чужих сайтов игнорируются
        s.IsStatusBarEnabled = true;
        core.Profile.PreferredTrackingPreventionLevel = CoreWebView2TrackingPreventionLevel.Strict;
        core.Profile.PreferredColorScheme = ColorScheme();

        // Свои страницы (старт и настройки) живут на виртуальном адресе lite.home
        core.SetVirtualHostNameToFolderMapping(Host, Path.Combine(AppContext.BaseDirectory, "wwwroot"),
            CoreWebView2HostResourceAccessKind.Deny);

        // Блокировка рекламы и трекеров
        foreach (var h in BlockedHosts)
        {
            core.AddWebResourceRequestedFilter($"*://{h}/*", CoreWebView2WebResourceContext.All, CoreWebView2WebResourceRequestSourceKinds.All);
            core.AddWebResourceRequestedFilter($"*://*.{h}/*", CoreWebView2WebResourceContext.All, CoreWebView2WebResourceRequestSourceKinds.All);
        }
        core.WebResourceRequested += (_, e) =>
        {
            if (!settings.BlockAds) return;
            e.Response = env!.CreateWebResourceResponse(null, 403, "Blocked", "");
            tab.Blocked++;
            if (tab == active) address.SetBlocked(tab.Blocked);
        };

        core.NavigationStarting += (_, e) =>
        {
            if (Uri.TryCreate(e.Uri, UriKind.Absolute, out var u))
            {
                // Поиск со стартовой страницы
                if (u.Host == Host && u.AbsolutePath == "/search")
                {
                    e.Cancel = true;
                    var q = HttpUtility.ParseQueryString(u.Query)["q"] ?? "";
                    BeginInvoke(() => core.Navigate(ToUrl(q)));
                    return;
                }
                // Только HTTPS
                if (settings.HttpsOnly && u.Scheme == Uri.UriSchemeHttp && !u.IsLoopback && !httpAllowed.Contains(u.Host))
                {
                    e.Cancel = true;
                    upgraded.Add(u.Host);
                    var b = new UriBuilder(u) { Scheme = Uri.UriSchemeHttps, Port = u.IsDefaultPort ? -1 : u.Port };
                    BeginInvoke(() => core.Navigate(b.Uri.ToString()));
                    return;
                }
            }
            tab.Loading = true;
            tab.Blocked = 0;
            StartAnim();
            if (tab == active) UpdateUi();
            strip.Invalidate();
        };

        core.NavigationCompleted += (_, e) =>
        {
            tab.Loading = false;
            if (tab == active) UpdateUi();
            strip.Invalidate();
            if (e.IsSuccess || e.WebErrorStatus == CoreWebView2WebErrorStatus.OperationCanceled) return;
            if (!Uri.TryCreate(core.Source, UriKind.Absolute, out var u)) return;
            if (u.Scheme != Uri.UriSchemeHttps || !upgraded.Contains(u.Host) || httpAllowed.Contains(u.Host)) return;
            BeginInvoke(() =>
            {
                var r = MessageBox.Show(
                    $"Сайт {u.Host} не поддерживает защищённое соединение (HTTPS).\n\n" +
                    "Данные на нём могут перехватить. Всё равно открыть?",
                    "Небезопасный сайт", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2);
                if (r != DialogResult.Yes) return;
                httpAllowed.Add(u.Host);
                core.Navigate("http://" + u.Host + u.PathAndQuery);
            });
        };

        // Разрешения: уведомления запрещены, остальное — только с согласия
        core.PermissionRequested += (_, e) =>
        {
            if (e.PermissionKind == CoreWebView2PermissionKind.Notifications)
            {
                e.State = CoreWebView2PermissionState.Deny;
                return;
            }
            var what = e.PermissionKind switch
            {
                CoreWebView2PermissionKind.Camera => "камере",
                CoreWebView2PermissionKind.Microphone => "микрофону",
                CoreWebView2PermissionKind.Geolocation => "вашему местоположению",
                CoreWebView2PermissionKind.ClipboardRead => "буферу обмена",
                CoreWebView2PermissionKind.MultipleAutomaticDownloads => "загрузке нескольких файлов",
                _ => e.PermissionKind.ToString(),
            };
            var host = Uri.TryCreate(e.Uri, UriKind.Absolute, out var u) ? u.Host : e.Uri;
            var deferral = e.GetDeferral();
            BeginInvoke(() =>
            {
                var r = MessageBox.Show($"Сайт {host} просит доступ к {what}.\nРазрешить?", "Разрешение",
                    MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2);
                e.State = r == DialogResult.Yes ? CoreWebView2PermissionState.Allow : CoreWebView2PermissionState.Deny;
                deferral.Complete();
            });
        };

        // Всплывающие окна — во вкладках
        core.NewWindowRequested += async (_, e) =>
        {
            var deferral = e.GetDeferral();
            var t = await AddTab(null);
            e.NewWindow = t.Core;
            e.Handled = true;
            deferral.Complete();
        };

        // Сообщения принимаются только от своих страниц
        core.WebMessageReceived += async (_, e) =>
        {
            if (!e.Source.StartsWith("https://" + Host + "/", StringComparison.Ordinal)) return;
            try { await HandleMessage(core, e.WebMessageAsJson); } catch { }
        };

        core.DocumentTitleChanged += (_, _) =>
        {
            tab.Title = string.IsNullOrWhiteSpace(core.DocumentTitle) ? "Новая вкладка" : core.DocumentTitle;
            strip.Invalidate();
            if (tab == active) UpdateTitle();
        };

        core.FaviconChanged += async (_, _) =>
        {
            Image? icon = null;
            try
            {
                using var stream = await core.GetFaviconAsync(CoreWebView2FaviconImageFormat.Png);
                using var ms = new MemoryStream();
                if (stream != null) await stream.CopyToAsync(ms);
                if (ms.Length > 0) { ms.Position = 0; using var img = Image.FromStream(ms); icon = new Bitmap(img); }
            }
            catch { }
            var old = tab.Icon;
            tab.Icon = icon;
            old?.Dispose();
            strip.Invalidate();
        };

        core.SourceChanged += (_, _) =>
        {
            tab.IsHome = core.Source.StartsWith(HomeUrl, StringComparison.Ordinal);
            if (tab == active) UpdateUi(force: true);
            strip.Invalidate();
        };
        core.HistoryChanged += (_, _) => { if (tab == active) UpdateUi(); };
        core.WindowCloseRequested += (_, _) => CloseTab(tab);
        core.ContainsFullScreenElementChanged += (_, _) => SetFullscreen(core.ContainsFullScreenElement);
    }

    async Task HandleMessage(CoreWebView2 core, string json)
    {
        using var doc = JsonDocument.Parse(json);
        var type = doc.RootElement.TryGetProperty("type", out var t) ? t.GetString() : null;
        switch (type)
        {
            case "get":
                core.PostWebMessageAsJson(JsonSerializer.Serialize(new
                {
                    type = "settings",
                    settings,
                    version = Version,
                    browserVersion = env?.BrowserVersionString ?? "",
                    engines = SearchEngines.All.Select(x => new { id = x.Id, name = x.Name, desc = x.Description, @private = x.Private }),
                }, AppSettings.Json));
                break;

            case "save":
                var ns = doc.RootElement.GetProperty("settings").Deserialize<AppSettings>(AppSettings.Json);
                if (ns == null) return;
                settings = ns.Normalize();
                settings.Save(dataDir);
                ApplySettings();
                core.PostWebMessageAsJson("{\"type\":\"saved\"}");
                break;

            case "clear":
                await core.Profile.ClearBrowsingDataAsync();
                core.PostWebMessageAsJson("{\"type\":\"cleared\"}");
                break;
        }
    }

    CoreWebView2PreferredColorScheme ColorScheme() => settings.SiteTheme switch
    {
        "dark" => CoreWebView2PreferredColorScheme.Dark,
        "light" => CoreWebView2PreferredColorScheme.Light,
        _ => CoreWebView2PreferredColorScheme.Auto,
    };

    void ApplySettings()
    {
        foreach (var t in tabs)
            if (t.Core != null) t.Core.Profile.PreferredColorScheme = ColorScheme();
        UpdatePlaceholder();
    }

    void UpdatePlaceholder() =>
        address.Box.PlaceholderText = $"Поиск в {SearchEngines.Get(settings.Engine).Name} или адрес сайта";

    void SetFullscreen(bool on)
    {
        if (on)
        {
            stateBeforeFullscreen = WindowState;
            strip.Visible = toolbar.Visible = false;
            FormBorderStyle = FormBorderStyle.None;
            WindowState = FormWindowState.Normal;
            WindowState = FormWindowState.Maximized;
        }
        else
        {
            FormBorderStyle = FormBorderStyle.Sizable;
            strip.Visible = toolbar.Visible = true;
            WindowState = stateBeforeFullscreen;
        }
    }

    // ---------- Интерфейс ----------

    void UpdateUi(bool force = false)
    {
        var core = active?.Core;
        var src = core?.Source ?? "";
        back.Enabled = core?.CanGoBack ?? false;
        forward.Enabled = core?.CanGoForward ?? false;
        bool loading = active?.Loading == true;
        reload.Glyph = loading ? "" : "";
        tips.SetToolTip(reload, loading ? "Остановить" : "Обновить (F5)");

        bool isHome = src.StartsWith(HomeUrl, StringComparison.Ordinal) || active?.IsHome == true && src.Length == 0;
        bool isSettings = src.StartsWith(SettingsUrl, StringComparison.Ordinal);
        if (force || !address.Box.Focused)
            address.Box.Text = isHome || src == "about:blank" ? "" : isSettings ? "lite://settings" : src;

        if (isHome) address.SetLock("", Theme.TextDim);
        else if (isSettings) address.SetLock("", Theme.Accent);
        else if (src.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) address.SetLock("", Theme.Secure);
        else if (src.StartsWith("http://", StringComparison.OrdinalIgnoreCase)) address.SetLock("", Theme.Danger);
        else address.SetLock("", Theme.TextDim);

        address.SetBlocked(active?.Blocked ?? 0);
        UpdateTitle();
        toolbar.Invalidate();
    }

    void UpdateTitle()
    {
        var title = active?.Title;
        Text = string.IsNullOrWhiteSpace(title) ? "LiteBrowser" : title + " — LiteBrowser";
    }

    void ReloadOrStop()
    {
        if (active?.Core == null) return;
        if (active.Loading) active.Core.Stop(); else active.Core.Reload();
    }

    async Task ClearDataWithConfirm()
    {
        var core = active?.Core;
        if (core == null) return;
        var r = MessageBox.Show("Удалить историю, cookies, кэш и данные сайтов?\nВы выйдете из всех аккаунтов.",
            "Очистка", MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2);
        if (r != DialogResult.Yes) return;
        await core.Profile.ClearBrowsingDataAsync();
        MessageBox.Show("Готово, данные удалены.", "Очистка");
    }

    string ToUrl(string text)
    {
        var t = text.Trim();
        if (t.Length == 0) return HomeUrl;
        if (t.Equals("lite://settings", StringComparison.OrdinalIgnoreCase)) return SettingsUrl;
        if (t.Equals("lite://home", StringComparison.OrdinalIgnoreCase)) return HomeUrl;
        if (t.StartsWith("localhost", StringComparison.OrdinalIgnoreCase)) return "http://" + t;
        if (Uri.TryCreate(t, UriKind.Absolute, out var u) && (u.Scheme == Uri.UriSchemeHttp || u.Scheme == Uri.UriSchemeHttps))
            return u.ToString();
        if (!t.Contains(' ') && t.Contains('.') && !t.EndsWith('.')) return "https://" + t;
        return SearchEngines.Get(settings.Engine).Url + Uri.EscapeDataString(t);
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        switch (keyData)
        {
            case Keys.Control | Keys.T: _ = AddTab(HomeUrl); return true;
            case Keys.Control | Keys.W: if (active != null) CloseTab(active); return true;
            case Keys.Control | Keys.Tab: CycleTab(1); return true;
            case Keys.Control | Keys.Shift | Keys.Tab: CycleTab(-1); return true;
            case Keys.Control | Keys.L: case Keys.F6: address.Box.Focus(); address.Box.SelectAll(); return true;
            case Keys.Control | Keys.Oemcomma: OpenSettings(); return true;
            case Keys.F5: active?.Core?.Reload(); return true;
            case Keys.Alt | Keys.Left: active?.Core?.GoBack(); return true;
            case Keys.Alt | Keys.Right: active?.Core?.GoForward(); return true;
        }
        return base.ProcessCmdKey(ref msg, keyData);
    }
}
