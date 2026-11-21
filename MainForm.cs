using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Text.Json;
using System.Web;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace LiteBrowser;

/// <summary>Свои страницы браузера живут на виртуальном адресе https://lite.home/ и лежат внутри exe.</summary>
static class LitePages
{
    public const string Host = "lite.home";
    public const string Prefix = "https://lite.home/";
    public const string Home = Prefix + "home.html";
    public const string Settings = Prefix + "settings.html";
    public const string History = Prefix + "history.html";
    public const string Blocked = Prefix + "blocked.html";
    public const string Passwords = Prefix + "passwords.html";

    public static CoreWebView2WebResourceResponse Response(CoreWebView2Environment env, string uri)
    {
        var path = new Uri(uri).AbsolutePath.TrimStart('/');
        if (path.Length == 0) path = "home.html";
        using var s = typeof(LitePages).Assembly.GetManifestResourceStream("wwwroot/" + path);
        if (s == null) return env.CreateWebResourceResponse(null, 404, "Not Found", "");
        var ms = new MemoryStream();
        s.CopyTo(ms);
        ms.Position = 0;
        string type = Path.GetExtension(path) switch
        {
            ".html" => "text/html; charset=utf-8",
            ".js" => "text/javascript; charset=utf-8",
            ".css" => "text/css; charset=utf-8",
            ".svg" => "image/svg+xml",
            _ => "application/octet-stream",
        };
        return env.CreateWebResourceResponse(ms, 200, "OK",
            $"Content-Type: {type}\r\nCache-Control: no-store\r\nX-Content-Type-Options: nosniff");
    }

    /// <summary>Как показать адрес своей страницы в адресной строке.</summary>
    public static string? Display(string src)
    {
        if (src.StartsWith(Home, StringComparison.Ordinal)) return "";
        if (src.StartsWith(Settings, StringComparison.Ordinal)) return "lite://settings";
        if (src.StartsWith(History, StringComparison.Ordinal)) return "lite://history";
        if (src.StartsWith(Passwords, StringComparison.Ordinal)) return "lite://passwords";
        if (src.StartsWith(Blocked, StringComparison.Ordinal))
            return HttpUtility.ParseQueryString(new Uri(src).Query)["url"] ?? "lite://blocked";
        return null;
    }
}

/// <summary>Строка меню масштаба: [−] 100% [+] Сброс.</summary>
sealed class ZoomRow : Control
{
    readonly Func<int> value;
    int hover = -1; // 0 — минус, 1 — плюс, 2 — сброс
    public event Action<int>? Step;
    public event Action? Reset;

    public ZoomRow(Func<int> value)
    {
        this.value = value;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint, true);
        BackColor = Theme.Menu;
        Size = new Size(Theme.S(310), Theme.S(46));
    }

    Rectangle Minus => new(Theme.S(150), (Height - Theme.S(30)) / 2, Theme.S(30), Theme.S(30));
    Rectangle Value => new(Minus.Right, 0, Theme.S(56), Height);
    Rectangle Plus => new(Value.Right, Minus.Y, Theme.S(30), Theme.S(30));
    Rectangle ResetR => new(Plus.Right + Theme.S(6), Minus.Y, Width - Plus.Right - Theme.S(12), Theme.S(30));

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        g.Clear(BackColor);
        var text = Theme.GetFont("Segoe UI", 9.5f);
        TextRenderer.DrawText(g, "Масштаб страницы", text, new Rectangle(Theme.S(12), 0, Theme.S(140), Height), Theme.Text,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter);
        var icon = Theme.GetFont(Theme.IconFont, 9f);
        void Circle(Rectangle r, int i, string glyph)
        {
            using var b = new SolidBrush(hover == i ? Theme.Pressed : Theme.Hover);
            g.FillEllipse(b, r);
            TextRenderer.DrawText(g, glyph, icon, r, Theme.Text, Theme.Center);
        }
        Circle(Minus, 0, "");
        Circle(Plus, 1, "");
        TextRenderer.DrawText(g, value() + "%", Theme.GetFont("Segoe UI", 10.5f, FontStyle.Bold), Value, Theme.Text, Theme.Center);
        TextRenderer.DrawText(g, "Сброс", text, ResetR, hover == 2 ? Theme.Text : Theme.Accent, Theme.Center);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        int h = Minus.Contains(e.Location) ? 0 : Plus.Contains(e.Location) ? 1 : ResetR.Contains(e.Location) ? 2 : -1;
        Cursor = h >= 0 ? Cursors.Hand : Cursors.Default;
        if (h != hover) { hover = h; Invalidate(); }
        base.OnMouseMove(e);
    }

    protected override void OnMouseLeave(EventArgs e) { hover = -1; Invalidate(); base.OnMouseLeave(e); }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left) return;
        if (hover == 0) Step?.Invoke(-1);
        else if (hover == 1) Step?.Invoke(1);
        else if (hover == 2) Reset?.Invoke();
        base.OnMouseDown(e);
    }
}

sealed class MainForm : Form
{
    readonly List<BrowserTab> tabs = new();
    readonly TabStrip strip;
    readonly BufferedPanel toolbar = new() { Dock = DockStyle.Top };
    readonly BookmarksBar bookmarksBar = new() { Dock = DockStyle.Top };
    readonly BufferedPanel content = new() { Dock = DockStyle.Fill };
    readonly AddressBox address = new();
    readonly IconButton back, forward, reload, home, proxyBtn, downloadsBtn, menuBtn;
    readonly ToolTip tips = new() { InitialDelay = 500 };
    readonly System.Windows.Forms.Timer anim = new() { Interval = 16 };
    readonly System.Windows.Forms.Timer freezeTimer = new() { Interval = 30_000 };
    readonly HashSet<string> httpAllowed = new(StringComparer.OrdinalIgnoreCase);
    readonly HashSet<string> upgraded = new(StringComparer.OrdinalIgnoreCase);
    readonly Stack<string> closedTabs = new();
    readonly DownloadsPopup downloadsPopup = new();
    readonly Color frame;
    readonly string homeUrl;
    readonly string? startUrl;
    readonly List<string>? startTabs;
    readonly int startActive;
    BrowserTab? active;
    FormWindowState stateBeforeFullscreen;
    float phase;
    bool runtimeErrorShown;
    int appliedUiScale, appliedPageZoom;
    bool showDownloads, showProxy;
    readonly PasswordPopup passwordPopup = new();
    (string origin, string user, string pass, DateTime time)? pendingCapture;
    readonly IconButton zoomBtn;

    public bool Incognito { get; }

    static AppSettings Cfg => AppEnv.Settings;

    public MainForm(bool incognito, string? startUrl = null, List<string>? startTabs = null, int startActive = 0)
    {
        Incognito = incognito;
        this.startUrl = startUrl;
        this.startTabs = startTabs;
        this.startActive = startActive;
        Theme.SystemDpi = DeviceDpi / 96f;
        Theme.UiScale = Cfg.UiScale / 100f;
        appliedUiScale = Cfg.UiScale;
        appliedPageZoom = Cfg.PageZoom;
        frame = incognito ? Theme.IncognitoFrame : Theme.Frame;
        homeUrl = incognito ? LitePages.Home + "?private=1" : LitePages.Home;

        Text = incognito ? "Инкогнито — LiteBrowser" : "LiteBrowser";
        Icon = Theme.MakeAppIcon();
        BackColor = frame;
        Size = new Size(Theme.S(1280), Theme.S(820));
        MinimumSize = new Size(Theme.S(600), Theme.S(400));
        StartPosition = FormStartPosition.CenterScreen;
        KeyPreview = true;

        strip = new TabStrip(tabs) { Dock = DockStyle.Top, Height = Theme.S(44), FrameColor = frame, Badge = incognito ? "Инкогнито" : null };
        strip.TabSelected += SelectTab;
        strip.TabClosed += CloseTab;
        strip.TabMuteToggled += t => { if (t.Core != null) t.Core.IsMuted = !t.Core.IsMuted; };
        strip.TabMenu += ShowTabMenu;
        strip.NewTab += () => _ = AddTab(homeUrl);

        toolbar.Height = Theme.S(50);
        toolbar.BackColor = Theme.Toolbar;
        bookmarksBar.Height = Theme.S(34);
        bookmarksBar.OpenBookmark += (b, newTab) => { if (newTab) _ = AddTab(b.Url, select: false); else Navigate(b.Url); };
        content.BackColor = frame;

        back = new IconButton("", "Назад (Alt+←)", tips);
        forward = new IconButton("", "Вперёд (Alt+→)", tips);
        reload = new IconButton("", "Обновить (F5)", tips);
        home = new IconButton("", "Домашняя страница", tips);
        proxyBtn = new IconButton("", "Обход блокировок", tips) { GlyphColor = Theme.Secure, Visible = false };
        downloadsBtn = new IconButton("", "Загрузки (Ctrl+J)", tips) { Visible = false };
        menuBtn = new IconButton("", "Меню", tips);
        zoomBtn = new IconButton("100%", "Масштаб страницы и размер интерфейса", tips) { TextMode = true };
        zoomBtn.Click += (_, _) => ShowZoomMenu();

        back.Click += (_, _) => active?.Core?.GoBack();
        forward.Click += (_, _) => active?.Core?.GoForward();
        reload.Click += (_, _) => ReloadOrStop();
        home.Click += (_, _) => Navigate(homeUrl);
        proxyBtn.Click += (_, _) => OpenLitePage(LitePages.Settings, "#proxy");
        downloadsBtn.Click += (_, _) => ToggleDownloads();
        menuBtn.Click += (_, _) => ShowMainMenu();

        address.StarClicked += ToggleBookmark;
        address.KeyClicked += async () => await ShowKeyMenu();
        passwordPopup.OpenManager = () => OpenLitePage(LitePages.Passwords);
        passwordPopup.NeverForSite = origin => { var h = PasswordTools.HostKey(origin); CopyAndSave(x => x.NeverSave.Add(h)); pendingCapture = null; UpdateKey(); };
        passwordPopup.Saved += () => { pendingCapture = null; UpdateKey(); };
        address.Box.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Escape) { e.SuppressKeyPress = true; UpdateUi(force: true); active?.View.Focus(); return; }
            if (e.KeyCode != Keys.Enter) return;
            e.SuppressKeyPress = true;
            Navigate(ToUrl(address.Box.Text));
            active?.View.Focus();
        };
        address.Box.Enter += (_, _) => BeginInvoke(address.Box.SelectAll);

        toolbar.Controls.AddRange(new Control[] { back, forward, reload, home, address, zoomBtn, proxyBtn, downloadsBtn, menuBtn });
        toolbar.Resize += (_, _) => LayoutToolbar();
        toolbar.Paint += PaintProgress;

        Controls.Add(content);
        Controls.Add(bookmarksBar);
        Controls.Add(toolbar);
        Controls.Add(strip);

        anim.Tick += (_, _) => Animate();
        freezeTimer.Tick += async (_, _) => await FreezeIdleTabs();
        freezeTimer.Start();

        AppEnv.SettingsChanged += OnSettingsChanged;
        Store.BookmarksChanged += OnBookmarksChanged;
        Downloads.Changed += OnDownloadsChanged;
        Vault.Changed += OnVaultChanged;
        FormClosed += (_, _) =>
        {
            AppEnv.SettingsChanged -= OnSettingsChanged;
            Store.BookmarksChanged -= OnBookmarksChanged;
            Downloads.Changed -= OnDownloadsChanged;
            Vault.Changed -= OnVaultChanged;
            downloadsPopup.Dispose();
            passwordPopup.Dispose();
        };

        Load += (_, _) => OpenStartTabs();
        Shown += (_, _) => LayoutToolbar();
        ApplyChrome();
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        try { Theme.StyleTitleBar(Handle, frame); } catch { }
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (!Incognito && Cfg.RestoreSession) Store.SaveSession(GetSession());
        base.OnFormClosing(e);
    }

    void LayoutToolbar()
    {
        int s = Theme.S(36), y = (toolbar.Height - s) / 2, x = Theme.S(10);
        foreach (var b in new[] { back, forward, reload, home }) { b.SetBounds(x, y, s, s); x += s + Theme.S(2); }
        int right = toolbar.Width - Theme.S(10);
        foreach (var b in new[] { menuBtn, downloadsBtn, proxyBtn, zoomBtn })
        {
            // Visible до показа окна всегда false, поэтому храним желаемое состояние отдельно
            bool want = b == downloadsBtn ? showDownloads : b == proxyBtn ? showProxy : true;
            b.Visible = want;
            if (!want) continue;
            int w = b == zoomBtn ? Theme.S(58) : s;
            right -= w;
            b.SetBounds(right, y, w, s);
            right -= Theme.S(2);
        }
        int ah = Theme.S(36), ax = x + Theme.S(8);
        address.SetBounds(ax, (toolbar.Height - ah) / 2, Math.Max(Theme.S(120), right - Theme.S(8) - ax), ah);
    }

    // ================= Вкладки =================

    void OpenStartTabs()
    {
        if (startTabs is { Count: > 0 })
        {
            for (int i = 0; i < startTabs.Count; i++)
            {
                var u = startTabs[i];
                string title = u.StartsWith(LitePages.Prefix) ? "Новая вкладка" : Uri.TryCreate(u, UriKind.Absolute, out var x) ? x.Host : u;
                CreateTab(u, select: false, title);
            }
            SelectTab(tabs[Math.Clamp(startActive, 0, tabs.Count - 1)]);
        }
        else
        {
            _ = AddTab(string.IsNullOrWhiteSpace(startUrl) ? homeUrl : ToUrl(startUrl));
        }
    }

    BrowserTab CreateTab(string? url, bool select, string? title = null)
    {
        var view = new WebView2 { Dock = DockStyle.Fill, DefaultBackgroundColor = frame, Visible = false };
        var tab = new BrowserTab { View = view, PendingUrl = url, IsHome = url?.StartsWith(LitePages.Home) == true, Title = title ?? "Новая вкладка" };
        int insertAt = active != null && !select ? tabs.IndexOf(active) + 1 : tabs.Count;
        tabs.Insert(Math.Clamp(insertAt, 0, tabs.Count), tab);
        content.Controls.Add(view);
        if (select) SelectTab(tab); else strip.Invalidate();
        return tab;
    }

    Task InitTab(BrowserTab tab) => tab.InitTask ??= InitTabCore(tab);

    async Task InitTabCore(BrowserTab tab)
    {
        try
        {
            var env = await AppEnv.GetEnvironment();
            if (tab.View.IsDisposed) return;
            if (Incognito)
            {
                var o = env.CreateCoreWebView2ControllerOptions();
                o.IsInPrivateModeEnabled = true;
                o.ProfileName = "Incognito";
                await tab.View.EnsureCoreWebView2Async(env, o);
            }
            else await tab.View.EnsureCoreWebView2Async(env);
            if (tab.View.IsDisposed || tab.Core == null) return;
            Configure(tab, env);
            tab.View.ZoomFactor = Cfg.PageZoom / 100.0;
            tab.View.ZoomFactorChanged += (_, _) => { if (tab == active) UpdateZoomLabel(); };
            var url = tab.PendingUrl;
            tab.PendingUrl = null;
            if (url != null) tab.Core.Navigate(url);
            if (tab == active) { tab.View.Focus(); UpdateUi(force: true); }
        }
        catch (WebView2RuntimeNotFoundException)
        {
            if (runtimeErrorShown) return;
            runtimeErrorShown = true;
            MessageBox.Show(this, "Не найден компонент WebView2 Runtime. Установите его с сайта Microsoft.", "LiteBrowser",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
            Close();
        }
        catch (Exception ex)
        {
            if (runtimeErrorShown) return;
            runtimeErrorShown = true;
            MessageBox.Show(this, "Не удалось запустить движок браузера:\n" + ex.Message, "LiteBrowser",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    async Task<BrowserTab> AddTab(string? url, bool select = true)
    {
        var t = CreateTab(url, select);
        await InitTab(t);
        return t;
    }

    void SelectTab(BrowserTab tab)
    {
        if (active != null && active != tab) active.LastActive = DateTime.Now;
        active = tab;
        tab.LastActive = DateTime.Now;
        foreach (var t in tabs)
        {
            bool on = t == tab;
            t.View.Visible = on;
            if (t.Core == null) continue;
            try { t.Core.MemoryUsageTargetLevel = on ? CoreWebView2MemoryUsageTargetLevel.Normal : CoreWebView2MemoryUsageTargetLevel.Low; }
            catch { }
        }
        if (tab.Frozen && tab.Core != null)
        {
            try { tab.Core.Resume(); } catch { }
            tab.Frozen = false;
        }
        strip.Selected = tab;
        if (tab.Core == null) _ = InitTab(tab);
        else tab.View.Focus();
        UpdateUi(force: true);
    }

    void CloseTab(BrowserTab tab)
    {
        var url = tab.Core?.Source ?? tab.PendingUrl;
        if (url != null && !url.StartsWith(LitePages.Home)) { closedTabs.Push(url); }
        if (tabs.Count == 1) { Close(); return; }
        int idx = tabs.IndexOf(tab);
        tabs.Remove(tab);
        content.Controls.Remove(tab.View);
        tab.View.Dispose();
        tab.Icon?.Dispose();
        if (tab == active || active == null) SelectTab(tabs[Math.Min(idx, tabs.Count - 1)]);
        else strip.Invalidate();
    }

    void ReopenClosedTab()
    {
        if (closedTabs.Count > 0) _ = AddTab(closedTabs.Pop());
    }

    void CycleTab(int dir)
    {
        if (active == null || tabs.Count < 2) return;
        SelectTab(tabs[(tabs.IndexOf(active) + dir + tabs.Count) % tabs.Count]);
    }

    void ShowTabMenu(BrowserTab tab, Point screen)
    {
        var menu = Theme.NewMenu();
        menu.Items.Add(Theme.Item("Новая вкладка", () => _ = AddTab(homeUrl), "Ctrl+T"));
        menu.Items.Add(Theme.Item("Обновить", () => tab.Core?.Reload()));
        menu.Items.Add(Theme.Item("Дублировать", () => { var u = tab.Core?.Source ?? tab.PendingUrl; if (u != null) _ = AddTab(u); }));
        if (tab.Core != null)
            menu.Items.Add(Theme.Item(tab.Core.IsMuted ? "Включить звук" : "Выключить звук", () => tab.Core.IsMuted = !tab.Core.IsMuted));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(Theme.Item("Закрыть", () => CloseTab(tab), "Ctrl+W"));
        menu.Items.Add(Theme.Item("Закрыть другие вкладки", () => { foreach (var t in tabs.Where(x => x != tab).ToList()) CloseTab(t); }));
        menu.Items.Add(Theme.Item("Открыть закрытую вкладку", ReopenClosedTab, "Ctrl+Shift+T"));
        menu.Show(screen);
    }

    public void OpenFromOutside(string? url)
    {
        _ = AddTab(url == null ? homeUrl : ToUrl(url));
        if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
        Activate();
    }

    public Session GetSession() => new()
    {
        Tabs = tabs.Select(t => t.Core?.Source is { Length: > 0 } s && !s.StartsWith(LitePages.Blocked) ? s : t.PendingUrl ?? LitePages.Home).ToList(),
        Active = active == null ? 0 : Math.Max(0, tabs.IndexOf(active)),
    };

    async Task FreezeIdleTabs()
    {
        int min = Cfg.FreezeMinutes;
        if (min <= 0) return;
        foreach (var t in tabs.ToList())
        {
            if (t == active || t.Frozen || t.Core == null || t.Loading) continue;
            if ((DateTime.Now - t.LastActive).TotalMinutes < min) continue;
            try
            {
                if (t.Core.IsDocumentPlayingAudio) continue;
                if (await t.Core.TrySuspendAsync()) { t.Frozen = true; strip.Invalidate(); }
            }
            catch { }
        }
    }

    // ================= Настройка движка =================

    void Configure(BrowserTab tab, CoreWebView2Environment env)
    {
        var core = tab.Core!;

        var s = core.Settings;
        s.IsPasswordAutosaveEnabled = false;   // не хранить пароли
        s.IsGeneralAutofillEnabled = false;    // не хранить данные форм
        s.IsReputationCheckingRequired = true; // SmartScreen: защита от фишинга и вирусов
        s.AreHostObjectsAllowed = false;       // сайты не получают доступ к программе
        s.IsWebMessageEnabled = true;          // нужно своим страницам; сообщения чужих сайтов игнорируются
        s.IsStatusBarEnabled = true;
        core.Profile.PreferredTrackingPreventionLevel = CoreWebView2TrackingPreventionLevel.Strict;
        core.Profile.PreferredColorScheme = ColorScheme();

        core.AddWebResourceRequestedFilter(LitePages.Prefix + "*", CoreWebView2WebResourceContext.All, CoreWebView2WebResourceRequestSourceKinds.All);
        ApplyFilters(tab);

        core.WebResourceRequested += (_, e) =>
        {
            var uri = e.Request.Uri;
            if (uri.StartsWith(LitePages.Prefix, StringComparison.Ordinal))
            {
                e.Response = LitePages.Response(env, uri);
                return;
            }
            if (!Cfg.BlockAds || !Uri.TryCreate(uri, UriKind.Absolute, out var u)) return;
            if (!Blocklist.IsBlocked(u.Host, Cfg.ExtendedBlocklist)) return;
            e.Response = env.CreateWebResourceResponse(null, 403, "Blocked", "");
            tab.Blocked++;
            if (tab == active) address.SetBlocked(tab.Blocked);
        };

        core.NavigationStarting += (_, e) =>
        {
            if (Uri.TryCreate(e.Uri, UriKind.Absolute, out var u))
            {
                if (u.Host == LitePages.Host)
                {
                    var qs = HttpUtility.ParseQueryString(u.Query);
                    if (u.AbsolutePath == "/search")
                    {
                        e.Cancel = true;
                        var q = qs["q"] ?? "";
                        BeginInvoke(() => core.Navigate(ToUrl(q)));
                        return;
                    }
                    if (u.AbsolutePath == "/allow")
                    {
                        e.Cancel = true;
                        // Разрешаем только со своей страницы блокировки
                        if (!core.Source.StartsWith(LitePages.Blocked, StringComparison.Ordinal)) return;
                        var target = qs["url"];
                        if (target != null && Uri.TryCreate(target, UriKind.Absolute, out var t) && t.Scheme is "http" or "https")
                        {
                            Blocklist.Allow(t.Host);
                            BeginInvoke(() => core.Navigate(t.ToString()));
                        }
                        return;
                    }
                }
                else if (u.Scheme is "http" or "https")
                {
                    // Опасный или рекламный сайт — показываем предупреждение
                    if (Cfg.BlockAds && Blocklist.IsBlocked(u.Host, Cfg.ExtendedBlocklist))
                    {
                        e.Cancel = true;
                        var blocked = LitePages.Blocked + "?url=" + Uri.EscapeDataString(e.Uri);
                        BeginInvoke(() => core.Navigate(blocked));
                        return;
                    }
                    // Только HTTPS
                    if (Cfg.HttpsOnly && u.Scheme == "http" && !u.IsLoopback && !httpAllowed.Contains(u.Host))
                    {
                        e.Cancel = true;
                        upgraded.Add(u.Host);
                        var b = new UriBuilder(u) { Scheme = "https", Port = u.IsDefaultPort ? -1 : u.Port };
                        BeginInvoke(() => core.Navigate(b.Uri.ToString()));
                        return;
                    }
                }
            }
            tab.Loading = true;
            tab.Blocked = 0;
            tab.HasLoginForm = false;
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
            if (u.Scheme != "https" || !upgraded.Contains(u.Host) || httpAllowed.Contains(u.Host)) return;
            BeginInvoke(() =>
            {
                var r = MessageBox.Show(this,
                    $"Сайт {u.Host} не поддерживает защищённое соединение (HTTPS).\n\n" +
                    "Данные на нём могут перехватить. Всё равно открыть?",
                    "Небезопасный сайт", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2);
                if (r != DialogResult.Yes) return;
                httpAllowed.Add(u.Host);
                core.Navigate("http://" + u.Host + u.PathAndQuery);
            });
        };

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
                var r = MessageBox.Show(this, $"Сайт {host} просит доступ к {what}.\nРазрешить?", "Разрешение",
                    MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2);
                e.State = r == DialogResult.Yes ? CoreWebView2PermissionState.Allow : CoreWebView2PermissionState.Deny;
                deferral.Complete();
            });
        };

        core.NewWindowRequested += async (_, e) =>
        {
            var deferral = e.GetDeferral();
            var t = CreateTab(null, select: true);
            await InitTab(t);
            if (t.Core != null) e.NewWindow = t.Core;
            e.Handled = true;
            deferral.Complete();
        };

        core.DownloadStarting += (_, e) =>
        {
            if (!Cfg.BuiltInDownloads) return;
            e.Handled = true;
            Downloads.Track(e.DownloadOperation);
            BeginInvoke(() => { showDownloads = true; LayoutToolbar(); downloadsPopup.ShowBelow(downloadsBtn); });
        };

        // Скрипт менеджера паролей: находит формы входа на страницах
        _ = core.AddScriptToExecuteOnDocumentCreatedAsync(PasswordScripts.Page);

        core.WebMessageReceived += async (_, e) =>
        {
            if (!e.Source.StartsWith(LitePages.Prefix, StringComparison.Ordinal))
            {
                // Сообщения обычных сайтов — только от скрипта паролей, и только про их собственный адрес
                try { HandlePageMessage(tab, e.Source, e.WebMessageAsJson); } catch { }
                return;
            }
            try { await HandleMessage(core, e.WebMessageAsJson); }
            catch (Exception ex) { Post(core, new { type = "error", text = ex.Message }); }
        };

        core.DocumentTitleChanged += (_, _) =>
        {
            tab.Title = string.IsNullOrWhiteSpace(core.DocumentTitle) ? "Новая вкладка" : core.DocumentTitle;
            if (tab.HistoryEntry != null && tab.HistoryEntry.Url == core.Source)
            {
                tab.HistoryEntry.Title = tab.Title;
                Store.TouchHistory();
            }
            strip.Invalidate();
            if (tab == active) UpdateTitle();
        };

        core.SourceChanged += (_, _) =>
        {
            var src = core.Source;
            tab.IsHome = src.StartsWith(LitePages.Home, StringComparison.Ordinal);
            if (!Incognito && Cfg.SaveHistory && src.StartsWith("http") && !src.StartsWith(LitePages.Prefix))
                tab.HistoryEntry = Store.AddHistory(src, core.DocumentTitle);
            if (tab == active) UpdateUi(force: true);
            strip.Invalidate();
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

        core.IsDocumentPlayingAudioChanged += (_, _) => { tab.Audio = core.IsDocumentPlayingAudio; strip.Invalidate(); };
        core.IsMutedChanged += (_, _) => { tab.Muted = core.IsMuted; strip.Invalidate(); };
        core.HistoryChanged += (_, _) => { if (tab == active) UpdateUi(); };
        core.WindowCloseRequested += (_, _) => CloseTab(tab);
        core.ContainsFullScreenElementChanged += (_, _) => SetFullscreen(core.ContainsFullScreenElement);
    }

    /// <summary>Фильтры запросов: свой список (быстро) или «все запросы» для расширенного списка.</summary>
    static void ApplyFilters(BrowserTab tab)
    {
        var core = tab.Core;
        if (core == null) return;
        string mode = !Cfg.BlockAds ? "none" : Cfg.ExtendedBlocklist ? "all" : "builtin";
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
        if (!Cfg.PasswordManager || origin == null) { address.SetKey(false, false); return; }
        bool saved = Vault.Unlocked && Vault.ForOrigin(origin).Count > 0;
        bool pending = PendingFor(origin);
        address.SetKey(active!.HasLoginForm || saved || pending, saved || pending);
    }

    /// <summary>Сообщение от скрипта паролей на обычной странице. Адрес сайта берём из движка, а не из сообщения.</summary>
    void HandlePageMessage(BrowserTab tab, string source, string json)
    {
        if (!Cfg.PasswordManager || json.Length > 4000) return;
        var origin = PasswordTools.Origin(source);
        if (origin == null) return;
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
        menu.Items.Add(new ToolStripMenuItem("Пароли для " + PasswordTools.Host(origin)) { Enabled = false, Padding = new Padding(6, 5, 6, 3) });
        if (list.Count == 0)
            menu.Items.Add(new ToolStripMenuItem("Сохранённых паролей нет") { Enabled = false, Padding = new Padding(6, 4, 6, 4) });
        foreach (var e in list)
        {
            var entry = e;
            var label = (entry.Username.Length > 0 ? entry.Username : "(без логина)") + "   ••••••••";
            menu.Items.Add(Theme.Item(label, () => FillEntry(core, entry)));
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
                if (re != null) Post(core, new { type = "pwReveal", id = re.Id, password = re.Password });
                break;
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
                Vault.Upsert(sid.Length > 0 ? sid : null, origin, Str("username").Trim(), Str("password"));
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

    // ================= Сообщения от своих страниц =================

    static void Post(CoreWebView2 core, object msg) => core.PostWebMessageAsJson(JsonSerializer.Serialize(msg, AppSettings.Json));

    void SendSettings(CoreWebView2 core) => Post(core, new
    {
        type = "settings",
        settings = Cfg,
        version = AppEnv.Version,
        browserVersion = AppEnv.GetEnvironment().IsCompletedSuccessfully ? AppEnv.GetEnvironment().Result.BrowserVersionString : "",
        incognito = Incognito,
        importSuggest = !Incognito && !Cfg.ImportOffered && Store.Bookmarks.Count == 0,
        restartNeeded = AppEnv.RestartNeeded,
        proxyRunning = AppEnv.RunningSignature != "off",
        defaultSites = AppSettings.DefaultProxySites,
        blocklist = new { count = Blocklist.ExtendedCount, updated = Blocklist.Updated?.ToString("dd.MM.yyyy HH:mm"), updating = Blocklist.Updating },
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
        switch (msgType)
        {
            case "get":
                SendSettings(core);
                break;

            case "save":
                var ns = root.GetProperty("settings").Deserialize<AppSettings>(AppSettings.Json);
                if (ns == null) return;
                bool wantBlocklist = ns.ExtendedBlocklist && ns.BlockAds && Blocklist.ExtendedCount == 0;
                AppEnv.UpdateSettings(ns);
                Post(core, new { type = "saved", restartNeeded = AppEnv.RestartNeeded });
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

            case "updateBlocklist":
                SendSettings(core);
                bool updated = await Blocklist.UpdateAsync(AppEnv.DataDir, Cfg, force: true);
                SendSettings(core);
                Post(core, new { type = "done", text = updated ? "Список обновлён" : "Не удалось обновить список" });
                break;

            case "shortcuts":
                SystemIntegration.CreateShortcuts();
                Post(core, new { type = "done", text = "Ярлыки созданы на рабочем столе и в меню «Пуск»" });
                break;

            case "defaultBrowser":
                SystemIntegration.RegisterAsBrowser();
                Post(core, new { type = "done", text = "Выберите LiteBrowser в открывшемся окне Windows" });
                break;

            case "historyGet":
                Post(core, new
                {
                    type = "history",
                    saving = Cfg.SaveHistory,
                    items = Store.SearchHistory(Str("q"), 600).Select(h => new { title = h.Title, url = h.Url, time = h.Time }),
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
        }
    }

    // ================= Настройки и внешний вид =================

    CoreWebView2PreferredColorScheme ColorScheme() => Cfg.SiteTheme switch
    {
        "dark" => CoreWebView2PreferredColorScheme.Dark,
        "light" => CoreWebView2PreferredColorScheme.Light,
        _ => CoreWebView2PreferredColorScheme.Auto,
    };

    void OnSettingsChanged()
    {
        bool zoomChanged = Cfg.PageZoom != appliedPageZoom;
        appliedPageZoom = Cfg.PageZoom;
        foreach (var t in tabs)
        {
            if (t.Core == null) continue;
            t.Core.Profile.PreferredColorScheme = ColorScheme();
            ApplyFilters(t);
            if (zoomChanged) t.View.ZoomFactor = Cfg.PageZoom / 100.0;
        }
        if (Cfg.UiScale != appliedUiScale) ApplyScale();
        ApplyChrome();
    }

    // ================= Масштаб =================

    /// <summary>Меняет размер интерфейса браузера (вкладки, кнопки, адресная строка) без перезапуска.</summary>
    void ApplyScale()
    {
        appliedUiScale = Cfg.UiScale;
        Theme.UiScale = Cfg.UiScale / 100f;
        SuspendLayout();
        strip.Height = Theme.S(44);
        toolbar.Height = Theme.S(50);
        bookmarksBar.Height = Theme.S(34);
        address.ApplyScale();
        bookmarksBar.Rescale();
        ResumeLayout(true);
        LayoutToolbar();
        strip.Invalidate();
        foreach (Control c in toolbar.Controls) c.Invalidate();
        toolbar.Invalidate();
    }

    int CurrentZoom => active?.Core != null ? (int)Math.Round(active.View.ZoomFactor * 100) : Cfg.PageZoom;

    void UpdateZoomLabel()
    {
        int z = CurrentZoom;
        zoomBtn.Glyph = z + "%";
        zoomBtn.GlyphColor = z == 100 ? Theme.TextDim : Theme.Accent;
    }

    void SetZoom(int percent)
    {
        if (active?.Core == null) return;
        active.View.ZoomFactor = Math.Clamp(percent, 25, 300) / 100.0;
        UpdateZoomLabel();
    }

    void ZoomStep(int dir)
    {
        var steps = AppSettings.ZoomSteps;
        int cur = CurrentZoom;
        int next = dir > 0 ? steps.FirstOrDefault(s => s > cur, steps[^1]) : steps.LastOrDefault(s => s < cur, steps[0]);
        SetZoom(next);
    }

    void ShowZoomMenu()
    {
        var menu = Theme.NewMenu();
        var row = new ZoomRow(() => CurrentZoom);
        row.Step += d => { ZoomStep(d); row.Invalidate(); };
        row.Reset += () => { SetZoom(Cfg.PageZoom); row.Invalidate(); };
        menu.Items.Add(new ToolStripControlHost(row) { AutoSize = false, Size = row.Size, Margin = Padding.Empty, Padding = Padding.Empty });
        menu.Items.Add(Theme.Item("Сделать масштабом по умолчанию для всех сайтов", () => { int z = CurrentZoom; CopyAndSave(x => x.PageZoom = z); }));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("Размер интерфейса (вкладки, кнопки)") { Enabled = false, Padding = new Padding(6, 5, 6, 2) });
        foreach (var v in AppSettings.UiScales)
        {
            int value = v;
            menu.Items.Add(Theme.Item((v == Cfg.UiScale ? "✓  " : "     ") + v + "%" + (v == 100 ? " (обычный)" : ""),
                () => CopyAndSave(x => x.UiScale = value)));
        }
        menu.Show(zoomBtn, new Point(zoomBtn.Width - menu.PreferredSize.Width, zoomBtn.Height + Theme.S(4)));
    }

    void ApplyChrome()
    {
        address.Box.PlaceholderText = $"Поиск в {SearchEngines.Get(Cfg.Engine).Name} или адрес сайта";
        bookmarksBar.Visible = Cfg.ShowBookmarksBar;
        showProxy = AppEnv.RunningSignature != "off" || Cfg.ProxyMode != "off";
        proxyBtn.GlyphColor = AppEnv.RestartNeeded ? Theme.Danger : Theme.Secure;
        tips.SetToolTip(proxyBtn, AppEnv.RestartNeeded
            ? "Обход блокировок: нужен перезапуск браузера"
            : Cfg.ProxyMode == "all" ? $"Обход блокировок: все сайты через {Cfg.ProxyHost}:{Cfg.ProxyPort}"
            : $"Обход блокировок: {Cfg.ProxySites.Count} сайтов через {Cfg.ProxyHost}:{Cfg.ProxyPort}");
        showDownloads = Downloads.Items.Count > 0;
        LayoutToolbar();
    }

    void OnBookmarksChanged() => UpdateStar();

    void OnDownloadsChanged()
    {
        if (!showDownloads) { showDownloads = true; LayoutToolbar(); }
        downloadsBtn.Progress = Downloads.Progress;
        downloadsBtn.GlyphColor = Downloads.AnyActive ? Theme.Accent : null;
    }

    void ToggleDownloads()
    {
        if (downloadsPopup.Visible) { downloadsPopup.Hide(); return; }
        if ((DateTime.Now - downloadsPopup.HiddenAt).TotalMilliseconds < 250) return; // клик по кнопке закрыл окно
        showDownloads = true;
        LayoutToolbar();
        downloadsPopup.ShowBelow(downloadsBtn);
    }

    void ShowMainMenu()
    {
        var menu = Theme.NewMenu();
        menu.Items.Add(Theme.Item("Новая вкладка", () => _ = AddTab(homeUrl), "Ctrl+T"));
        menu.Items.Add(Theme.Item("Новое окно", () => BrowserApp.Instance.OpenWindow(false), "Ctrl+N"));
        menu.Items.Add(Theme.Item("Окно инкогнито", () => BrowserApp.Instance.OpenWindow(true), "Ctrl+Shift+N"));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(Theme.Item("История", () => OpenLitePage(LitePages.History), "Ctrl+H"));
        menu.Items.Add(Theme.Item("Пароли", () => OpenLitePage(LitePages.Passwords), "Ctrl+Shift+P"));
        menu.Items.Add(Theme.Item("Загрузки", ToggleDownloads, "Ctrl+J"));
        menu.Items.Add(Theme.Item(Cfg.ShowBookmarksBar ? "Скрыть панель закладок" : "Показать панель закладок", ToggleBookmarksBar, "Ctrl+Shift+B"));
        menu.Items.Add(Theme.Item("Открыть закрытую вкладку", ReopenClosedTab, "Ctrl+Shift+T"));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(Theme.Item("Обход блокировок", () => OpenLitePage(LitePages.Settings, "#proxy")));
        menu.Items.Add(Theme.Item("Очистить историю и данные", () => OpenLitePage(LitePages.Settings, "#data")));
        menu.Items.Add(Theme.Item("Настройки", () => OpenLitePage(LitePages.Settings), "Ctrl+,"));
        menu.Show(menuBtn, new Point(menuBtn.Width - menu.PreferredSize.Width, menuBtn.Height + Theme.S(4)));
    }

    void ToggleBookmarksBar()
    {
        bool show = !Cfg.ShowBookmarksBar;
        CopyAndSave(x => x.ShowBookmarksBar = show);
    }

    static void CopyAndSave(Action<AppSettings> change)
    {
        var copy = JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(Cfg, AppSettings.Json), AppSettings.Json)!;
        change(copy);
        AppEnv.UpdateSettings(copy);
    }

    void ToggleBookmark()
    {
        var core = active?.Core;
        if (core == null || !core.Source.StartsWith("http")) return;
        var existing = Store.FindBookmark(core.Source);
        if (existing != null) { Store.RemoveBookmark(existing); return; }
        string? icon = null;
        if (active!.Icon != null)
        {
            try
            {
                using var bmp = new Bitmap(32, 32);
                using (var g = Graphics.FromImage(bmp))
                {
                    g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    g.DrawImage(active.Icon, 0, 0, 32, 32);
                }
                using var ms = new MemoryStream();
                bmp.Save(ms, ImageFormat.Png);
                icon = Convert.ToBase64String(ms.ToArray());
            }
            catch { }
        }
        var title = string.IsNullOrWhiteSpace(core.DocumentTitle) ? new Uri(core.Source).Host : core.DocumentTitle;
        Store.AddBookmark(title, core.Source, icon);
    }

    void UpdateStar()
    {
        var src = active?.Core?.Source ?? "";
        bool web = src.StartsWith("http") && !src.StartsWith(LitePages.Prefix);
        address.SetStar(web, web && Store.FindBookmark(src) != null);
    }

    void OpenLitePage(string page, string hash = "")
    {
        var t = tabs.FirstOrDefault(x => (x.Core?.Source ?? x.PendingUrl)?.StartsWith(page) == true);
        if (t != null)
        {
            SelectTab(t);
            if (hash.Length > 0) t.Core?.Navigate(page + hash);
        }
        else _ = AddTab(page + hash);
    }

    void SetFullscreen(bool on)
    {
        if (on)
        {
            stateBeforeFullscreen = WindowState;
            strip.Visible = toolbar.Visible = bookmarksBar.Visible = false;
            FormBorderStyle = FormBorderStyle.None;
            WindowState = FormWindowState.Normal;
            WindowState = FormWindowState.Maximized;
        }
        else
        {
            FormBorderStyle = FormBorderStyle.Sizable;
            strip.Visible = toolbar.Visible = true;
            bookmarksBar.Visible = Cfg.ShowBookmarksBar;
            WindowState = stateBeforeFullscreen;
        }
    }

    // ================= Анимация загрузки =================

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
        using var br = new LinearGradientBrush(new RectangleF(x - 1, 0, seg + 2, h), Theme.Accent, Theme.Accent2, 0f);
        e.Graphics.FillRectangle(br, x, toolbar.Height - h, seg, h);
    }

    // ================= Интерфейс =================

    void UpdateUi(bool force = false)
    {
        var core = active?.Core;
        var src = core?.Source ?? active?.PendingUrl ?? "";
        back.Enabled = core?.CanGoBack ?? false;
        forward.Enabled = core?.CanGoForward ?? false;
        bool loading = active?.Loading == true;
        reload.Glyph = loading ? "" : "";
        tips.SetToolTip(reload, loading ? "Остановить" : "Обновить (F5)");

        var lite = LitePages.Display(src);
        if (force || !address.Box.Focused)
            address.Box.Text = lite ?? (src == "about:blank" ? "" : src);

        if (src.StartsWith(LitePages.Blocked)) address.SetLock("", Theme.Danger);
        else if (src.StartsWith(LitePages.Settings)) address.SetLock("", Theme.Accent);
        else if (src.StartsWith(LitePages.History)) address.SetLock("", Theme.Accent);
        else if (src.StartsWith(LitePages.Passwords)) address.SetLock("", Theme.Accent);
        else if (src.StartsWith(LitePages.Prefix)) address.SetLock("", Theme.TextDim);
        else if (src.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) address.SetLock("", Theme.Secure);
        else if (src.StartsWith("http://", StringComparison.OrdinalIgnoreCase)) address.SetLock("", Theme.Danger);
        else address.SetLock("", Theme.TextDim);

        address.SetBlocked(active?.Blocked ?? 0);
        UpdateStar();
        UpdateZoomLabel();
        UpdateKey();
        UpdateTitle();
        toolbar.Invalidate();
    }

    void UpdateTitle()
    {
        var title = active?.Title;
        var suffix = Incognito ? " — Инкогнито — LiteBrowser" : " — LiteBrowser";
        Text = string.IsNullOrWhiteSpace(title) ? suffix[3..] : title + suffix;
    }

    void Navigate(string url)
    {
        if (active == null) return;
        if (active.Core != null) active.Core.Navigate(url);
        else active.PendingUrl = url;
    }

    void ReloadOrStop()
    {
        if (active?.Core == null) return;
        if (active.Loading) active.Core.Stop(); else active.Core.Reload();
    }

    string ToUrl(string text)
    {
        var t = text.Trim();
        if (t.Length == 0) return homeUrl;
        if (t.StartsWith("lite://", StringComparison.OrdinalIgnoreCase))
        {
            int cut = t.IndexOfAny(new[] { '#', '?' });
            string name = (cut < 0 ? t[7..] : t[7..cut]).Trim('/').ToLowerInvariant(), rest = cut < 0 ? "" : t[cut..];
            switch (name)
            {
                case "settings": return LitePages.Settings + rest;
                case "history": return LitePages.History + rest;
                case "passwords": return LitePages.Passwords + rest;
                case "home": return homeUrl;
            }
        }
        if (t.StartsWith("localhost", StringComparison.OrdinalIgnoreCase)) return "http://" + t;
        if (Uri.TryCreate(t, UriKind.Absolute, out var u) && u.Scheme is "http" or "https") return u.ToString();
        if (!t.Contains(' ') && t.Contains('.') && !t.EndsWith('.')) return "https://" + t;
        return SearchEngines.Get(Cfg.Engine).Url + Uri.EscapeDataString(t);
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        switch (keyData)
        {
            case Keys.Control | Keys.T: _ = AddTab(homeUrl); return true;
            case Keys.Control | Keys.W: if (active != null) CloseTab(active); return true;
            case Keys.Control | Keys.Shift | Keys.T: ReopenClosedTab(); return true;
            case Keys.Control | Keys.N: BrowserApp.Instance.OpenWindow(false); return true;
            case Keys.Control | Keys.Shift | Keys.N: BrowserApp.Instance.OpenWindow(true); return true;
            case Keys.Control | Keys.Tab: CycleTab(1); return true;
            case Keys.Control | Keys.Shift | Keys.Tab: CycleTab(-1); return true;
            case Keys.Control | Keys.L: case Keys.F6: address.Box.Focus(); address.Box.SelectAll(); return true;
            case Keys.Control | Keys.D: ToggleBookmark(); return true;
            case Keys.Control | Keys.H: OpenLitePage(LitePages.History); return true;
            case Keys.Control | Keys.Shift | Keys.P: OpenLitePage(LitePages.Passwords); return true;
            case Keys.Control | Keys.J: ToggleDownloads(); return true;
            case Keys.Control | Keys.Shift | Keys.B: ToggleBookmarksBar(); return true;
            case Keys.Control | Keys.Oemcomma: OpenLitePage(LitePages.Settings); return true;
            case Keys.Control | Keys.Oemplus: case Keys.Control | Keys.Add: case Keys.Control | Keys.Shift | Keys.Oemplus: ZoomStep(1); return true;
            case Keys.Control | Keys.OemMinus: case Keys.Control | Keys.Subtract: ZoomStep(-1); return true;
            case Keys.Control | Keys.D0: case Keys.Control | Keys.NumPad0: SetZoom(Cfg.PageZoom); return true;
            case Keys.F5: active?.Core?.Reload(); return true;
            case Keys.Alt | Keys.Left: active?.Core?.GoBack(); return true;
            case Keys.Alt | Keys.Right: active?.Core?.GoForward(); return true;
        }
        if ((keyData & Keys.Modifiers) == Keys.Control)
        {
            var k = keyData & Keys.KeyCode;
            if (k is >= Keys.D1 and <= Keys.D8 && tabs.Count > k - Keys.D1) { SelectTab(tabs[k - Keys.D1]); return true; }
            if (k == Keys.D9 && tabs.Count > 0) { SelectTab(tabs[^1]); return true; }
        }
        return base.ProcessCmdKey(ref msg, keyData);
    }
}
