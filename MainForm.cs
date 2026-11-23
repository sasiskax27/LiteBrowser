using System.Text;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Text.Json;
using System.Web;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace LiteBrowser;

sealed partial class MainForm : Form
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
    Color frame;
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
    readonly InfoBar infoBar = new();
    readonly SuggestPopup suggest = new();
    readonly SidePanel sidePanel;
    readonly Splitter splitter = new() { Dock = DockStyle.Right, Visible = false, MinSize = 200, MinExtra = 300 };
    readonly IconButton sideBtn, extBtn, focusBtn;
    bool showFocus;
    ExtensionsPanel? extPanel;
    FindBar findBar = null!;
    CancellationTokenSource? suggestCts;
    (string origin, string user, string pass, DateTime time)? pendingCapture;
    readonly IconButton zoomBtn, installBtn;
    bool showInstall;

    public bool Incognito { get; }
    internal IReadOnlyList<BrowserTab> Tabs => tabs;
    /// <summary>Гостевое окно: приватное и без ваших данных — закладок, истории, паролей, настроек, боковой панели.</summary>
    public bool Guest { get; }

    static AppSettings Cfg => AppEnv.Settings;

    public MainForm(bool incognito, string? startUrl = null, List<string>? startTabs = null, int startActive = 0, bool guest = false)
    {
        Guest = guest;
        incognito |= guest;
        Incognito = incognito;
        this.startUrl = startUrl;
        this.startTabs = startTabs;
        this.startActive = startActive;
        Theme.SystemDpi = DeviceDpi / 96f;
        Theme.UiScale = Cfg.UiScale / 100f;
        appliedUiScale = Cfg.UiScale;
        appliedPageZoom = Cfg.PageZoom;
        appliedTheme = Cfg.UiTheme + "|" + Cfg.Accent;
        frame = incognito ? Theme.IncognitoFrame : Theme.Frame;
        homeUrl = guest ? LitePages.Home + "?guest=1" : incognito ? LitePages.Home + "?private=1" : LitePages.Home;

        Text = guest ? L.T("Гость") + " — LiteBrowser" : incognito ? "Инкогнито — LiteBrowser" : "LiteBrowser";
        Icon = Theme.MakeAppIcon();
        BackColor = frame;
        Size = new Size(Theme.S(1280), Theme.S(820));
        MinimumSize = new Size(Theme.S(600), Theme.S(400));
        StartPosition = FormStartPosition.CenterScreen;
        KeyPreview = true;

        strip = new TabStrip(tabs) { Dock = DockStyle.Top, Height = Theme.S(44), FrameColor = frame, Badge = guest ? "Гость" : incognito ? "Инкогнито" : Profiles.Current.Length > 0 ? Profiles.Current : null };
        strip.TabSelected += SelectTab;
        strip.TabClosed += CloseTab;
        strip.TabMuteToggled += t => { if (t.Core != null) t.Core.IsMuted = !t.Core.IsMuted; };
        strip.TabMenu += ShowTabMenu;
        strip.GroupClicked += g => { g.Collapsed = !g.Collapsed; if (g.Collapsed && active?.Group == g) { var other = tabs.FirstOrDefault(t => t.Group != g); if (other != null) SelectTab(other); } strip.Invalidate(); };
        strip.GroupMenu += ShowGroupMenu;
        strip.NewTab += () => _ = AddTab(homeUrl);
        strip.HoverChanged += OnTabHover;
        strip.TabSearch += ShowPalette;
        SetupFrame();
        strip.CompactToggled += () => { bool v = !Cfg.VerticalTabsCompact; CopyAndSave(x => x.VerticalTabsCompact = v); };

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
        installBtn = new IconButton("＋ Установить расширение", "Установить это расширение в LiteBrowser", tips) { TextMode = true, GlyphColor = Theme.Accent };
        installBtn.Click += async (_, _) => { if (active?.Core?.Source is { } u) await InstallExtension(u, null); };

        back.Click += (_, _) => active?.Core?.GoBack();
        forward.Click += (_, _) => active?.Core?.GoForward();
        reload.Click += (_, _) => ReloadOrStop();
        home.Click += (_, _) => Navigate(homeUrl);
        proxyBtn.Click += (_, _) => ShowProxyMenu();
        downloadsBtn.Click += (_, _) => ToggleDownloads();
        menuBtn.Click += (_, _) => ShowMainMenu();

        address.StarClicked += ToggleBookmark;
        address.KeyClicked += async () => await ShowKeyMenu();
        address.ShieldClicked += ShowShieldMenu;
        address.LockClicked += ShowSiteMenu;
        passwordPopup.OpenManager = () => OpenLitePage(LitePages.Passwords);
        passwordPopup.NeverForSite = origin => { var h = PasswordTools.HostKey(origin); CopyAndSave(x => x.NeverSave.Add(h)); pendingCapture = null; UpdateKey(); };
        passwordPopup.Saved += () => { pendingCapture = null; UpdateKey(); };
        downloadsPopup.OpenAll = () => OpenLitePage(LitePages.DownloadsPage);
        address.Box.KeyDown += (_, e) =>
        {
            if (e.KeyCode is Keys.Down or Keys.Up && suggest.Visible) { e.SuppressKeyPress = true; suggest.Step(e.KeyCode == Keys.Down ? 1 : -1); return; }
            if (e.KeyCode == Keys.Escape)
            {
                e.SuppressKeyPress = true;
                if (suggest.Visible) { suggest.Hide(); return; }
                UpdateUi(force: true); active?.View.Focus(); return;
            }
            if (e.KeyCode != Keys.Enter) return;
            e.SuppressKeyPress = true;
            var target = suggest.Visible && suggest.Selected is { } sel ? sel.Url : ToUrl(address.Box.Text);
            if (HandleSuggestAction(target)) return;
            suggest.Hide();
            Navigate(target);
            active?.View.Focus();
        };
        address.Box.TextChanged += (_, _) => UpdateSuggestions();
        address.Box.LostFocus += (_, _) => BeginInvoke(async () => { await Task.Delay(150); if (!address.Box.Focused) suggest.Hide(); });
        suggest.Chosen += it => { if (HandleSuggestAction(it.Url)) return; suggest.Hide(); Navigate(it.Url); active?.View.Focus(); };

        findBar = new FindBar(tips);
        findBar.Search += async term => await RunFind(term);
        findBar.Next += () => active?.Core?.Find.FindNext();
        findBar.Previous += () => active?.Core?.Find.FindPrevious();
        findBar.Closed += CloseFind;
        content.Controls.Add(findBar);
        content.Resize += (_, _) => PlaceFindBar();
        SetupSplit();
        // Первый клик по адресной строке выделяет весь адрес (как в других браузерах), следующие — ставят курсор
        bool selectOnClick = false;
        address.Box.Enter += (_, _) => { selectOnClick = true; BeginInvoke(address.Box.SelectAll); };
        address.Box.MouseUp += (_, _) => { if (selectOnClick && address.Box.SelectionLength == 0) address.Box.SelectAll(); selectOnClick = false; };
        address.Box.Leave += (_, _) => selectOnClick = false;

        focusBtn = new IconButton("", "Фокус-режим: отвлекающие сайты закрыты", tips) { TextMode = true, GlyphColor = Theme.Accent };
        focusBtn.Click += (_, _) => ShowFocusButtonMenu();
        extBtn = new IconButton("", "Расширения", tips);
        extBtn.Click += (_, _) => ShowExtPanel();
        mediaBtn = new IconButton("\uEC4F", L.T("Сейчас играет"), tips) { GlyphColor = Theme.Accent };
        mediaBtn.Click += (_, _) => ShowMediaMenu();
        sideBtn = new IconButton("", "Боковая панель (Ctrl+B)", tips);
        sideBtn.Click += (_, _) => ToggleSidePanel(!sidePanel!.Visible);
        toolbar.Controls.AddRange(new Control[] { back, forward, reload, home, address, installBtn, zoomBtn, proxyBtn, focusBtn, mediaBtn, downloadsBtn, extBtn, sideBtn, menuBtn });
        toolbar.Resize += (_, _) => LayoutToolbar();
        toolbar.Paint += PaintProgress;

        sidePanel = new SidePanel(tips, HandleMessage);
        sidePanel.OpenInMain += u => _ = AddTab(u);
        sidePanel.SetApps(Cfg.SidePanelApps);
        sidePanel.AppsChanged += list => { if (!Incognito) CopyAndSave(x => x.SidePanelApps = list); };
        sidePanel.CloseRequested += () => ToggleSidePanel(false);
        sidePanel.UrlChanged += u => { if (u != Cfg.SidePanelUrl && !Incognito) CopyAndSave(x => x.SidePanelUrl = u); };
        splitter.SplitterMoved += (_, _) => CopyAndSave(x => x.SidePanelWidth = (int)(sidePanel.Width / Theme.Dpi));

        Controls.Add(content);
        Controls.Add(splitter);
        Controls.Add(sidePanel);
        Controls.Add(infoBar);
        Controls.Add(bookmarksBar);
        Controls.Add(toolbar);
        Controls.Add(strip);
        ApplyTabLayout();

        anim.Tick += (_, _) => Animate();
        freezeTimer.Tick += async (_, _) => await FreezeIdleTabs();
        freezeTimer.Start();

        AppEnv.SettingsChanged += OnSettingsChanged;
        Shown += (_, _) => EnsureSnoozeTimer();
        Store.BookmarksChanged += OnBookmarksChanged;
        Downloads.Changed += OnDownloadsChanged;
        Vault.Changed += OnVaultChanged;
        AdBlock.Changed += OnAdBlockChanged;
        FormClosed += (_, _) =>
        {
            AdBlock.Changed -= OnAdBlockChanged;
            AppEnv.SettingsChanged -= OnSettingsChanged;
            Store.BookmarksChanged -= OnBookmarksChanged;
            Downloads.Changed -= OnDownloadsChanged;
            Vault.Changed -= OnVaultChanged;
            downloadsPopup.Dispose();
            passwordPopup.Dispose();
            suggest.Dispose();
        };

        Load += (_, _) => { if (StartSession?.Rule is { } sr && Cfg.WindowRules.Contains(sr)) SetDedicated(sr); StartupTrace.Mark("окно: Load"); OpenStartTabs(); if (Cfg.SidePanelOpen && !Incognito) ToggleSidePanel(true); };
        if (guest) sideBtn.Visible = false;
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
        BrowserApp.Instance.OnWindowClosing(this);
        // Идёт запись — дописываем файл, чтобы видео не пропало
        if (recorder != null) { var r = recorder; recorder = null; r.Dispose(); }
        base.OnFormClosing(e);
    }

    void LayoutToolbar()
    {
        int s = Theme.S(36), y = (toolbar.Height - s) / 2, x = Theme.S(10);
        foreach (var b in new[] { back, forward, reload, home }) { b.SetBounds(x, y, s, s); x += s + Theme.S(2); }
        int right = toolbar.Width - Theme.S(10);
        foreach (var b in new[] { menuBtn, sideBtn, extBtn, downloadsBtn, mediaBtn, focusBtn, proxyBtn, zoomBtn, installBtn })
        {
            // Visible до показа окна всегда false, поэтому храним желаемое состояние отдельно
            bool want = b == downloadsBtn ? showDownloads : b == proxyBtn ? showProxy : b == installBtn ? showInstall : b == extBtn ? !Incognito : b == focusBtn ? showFocus : b == sideBtn ? !Guest : b == mediaBtn ? showMedia : true;
            b.Visible = want;
            if (!want) continue;
            int w = b == zoomBtn ? Theme.S(58) : b == installBtn ? Theme.S(200) : b == focusBtn ? Theme.S(84) : s;
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
        if (startTabs is { Count: > 0 }) RestoreTabs(startTabs, StartSession?.Info, startActive);
        else
        {
            _ = AddTab(string.IsNullOrWhiteSpace(startUrl) ? homeUrl : ToUrl(startUrl));
        }
    }

    /// <summary>Открывает сохранённые вкладки (лениво — загрузится только активная).</summary>
    void RestoreTabs(List<string> startTabs, List<SessionTab>? info, int startActive)
    {
        {
            var groups = new Dictionary<string, TabGroup>();
            for (int i = 0; i < startTabs.Count; i++)
            {
                var u = startTabs[i];
                string title = u.StartsWith(LitePages.Prefix) ? "Новая вкладка" : Uri.TryCreate(u, UriKind.Absolute, out var x) ? x.Host : u;
                var t = CreateTab(u, select: false, title);
                if (info != null && i < info.Count)
                {
                    t.Pinned = info[i].Pinned;
                    if (info[i].Group != null)
                    {
                        var key = info[i].Group + "|" + info[i].GroupColor;
                        if (!groups.TryGetValue(key, out var g))
                        {
                            Color c = TabGroup.Palette[0];
                            try { if (info[i].GroupColor != null) c = ColorTranslator.FromHtml(info[i].GroupColor!); } catch { }
                            groups[key] = g = new TabGroup { Name = info[i].Group!, Color = c, Collapsed = info[i].GroupCollapsed };
                        }
                        t.Group = g;
                    }
                }
            }
            // CreateTab вставляет фоновые вкладки после активной — восстанавливаем исходный порядок
            var ordered = tabs.OrderBy(t => startTabs.IndexOf(t.PendingUrl ?? "")).ToList();
            tabs.Clear();
            tabs.AddRange(ordered.Where(t => t.Pinned).Concat(ordered.Where(t => !t.Pinned)));
            SelectTab(tabs[Math.Clamp(startActive, 0, tabs.Count - 1)]);
        }
    }

    BrowserTab CreateTab(string? url, bool select, string? title = null)
    {
        var view = new WebView2 { Dock = DockStyle.Fill, DefaultBackgroundColor = frame, Visible = false };
        var tab = new BrowserTab { View = view, PendingUrl = url, IsHome = url?.StartsWith(LitePages.Home) == true, Title = title ?? "Новая вкладка" };
        int insertAt = active != null && !select ? tabs.IndexOf(active) + 1 : tabs.Count;
        tabs.Insert(Math.Clamp(insertAt, 0, tabs.Count), tab);
        content.Controls.Add(view);
        view.GotFocus += (_, _) => OnViewFocused(tab);
        if (select) SelectTab(tab); else strip.Invalidate();
        return tab;
    }

    static readonly string[] GuestPages = { "/home.html", "/blocked.html", "/offline.html", "/search", "/allow" };
    static bool GuestAllowed(string path) => GuestPages.Contains(path, StringComparer.OrdinalIgnoreCase) || path.EndsWith(".js") || path.EndsWith(".css") || path.EndsWith(".json") || path.EndsWith(".png") || path.EndsWith(".svg");

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
                o.ProfileName = Guest ? "Guest" : "Incognito";
                await tab.View.EnsureCoreWebView2Async(env, o);
            }
            else await tab.View.EnsureCoreWebView2Async(env);
            if (tab.View.IsDisposed || tab.Core == null) return;
            StartupTrace.Mark("вкладка: WebView2 готов");
            HookController(tab);
            Configure(tab, env);
            ApplyAdScripts(tab);
            tab.ExpectedZoom = Cfg.PageZoom;
            tab.View.ZoomFactor = Cfg.PageZoom / 100.0;
            tab.View.ZoomFactorChanged += (_, _) => OnZoomChanged(tab);
            ApplyDarkScript(tab);
            var url = tab.PendingUrl;
            tab.PendingUrl = null;
            if (url != null) tab.Core.Navigate(url);
            if (tab == active) { FocusTab(tab); UpdateUi(force: true); }
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
        if (active != null && active != tab && active.Core != null) _ = TabPreview.CaptureAsync(active);
        // Видео с уходящей вкладки — в маленькое окно, а на новой — закрываем её окно (если открывали сами)
        if (active != null && active != tab && active.SplitWith != tab) _ = AutoPip(active, enter: true);
        if (active != tab) _ = AutoPip(tab, enter: false);
        if (findBar != null && active != tab) CloseFind();
        if (active != null && active != tab) active.LastActive = DateTime.Now;
        active = tab;
        tab.LastActive = DateTime.Now;
        LayoutViews();
        if (tab.Frozen && tab.Core != null)
        {
            try { tab.Core.Resume(); } catch { }
            tab.Frozen = false;
        }
        strip.Selected = tab;
        if (tab.Core == null) _ = InitTab(tab);
        else FocusTab(tab);
        UpdateUi(force: true);
    }

    internal void CloseTabFromManager(BrowserTab tab) { if (tabs.Contains(tab)) CloseTab(tab); }
    internal void ShowTabFromManager(BrowserTab tab) { if (!tabs.Contains(tab)) return; Activate(); SelectTab(tab); }
    internal async Task FreezeTabFromManager(BrowserTab tab)
    {
        if (tab == active || tab.Frozen || tab.Core == null) return; // активную вкладку не замораживаем — её сейчас смотрят
        try { if (await tab.Core.TrySuspendAsync()) { tab.Frozen = true; strip.Invalidate(); } } catch { }
    }

    void CloseTab(BrowserTab tab)
    {
        if (tab == recordingTab) _ = StopRecording();
        if (tab == speakingTab) StopReading();
        var url = tab.Core?.Source ?? tab.PendingUrl;
        if (url != null && !url.StartsWith(LitePages.Home)) { closedTabs.Push(url); RememberClosed(tab, url); }
        if (tabs.Count == 1) { Close(); return; }
        if (tab.SplitWith is { } sp) { sp.SplitWith = null; tab.SplitWith = null; }
        int idx = tabs.IndexOf(tab);
        tabs.Remove(tab);
        content.Controls.Remove(tab.View);
        tab.View.Dispose();
        tab.Icon?.Dispose();
        tab.Thumb?.Dispose();
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
        if (tab.Core != null) menu.Items.Add(VolumeMenu(tab));
        if (!Incognito && tab.Core?.Source is { } rs && rs.StartsWith("http") && Uri.TryCreate(rs, UriKind.Absolute, out var ru))
            menu.Items.Add(Theme.Item(RuleFor(ru.Host) is { } rr ? L.T("Не открывать ") + rr + L.T(" в отдельном окне") : L.T("Всегда открывать ") + RootDomain(ru.Host) + L.T(" в своём окне"), () => ToggleWindowRule(tab), null, "\uE737"));
        if (tab.SplitWith != null) menu.Items.Add(Theme.Item("Убрать разделение экрана", () => { SelectTab(tab); Unsplit(); }));
        else if (active != null && tab != active) menu.Items.Add(Theme.Item("Открыть рядом с текущей вкладкой", () => SplitWith(active, tab)));
        else menu.Items.Add(Theme.Item("Разделить экран", ToggleSplit, "Ctrl+\\"));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(Theme.Item(tab.Pinned ? "Открепить вкладку" : "Закрепить вкладку", () => TogglePin(tab)));
        menu.Items.Add(Theme.Item(Cfg.VerticalTabs ? "Вкладки сверху" : "Вертикальные вкладки (слева)", ToggleVerticalTabs));
        if (TabUrl(tab).StartsWith("http")) menu.Items.Add(SnoozeMenu(tab));
        menu.Items.Add(Theme.Item("Добавить в новую группу…", () => NewGroup(tab)));
        menu.Items.Add(Theme.Item("Сгруппировать вкладки по темам", AutoGroupTabs));
        if (Groups.Any()) menu.Items.Add(Theme.Item("Убрать все группы", UngroupAll));
        if (Groups.Any())
        {
            var sub = new ToolStripMenuItem(L.T("Добавить в группу")) { ForeColor = Theme.Text, Padding = new Padding(6, 5, 6, 5), DropDown = Theme.NewMenu() };
            foreach (var g in Groups.ToList())
            {
                var grp = g;
                var it = Theme.Item(g.Name.Length > 0 ? g.Name : "(без названия)", () => AddToGroup(tab, grp));
                it.ForeColor = g.Color;
                sub.DropDownItems.Add(it);
            }
            menu.Items.Add(sub);
        }
        if (tab.Group != null) menu.Items.Add(Theme.Item("Убрать из группы", () => { tab.Group = null; strip.Invalidate(); }));
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

    static string TabUrl(BrowserTab t) => t.Core?.Source is { Length: > 0 } s && !s.StartsWith(LitePages.Blocked) ? s : t.PendingUrl ?? LitePages.Home;

    public Session GetSession() => new()
    {
        Tabs = tabs.Select(TabUrl).ToList(),
        Info = tabs.Select(t => new SessionTab
        {
            Url = TabUrl(t), Pinned = t.Pinned, Group = t.Group?.Name, GroupColor = t.Group != null ? ColorTranslator.ToHtml(t.Group.Color) : null,
            GroupCollapsed = t.Group?.Collapsed ?? false,
        }).ToList(),
        Active = active == null ? 0 : Math.Max(0, tabs.IndexOf(active)),
        Window = WindowState == FormWindowState.Minimized ? null : new[] { RestoreBounds.X, RestoreBounds.Y, RestoreBounds.Width, RestoreBounds.Height, WindowState == FormWindowState.Maximized ? 1 : 0 },
        Rule = DedicatedRule,
    };

    public Session? StartSession { get; init; }

    // ================= Группы и закреплённые вкладки =================

    void TogglePin(BrowserTab t)
    {
        tabs.Remove(t);
        t.Pinned = !t.Pinned;
        if (t.Pinned) t.Group = null;
        int pinned = tabs.Count(x => x.Pinned);
        tabs.Insert(t.Pinned ? pinned : pinned, t);
        strip.Invalidate();
    }

    IEnumerable<TabGroup> Groups => tabs.Where(t => t.Group != null).Select(t => t.Group!).Distinct();

    void AddToGroup(BrowserTab t, TabGroup g)
    {
        if (t.Pinned) TogglePin(t);
        tabs.Remove(t);
        int last = tabs.FindLastIndex(x => x.Group == g);
        tabs.Insert(last >= 0 ? last + 1 : tabs.Count, t);
        t.Group = g;
        g.Collapsed = false;
        strip.Invalidate();
    }

    void NewGroup(BrowserTab t)
    {
        var name = InputDialog.Ask(this, "Название группы", "");
        if (name == null) return;
        var used = Groups.Select(g => g.Color).ToHashSet();
        var color = TabGroup.Palette.FirstOrDefault(c => !used.Contains(c), TabGroup.Palette[Groups.Count() % TabGroup.Palette.Length]);
        AddToGroup(t, new TabGroup { Name = name, Color = color });
    }

    void ShowGroupMenu(TabGroup g, Point screen)
    {
        var menu = Theme.NewMenu();
        menu.Items.Add(Theme.Item(g.Collapsed ? "Развернуть группу" : "Свернуть группу", () => { g.Collapsed = !g.Collapsed; strip.Invalidate(); }));
        menu.Items.Add(Theme.Item("Переименовать…", () => { var n = InputDialog.Ask(this, "Название группы", g.Name); if (n != null) { g.Name = n; strip.Invalidate(); } }));
        var colors = new ToolStripMenuItem(L.T("Цвет")) { ForeColor = Theme.Text, Padding = new Padding(6, 5, 6, 5), DropDown = Theme.NewMenu() };
        string[] names = { "Синий", "Красный", "Жёлтый", "Зелёный", "Розовый", "Фиолетовый", "Бирюзовый", "Оранжевый" };
        for (int i = 0; i < TabGroup.Palette.Length; i++)
        {
            var c = TabGroup.Palette[i];
            var item = Theme.Item((c == g.Color ? "●  " : "○  ") + names[i], () => { g.Color = c; strip.Invalidate(); });
            item.ForeColor = c;
            colors.DropDownItems.Add(item);
        }
        menu.Items.Add(colors);
        menu.Items.Add(Theme.Item("Новая вкладка в группе", async () => { var t = await AddTab(homeUrl); AddToGroup(t, g); }));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(Theme.Item("Разгруппировать", () => { foreach (var t in tabs.Where(x => x.Group == g)) t.Group = null; strip.Invalidate(); }));
        menu.Items.Add(Theme.Item("Закрыть группу", () => { foreach (var t in tabs.Where(x => x.Group == g).ToList()) CloseTab(t); }));
        menu.Show(screen);
    }

    async Task FreezeIdleTabs()
    {
        CollapseIdleGroups();
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
            if (!Uri.TryCreate(uri, UriKind.Absolute, out var u)) return;
            // Родительский контроль: запрещённые сайты не загружаются даже внутри других страниц
            if (Parental.IsBlocked(u.Host)) { Interlocked.Increment(ref SecurityStats.ParentalBlocked); e.Response = env.CreateWebResourceResponse(null, 403, "Blocked", ""); return; }
            // Экономия трафика: без картинок и видео
            if (Cfg.DataSaver && e.ResourceContext is CoreWebView2WebResourceContext.Image or CoreWebView2WebResourceContext.Media)
            {
                e.Response = env.CreateWebResourceResponse(null, 403, "Data saver", "");
                return;
            }
            if (!Cfg.BlockAds) return;
            if (e.ResourceContext == CoreWebView2WebResourceContext.Document && u.Host == tab.PageHost) return; // саму страницу не трогаем
            if (SiteAllowed(tab.PageHost)) return;
            var host = u.Host;
            bool lists = AdBlock.AnyEnabled(Cfg);
            bool block = lists && AdBlock.PageAllowed(tab.PageHost) ? false
                : AdBlock.IsException(host) ? false
                : Blocklist.IsBlocked(host, Cfg.ExtendedBlocklist) ||
                  (lists && AdBlock.ShouldBlock(uri, host, tab.PageHost, PatternIndex.FromContext(e.ResourceContext)));
            if (!block) return;
            e.Response = env.CreateWebResourceResponse(null, 403, "Blocked", "");
            tab.Blocked++;
            tab.BlockedHosts[host] = tab.BlockedHosts.GetValueOrDefault(host) + 1;
            SecurityStats.Blocked(host);
            Store.CountBlocked();
            if (tab == active) UpdateShield();
        };

        core.NavigationStarting += (_, e) =>
        {
            if (Uri.TryCreate(e.Uri, UriKind.Absolute, out var u))
            {
                // Сайт с правилом «в своём окне» — переносим туда
                if (!e.IsRedirected && RouteToRuleWindow(tab, u, e.IsUserInitiated)) { e.Cancel = true; return; }
                if (u.Host == LitePages.Host)
                {
                    var qs = HttpUtility.ParseQueryString(u.Query);
                    // Гостю — только стартовая и служебные страницы: ни настроек, ни паролей, ни истории
                    if (Guest && !GuestAllowed(u.AbsolutePath)) { e.Cancel = true; BeginInvoke(() =>
                    {
                        infoBar.ShowBar("guest", "\uE77B", L.T("В гостевом окне эта страница недоступна"));
                        if (!core.Source.StartsWith("http")) core.Navigate(homeUrl); // пустая вкладка — показываем стартовую
                    }); return; }
                    if (u.AbsolutePath == "/search")
                    {
                        e.Cancel = true;
                        var q = qs["q"] ?? "";
                        BeginInvoke(() => core.Navigate(ToUrl(q)));
                        return;
                    }
                    if (HandleFocusNavigation(core, u, qs)) { e.Cancel = true; return; }
                    if (u.AbsolutePath == "/allow")
                    {
                        e.Cancel = true;
                        // Разрешаем только со своей страницы блокировки
                        if (!core.Source.StartsWith(LitePages.Blocked, StringComparison.Ordinal)) return;
                        var target = qs["url"];
                        if (target != null && Uri.TryCreate(target, UriKind.Absolute, out var t) && t.Scheme is "http" or "https")
                        {
                            Blocklist.Allow(t.Host);
                            Lookalike.Allow(t.Host);
                            BeginInvoke(() => core.Navigate(t.ToString()));
                        }
                        return;
                    }
                }
                else if (u.Scheme is "http" or "https")
                {
                    if (Parental.IsBlocked(u.Host))
                    {
                        e.Cancel = true;
                        var pp = LitePages.Prefix + "parental.html?host=" + Uri.EscapeDataString(u.Host);
                        BeginInvoke(() => core.Navigate(pp));
                        return;
                    }
                    // Похоже на подделку известного сайта (банк, госуслуги, почта) — предупреждаем
                    if (Cfg.FakeSiteWarning && Lookalike.Check(u.Host) is { } real)
                    {
                        e.Cancel = true;
                        var warn = LitePages.Blocked + "?url=" + Uri.EscapeDataString(e.Uri) + "&fake=" + Uri.EscapeDataString(real);
                        BeginInvoke(() => core.Navigate(warn));
                        return;
                    }
                    // Фокус-режим: отвлекающий сайт — экран фокуса
                    if (FocusBlocks(u.Host))
                    {
                        e.Cancel = true;
                        var fp = FocusPageUrl(e.Uri, u.Host);
                        BeginInvoke(() => core.Navigate(fp));
                        return;
                    }
                    // Опасный или рекламный сайт — показываем предупреждение
                    if (Cfg.BlockAds && Blocklist.IsBlocked(u.Host, Cfg.ExtendedBlocklist))
                    {
                        e.Cancel = true;
                        Interlocked.Increment(ref SecurityStats.DangerousBlocked);
                        var blocked = LitePages.Blocked + "?url=" + Uri.EscapeDataString(e.Uri);
                        BeginInvoke(() => core.Navigate(blocked));
                        return;
                    }
                    // Только HTTPS
                    if (Cfg.HttpsOnly && u.Scheme == "http" && !u.IsLoopback && !httpAllowed.Contains(u.Host))
                    {
                        e.Cancel = true;
                        upgraded.Add(u.Host);
                        Interlocked.Increment(ref SecurityStats.HttpsUpgrades);
                        var b = new UriBuilder(u) { Scheme = "https", Port = u.IsDefaultPort ? -1 : u.Port };
                        BeginInvoke(() => core.Navigate(b.Uri.ToString()));
                        return;
                    }
                }
            }
            tab.Loading = true;
            tab.Dev?.OnNavigation(e.Uri);
            tab.Blocked = 0;
            tab.BlockedHosts.Clear();
            tab.PopupsBlocked = 0;
            tab.HasLoginForm = false;
            if (Uri.TryCreate(e.Uri, UriKind.Absolute, out var nu)) { tab.PageHost = nu.Host; ApplySiteZoom(tab); }
            // Ненадёжный сайт — без JavaScript
            bool scripts = !Untrusted(tab.PageHost);
            if (core.Settings.IsScriptEnabled != scripts) core.Settings.IsScriptEnabled = scripts;
            StartAnim();
            if (tab == active) UpdateUi();
            strip.Invalidate();
        };

        core.ProcessFailed += (_, e) => OnProcessFailed(tab, e);
        core.NavigationCompleted += (_, e) =>
        {
            tab.Loading = false;
            ReapplyVolume(tab);
            // Текст страницы — для поиска по истории «по содержимому»
            if (e.IsSuccess && !Incognito && Cfg.SaveHistory && Cfg.FullTextHistory && core.Source.StartsWith("http") && !core.Source.StartsWith(LitePages.Prefix))
                _ = PageIndex.CaptureAsync(core, core.Source);
            if (AppEnv.StartupMs == 0) StartupTrace.Mark("первая страница загружена: " + core.Source);
            if (AppEnv.StartupMs == 0) try { AppEnv.StartupMs = (long)(DateTime.Now - System.Diagnostics.Process.GetCurrentProcess().StartTime).TotalMilliseconds; } catch { }
            if (tab == active) UpdateUi();
            strip.Invalidate();
            if (e.IsSuccess && !Incognito) _ = OfferTranslate(tab);
            if (e.IsSuccess) _ = ApplyHighlights(tab);
            if (e.IsSuccess) _ = CaptureThumbLater(tab);
            if (e.IsSuccess || e.WebErrorStatus == CoreWebView2WebErrorStatus.OperationCanceled) return;
            if (TryOfflineFallback(core, core.Source, e.WebErrorStatus)) return;
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
            var permHost = Uri.TryCreate(e.Uri, UriKind.Absolute, out var pu) ? pu.Host : "";
            var permSite = PasswordTools.HostKey(permHost);
            var permKind = e.PermissionKind.ToString();
            // Ненадёжным сайтам — ничего; сохранённое решение — без вопроса
            if (Untrusted(permHost)) { e.State = CoreWebView2PermissionState.Deny; Interlocked.Increment(ref SecurityStats.PermissionsDenied); return; }
            if (Cfg.SitePermissions.TryGetValue(permSite, out var saved) && saved.TryGetValue(permKind, out var decision))
            {
                e.State = decision == "allow" ? CoreWebView2PermissionState.Allow : CoreWebView2PermissionState.Deny;
                return;
            }
            if (e.PermissionKind == CoreWebView2PermissionKind.Notifications)
            {
                e.State = CoreWebView2PermissionState.Deny;
                Interlocked.Increment(ref SecurityStats.PermissionsDenied);
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
                var r = MessageBox.Show(this, $"Сайт {host} просит доступ к {what}.\nРазрешить?\n\nРешение запомнится. Изменить его можно, нажав на замок в адресной строке.", "Разрешение",
                    MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2);
                bool allow = r == DialogResult.Yes;
                e.State = allow ? CoreWebView2PermissionState.Allow : CoreWebView2PermissionState.Deny;
                deferral.Complete();
                if (permSite.Length > 0 && !Incognito) CopyAndSave(x => SetPerm(x, permSite, permKind, allow ? "allow" : "deny"));
            });
        };

        core.NewWindowRequested += async (_, e) =>
        {
            // Всплывающие окна, которые сайт открывает сам, без клика пользователя
            if (Untrusted(tab.PageHost) || (Cfg.BlockAds && Cfg.BlockPopups && !e.IsUserInitiated && !SiteAllowed(tab.PageHost)))
            {
                e.Handled = true;
                tab.PopupsBlocked++;
                tab.Blocked++;
                Interlocked.Increment(ref SecurityStats.PopupsBlocked);
                Store.CountBlocked();
                if (tab == active) UpdateShield();
                return;
            }
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
        _ = core.AddScriptToExecuteOnDocumentCreatedAsync(PageTools.GesturesScript());
        _ = core.AddScriptToExecuteOnDocumentCreatedAsync(MediaSessionHook.Script);
        _ = core.AddScriptToExecuteOnDocumentCreatedAsync(PageTools.FocusPing);
        if (Cfg.SelectionTranslate) _ = core.AddScriptToExecuteOnDocumentCreatedAsync(SelectionTranslate.Script);
        SetupContextMenu(tab, core, env);

        core.WebMessageReceived += async (_, e) =>
        {
            // Клик по странице: в разделённом экране делает эту половину активной
            if (e.WebMessageAsJson == "{\"type\":\"lite-focus\"}") { OnViewFocused(tab); return; }
            if (e.WebMessageAsJson == "{\"type\":\"speak:done\"}") { OnSpeakDone(tab); return; }
            if (!e.Source.StartsWith(LitePages.Prefix, StringComparison.Ordinal))
            {
                // Сообщения обычных сайтов — только от скрипта паролей, и только про их собственный адрес
                try { HandlePageMessage(tab, core, e.Source, e.WebMessageAsJson); } catch { }
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

        core.Find.MatchCountChanged += (_, _) => { if (tab == active) findBar.SetCount(core.Find.ActiveMatchIndex + 1, core.Find.MatchCount); };
        core.Find.ActiveMatchIndexChanged += (_, _) => { if (tab == active) findBar.SetCount(core.Find.ActiveMatchIndex + 1, core.Find.MatchCount); };
        core.IsDocumentPlayingAudioChanged += (_, _) => { tab.Audio = core.IsDocumentPlayingAudio; strip.Invalidate(); BrowserApp.Instance.NotifyAudio(); };
        core.IsMutedChanged += (_, _) => { tab.Muted = core.IsMuted; strip.Invalidate(); };
        core.HistoryChanged += (_, _) => { if (tab == active) UpdateUi(); };
        core.WindowCloseRequested += (_, _) => CloseTab(tab);
        core.ContainsFullScreenElementChanged += (_, _) => SetFullscreen(core.ContainsFullScreenElement);
    }

    // ================= Боковая панель =================

    void ToggleSidePanel(bool show)
    {
        if (show)
        {
            sidePanel.ApplyTheme();
            sidePanel.Width = Math.Min(Theme.S(Cfg.SidePanelWidth), Math.Max(Theme.S(260), ClientSize.Width - Theme.S(400)));
            splitter.Width = Theme.S(4);
            splitter.BackColor = Theme.Line;
        }
        sidePanel.Visible = splitter.Visible = show;
        sideBtn.GlyphColor = show ? Theme.Accent : null;
        if (show) sidePanel.EnsureLoaded(Cfg.SidePanelUrl);
        if (!Incognito && Cfg.SidePanelOpen != show) CopyAndSave(x => x.SidePanelOpen = show);
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
        appliedTheme = Cfg.UiTheme + "|" + Cfg.Accent;
        foreach (var t in tabs)
        {
            if (t.Core == null) continue;
            t.Core.Profile.PreferredColorScheme = ColorScheme();
            ApplyFilters(t);
            ApplyAdScripts(t);
            if (zoomChanged) ApplySiteZoom(t);
            ApplyDarkScript(t);
        }
        if (Cfg.UiScale != appliedUiScale) ApplyScale();
        ApplyTabLayout();
        var themeSig = Cfg.UiTheme + "|" + Cfg.Accent;
        if (themeSig != appliedTheme)
        {
            appliedTheme = themeSig;
            Theme.ApplyPalette(Cfg.UiTheme, Cfg.Accent);
            ApplyTheme();
            // Свои страницы перечитают цвета
            foreach (var t in tabs) if (t.Core?.Source?.StartsWith(LitePages.Prefix) == true) t.Core.Reload();
        }
        ApplyChrome();
    }

    string appliedTheme = "";

    /// <summary>Перекрасить окно после смены темы или цвета акцента.</summary>
    void ApplyTheme()
    {
        if (!Incognito) frame = Theme.Frame;
        BackColor = frame;
        strip.FrameColor = frame;
        toolbar.BackColor = Theme.Toolbar;
        content.BackColor = frame;
        bookmarksBar.BackColor = Theme.Toolbar;
        address.BackColor = Theme.Toolbar;
        address.Box.BackColor = Theme.Field;
        address.Box.ForeColor = Theme.Text;
        foreach (Control c in toolbar.Controls) { if (c is IconButton) c.BackColor = Theme.Toolbar; c.Invalidate(); }
        foreach (var t in tabs) t.View.DefaultBackgroundColor = frame;
        sidePanel.ApplyTheme();
        splitter.BackColor = Theme.Line;
        installBtn.GlyphColor = Theme.Accent;
        try { Theme.StyleTitleBar(Handle, frame); } catch { }
        strip.Invalidate();
        toolbar.Invalidate();
        bookmarksBar.Invalidate();
        address.Invalidate();
    }

    // ================= Масштаб =================

    /// <summary>Меняет размер интерфейса браузера (вкладки, кнопки, адресная строка) без перезапуска.</summary>
    void ApplyScale()
    {
        appliedUiScale = Cfg.UiScale;
        Theme.UiScale = Cfg.UiScale / 100f;
        SuspendLayout();
        ApplyTabLayout();
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
        menu.Items.Add(new ToolStripMenuItem(L.T("Размер интерфейса (вкладки, кнопки)")) { Enabled = false, Padding = new Padding(6, 5, 6, 2) });
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
        bookmarksBar.Visible = Cfg.ShowBookmarksBar && !Guest;
        showProxy = AppEnv.RunningSignature != "off" || Cfg.ProxyMode != "off";
        proxyBtn.GlyphColor = AppEnv.RestartNeeded ? Theme.Danger : Theme.Secure;
        tips.SetToolTip(proxyBtn, AppEnv.RestartNeeded
            ? "Обход блокировок: нужен перезапуск браузера"
            : Cfg.ProxyMode == "all" ? $"Обход блокировок: все сайты через {Cfg.ProxyHost}:{Cfg.ProxyPort}"
            : $"Обход блокировок: {Cfg.ProxySites.Count} сайтов через {Cfg.ProxyHost}:{Cfg.ProxyPort}");
        showDownloads = Downloads.AnyActive || Downloads.Items.Any(i => (DateTime.Now - i.Time).TotalHours < 12);
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
        menu.ShowImageMargin = true;
        menu.Items.Add(Theme.Item("Новая вкладка", () => _ = AddTab(homeUrl), "Ctrl+T", "\uE710"));
        menu.Items.Add(Theme.Item("Новое окно", () => BrowserApp.Instance.OpenWindow(false), "Ctrl+N", "\uE7C4"));
        menu.Items.Add(Theme.Item("Окно инкогнито", () => BrowserApp.Instance.OpenWindow(true), "Ctrl+Shift+N", "\uE727"));
        menu.Items.Add(Theme.Item("Гостевое окно", BrowserApp.Instance.OpenGuest, "", "\uE77B"));
        if (WorkspacesHere) { var ws = WorkspacesMenu(); ws.Image = Theme.Glyph("\uE74C"); menu.Items.Add(ws); }
        menu.Items.Add(ProfilesMenu());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(Theme.Item("История", () => OpenLitePage(LitePages.History), "Ctrl+H", "\uE81C"));
        menu.Items.Add(Theme.Item("Закладки", () => OpenLitePage(LitePages.BookmarksPage), "Ctrl+Shift+O", "\uE728"));
        menu.Items.Add(Theme.Item("Прочитать позже" + (ReadLater.All.Count(i => !i.Read) is > 0 and var n ? $"  ({n})" : ""), () => OpenLitePage(LitePages.ReadLaterPage), null, "\uE7BC"));
        menu.Items.Add(Theme.Item("Загрузки", () => OpenLitePage(LitePages.DownloadsPage), "Ctrl+J", "\uE896"));
        menu.Items.Add(Theme.Item("Пароли", () => OpenLitePage(LitePages.Passwords), "Ctrl+Shift+P", "\uE8D7"));
        menu.Items.Add(Theme.Item("Расширения", () => OpenLitePage(LitePages.ExtensionsPage), "Ctrl+Shift+E", "\uEA86"));
        menu.Items.Add(new ToolStripSeparator());
        if (Cfg.AiEnabled) menu.Items.Add(Theme.Item("ИИ-помощник Lite AI", () => OpenAi(), "Ctrl+Shift+A", "\uE99A"));
        menu.Items.Add(Theme.Item("Команды и быстрый поиск", ShowPalette, "Ctrl+K", "\uE8FD"));
        menu.Items.Add(Theme.Item("Найти на странице", ShowFind, "Ctrl+F", "\uE721"));
        menu.Items.Add(Theme.Item(SplitPartner != null ? "Убрать разделение экрана" : "Разделить экран", ToggleSplit, "Ctrl+\\", "\uE736"));

        // Действия со страницей
        var pageMenu = SubMenu("Эта страница", "\uE8A5");
        pageMenu.DropDownItems.Add(Theme.Item("Режим чтения", () => _ = ToggleReader(), "Ctrl+Alt+R", "\uE8F1"));
        pageMenu.DropDownItems.Add(Theme.Item("Перевести страницу", TranslatePage, null, "\uE774"));
        pageMenu.DropDownItems.Add(Theme.Item("Читать вслух", () => _ = ReadAloud(), "Ctrl+Shift+U", "\uE767"));
        pageMenu.DropDownItems.Add(Theme.Item("Прочитать позже", AddToReadLater, "Ctrl+Shift+L", "\uE7BC"));
        pageMenu.DropDownItems.Add(Theme.Item("Сохранить для чтения без интернета", () => _ = SaveOffline(), "Ctrl+Shift+Y", "\uE78B"));
        pageMenu.DropDownItems.Add(Theme.Item("Скриншот видимой части", () => _ = TakeScreenshot(false), "Ctrl+Shift+S", "\uE722"));
        pageMenu.DropDownItems.Add(Theme.Item("Скриншот всей страницы", () => _ = TakeScreenshot(true), null, "\uE722"));
        pageMenu.DropDownItems.Add(Theme.Item("Мини-плеер поверх окон", () => { if (active != null) MiniPlayerForm.ShowFor(this, active); }, null, "\uE8B2"));
        pageMenu.DropDownItems.Add(Theme.Item("Текст с экрана (выделить область)", () => _ = RecognizeArea(), "Ctrl+Shift+X", "\uE8D2"));
        pageMenu.DropDownItems.Add(Theme.Item("Сравнить цены в магазинах", () => _ = ComparePrices(), null, "\uE7BF"));
        pageMenu.DropDownItems.Add(Theme.Item(recorder != null ? "Остановить запись видео" : "Записать видео вкладки", () => _ = ToggleRecording(), "Alt+Shift+R", "\uE7C8"));
        pageMenu.DropDownItems.Add(Theme.Item("QR-код страницы", ShowQr, null, "\uED14"));
        if (active?.PageHost is { Length: > 0 } ph && ph != LitePages.Host)
            pageMenu.DropDownItems.Add(Theme.Item((DarkFor(ph) ? "✓  " : "") + "Тёмная тема для этого сайта", ToggleDarkForSite, null, "\uE708"));
        pageMenu.DropDownItems.Add(new ToolStripSeparator());
        pageMenu.DropDownItems.Add(Theme.Item("Картинка в картинке", () => _ = PictureInPicture(), "Ctrl+Shift+V", "\uE8B9"));
        var speed = SubMenu("Скорость видео", "\uE768");
        foreach (var r in new[] { 0.5, 1, 1.25, 1.5, 2, 3 })
        {
            var rate = r;
            speed.DropDownItems.Add(Theme.Item($"×{rate:0.##}", () => _ = SetSpeed(rate)));
        }
        pageMenu.DropDownItems.Add(speed);
        pageMenu.DropDownItems.Add(Theme.Item("Скачать видео или аудио", () => _ = DownloadMedia(), null, "\uE714"));
        menu.Items.Add(pageMenu);

        // Инструменты
        var tools = SubMenu("Инструменты", "\uEC7A");
        tools.DropDownItems.Add(Theme.Item(sidePanel.Visible ? "Скрыть боковую панель" : "Боковая панель", () => ToggleSidePanel(!sidePanel.Visible), "Ctrl+B", "\uE8A0"));
        tools.DropDownItems.Add(Theme.Item(Cfg.ShowBookmarksBar ? "Скрыть панель закладок" : "Показать панель закладок", ToggleBookmarksBar, "Ctrl+Shift+B", "\uE728"));
        tools.DropDownItems.Add(RecentlyClosedMenu());
        tools.DropDownItems.Add(Theme.Item("Диспетчер задач браузера", () => TaskManagerForm.ShowFor(this), "Shift+Esc", "\uE9D9"));
        tools.DropDownItems.Add(FocusMenu());
        tools.DropDownItems.Add(SnoozedListMenu());
        tools.DropDownItems.Add(Theme.Item(Cfg.VerticalTabs ? "Вкладки сверху" : "Вертикальные вкладки", ToggleVerticalTabs, null, "\uE8A9"));
        tools.DropDownItems.Add(new ToolStripSeparator());
        tools.DropDownItems.Add(Theme.Item("Заметки и выделения", () => OpenLitePage(LitePages.Notes), null, "\uE8EC"));
        tools.DropDownItems.Add(Theme.Item("Сохранённые страницы", () => OpenLitePage(LitePages.OfflinePage), null, "\uE8B7"));
        tools.DropDownItems.Add(Theme.Item("Голосовой ввод", () => _ = VoiceInput(), "Ctrl+Shift+M", "\uE720"));
        tools.DropDownItems.Add(Theme.Item((Cfg.DataSaver ? "✓  " : "") + "Экономия трафика (без картинок)", () => { bool v = !Cfg.DataSaver; CopyAndSave(x => x.DataSaver = v); active?.Core?.Reload(); }, null, "\uEB9F"));
        tools.DropDownItems.Add(new ToolStripSeparator());
        tools.DropDownItems.Add(Theme.Item("Центр безопасности", () => OpenLitePage(LitePages.Security), null, "\uE83D"));
        tools.DropDownItems.Add(Theme.Item("Обход блокировок", () => OpenLitePage(LitePages.Settings, "#proxy"), null, "\uE909"));
        tools.DropDownItems.Add(Theme.Item("Инструменты разработчика", OpenDevTools, "Ctrl+Shift+I", "\uE9D9"));
        tools.DropDownItems.Add(Theme.Item("Очистить историю и данные", () => OpenLitePage(LitePages.Settings, "#data"), null, "\uE74D"));
        menu.Items.Add(tools);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(Theme.Item("Настройки", () => OpenLitePage(LitePages.Settings), "Ctrl+,", "\uE713"));
        menu.Items.Add(Theme.Item("Выход (запомнить все окна)", BrowserApp.Instance.ExitAll, "Ctrl+Shift+Q", "\uE7E8"));
        // Меню у правого края окна — подменю открываем влево, иначе они уходят за экран
        OpenSubmenusLeft(menu.Items);
        menu.Show(menuBtn, new Point(menuBtn.Width - menu.PreferredSize.Width, menuBtn.Height + Theme.S(4)));
    }

    static void OpenSubmenusLeft(ToolStripItemCollection items)
    {
        foreach (ToolStripItem i in items)
            if (i is ToolStripMenuItem m && m.HasDropDownItems)
            {
                m.DropDownDirection = ToolStripDropDownDirection.Left;
                OpenSubmenusLeft(m.DropDownItems);
            }
    }

    static ToolStripMenuItem SubMenu(string text, string? glyph = null)
    {
        var dd = Theme.NewMenu();
        dd.ShowImageMargin = true;
        return new(L.T(text)) { ForeColor = Theme.Text, Padding = new Padding(4, 4, 6, 4), DropDown = dd, Image = glyph != null ? Theme.Glyph(glyph) : null };
    }

    ToolStripMenuItem ProfilesMenu()
    {
        var prof = SubMenu(L.T("Профили") + (Profiles.Current.Length > 0 ? $" ({Profiles.Current})" : ""), "\uE77B");
        prof.DropDownItems.Add(Theme.Item((Profiles.Current.Length == 0 ? "●  " : "○  ") + L.T("Основной"), () => { if (Profiles.Current.Length > 0) Profiles.Open(null); }));
        foreach (var p in Profiles.List())
        {
            var pp = p;
            prof.DropDownItems.Add(Theme.Item((Profiles.Current == p.Name ? "●  " : "○  ") + p.Name, () => { if (Profiles.Current != pp.Name) Profiles.Open(pp); }));
        }
        prof.DropDownItems.Add(new ToolStripSeparator());
        prof.DropDownItems.Add(Theme.Item("Новый профиль…", () =>
        {
            var name = InputDialog.Ask(this, L.T("Название профиля (например, «Работа»)"), "");
            if (string.IsNullOrWhiteSpace(name)) return;
            try { Profiles.Open(Profiles.Create(name)); } catch (Exception ex) { MessageBox.Show(this, ex.Message, L.T("Профили")); }
        }, null, "\uE710"));
        if (Profiles.List().Any(p => p.Name != Profiles.Current))
        {
            var del = SubMenu("Удалить профиль", "\uE74D");
            foreach (var p in Profiles.List().Where(p => p.Name != Profiles.Current))
            {
                var pp = p;
                del.DropDownItems.Add(Theme.Item(p.Name, () =>
                {
                    if (MessageBox.Show(this, $"Удалить профиль «{pp.Name}» вместе с его закладками, паролями и историей? Это нельзя отменить.\n\nЕсли окно этого профиля открыто — сначала закройте его.",
                        "Удалить профиль", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) == DialogResult.Yes)
                        Profiles.Delete(pp.Id);
                }));
            }
            prof.DropDownItems.Add(del);
        }
        return prof;
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
            bookmarksBar.Visible = Cfg.ShowBookmarksBar && !Guest;
            WindowState = stateBeforeFullscreen;
        }
        RefreshFrame();
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
        else if (src.StartsWith(LitePages.ExtensionsPage) || src.StartsWith("chrome-extension://")) address.SetLock("", Theme.Accent);
        else if (src.StartsWith(LitePages.DownloadsPage)) address.SetLock("", Theme.Accent);
        else if (src.StartsWith(LitePages.BookmarksPage)) address.SetLock("", Theme.Accent);
        else if (src.StartsWith(LitePages.Prefix)) address.SetLock("", Theme.TextDim);
        else if (src.StartsWith("http") && Untrusted(active?.PageHost ?? "")) address.SetLock("", Color.FromArgb(255, 184, 107));
        else if (src.StartsWith("http") && Trusted(active?.PageHost ?? "")) address.SetLock("", Theme.Secure);
        else if (src.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) address.SetLock("", Theme.Secure);
        else if (src.StartsWith("http://", StringComparison.OrdinalIgnoreCase)) address.SetLock("", Theme.Danger);
        else address.SetLock("", Theme.TextDim);

        UpdateShield();
        UpdateStar();
        UpdateZoomLabel();
        UpdateKey();
        bool store = !Incognito && Extensions.Parse(src) != null;
        if (store != showInstall) { showInstall = store; LayoutToolbar(); }
        UpdateTitle();
        toolbar.Invalidate();
    }

    void UpdateTitle()
    {
        var title = active?.Title;
        var suffix = DedicatedRule != null ? $" — {DedicatedRule} — LiteBrowser" : Guest ? " — " + L.T("Гость") + " — LiteBrowser" : Incognito ? " — Инкогнито — LiteBrowser" : Profiles.Current.Length > 0 ? $" — {Profiles.Current} — LiteBrowser" : " — LiteBrowser";
        suffix = WorkspaceSuffix + suffix;
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
                case "extensions": return LitePages.ExtensionsPage + rest;
                case "downloads": return LitePages.DownloadsPage + rest;
                case "bookmarks": return LitePages.BookmarksPage + rest;
                case "notes": return LitePages.Notes + rest;
                case "ai": return LitePages.Ai + rest;
                case "security": return LitePages.Security + rest;
                case "devtools": return LitePages.DevTools + rest;
                case "offline": return LitePages.OfflinePage + rest;
                case "readlater": return LitePages.ReadLaterPage + rest;
                case "home": return homeUrl;
            }
        }
        if (t.StartsWith("localhost", StringComparison.OrdinalIgnoreCase)) return "http://" + t;
        if (Uri.TryCreate(t, UriKind.Absolute, out var u) && u.Scheme is "http" or "https") return u.ToString();
        if (!t.Contains(' ') && t.Contains('.') && !t.EndsWith('.')) return "https://" + t;
        return Parental.SafeSearch(SearchEngines.Get(Cfg.Engine).Url + Uri.EscapeDataString(t));
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData) =>
        HandleShortcut(keyData) || base.ProcessCmdKey(ref msg, keyData);

    /// <summary>Горячие клавиши браузера. true — сочетание наше и выполнено.</summary>
    bool HandleShortcut(Keys keyData)
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
            case Keys.Control | Keys.F: case Keys.F3: ShowFind(); return true;
            case Keys.Control | Keys.B: ToggleSidePanel(!sidePanel.Visible); return true;
            case Keys.Control | Keys.Shift | Keys.A: if (Cfg.AiEnabled) OpenAi(); return true;
            case Keys.Control | Keys.Shift | Keys.U: if (speakingTab != null) StopReading(); else _ = ReadAloud(); return true;
            case Keys.Control | Keys.Shift | Keys.M: _ = VoiceInput(); return true;
            case Keys.Control | Keys.Alt | Keys.R: _ = ToggleReader(); return true;
            case Keys.Control | Keys.Shift | Keys.V: _ = PictureInPicture(); return true;
            case Keys.Control | Keys.Shift | Keys.S: _ = TakeScreenshot(false); return true;
            case Keys.Alt | Keys.Shift | Keys.R: _ = ToggleRecording(); return true;
            case Keys.Control | Keys.Shift | Keys.X: _ = RecognizeArea(); return true;
            case Keys.Shift | Keys.Escape: TaskManagerForm.ShowFor(this); return true;
            case Keys.Control | Keys.H: OpenLitePage(LitePages.History); return true;
            case Keys.Control | Keys.Shift | Keys.P: OpenLitePage(LitePages.Passwords); return true;
            case Keys.Control | Keys.Shift | Keys.E: OpenLitePage(LitePages.ExtensionsPage); return true;
            case Keys.Control | Keys.Shift | Keys.O: OpenLitePage(LitePages.BookmarksPage); return true;
            case Keys.Control | Keys.J: OpenLitePage(LitePages.DownloadsPage); return true;
            case Keys.Control | Keys.Shift | Keys.I: OpenDevTools(); return true;
            case Keys.Control | Keys.K: ShowPalette(); return true;
            case Keys.Control | Keys.Shift | Keys.Y: _ = SaveOffline(); return true;
            case Keys.Control | Keys.Shift | Keys.L: AddToReadLater(); return true;
            case Keys.Control | Keys.Shift | Keys.Q: BrowserApp.Instance.ExitAll(); return true;
            case Keys.Control | Keys.OemPipe: case Keys.Control | Keys.OemBackslash: ToggleSplit(); return true;
            case Keys.Control | Keys.Shift | Keys.B: ToggleBookmarksBar(); return true;
            case Keys.Control | Keys.Oemcomma: OpenLitePage(LitePages.Settings); return true;
            case Keys.Control | Keys.Oemplus: case Keys.Control | Keys.Add: case Keys.Control | Keys.Shift | Keys.Oemplus: ZoomStep(1); return true;
            case Keys.Control | Keys.OemMinus: case Keys.Control | Keys.Subtract: ZoomStep(-1); return true;
            case Keys.Control | Keys.D0: case Keys.Control | Keys.NumPad0: SetZoom(Cfg.PageZoom); return true;
            case Keys.F5: active?.Core?.Reload(); return true;
            case Keys.F11: SetFullscreen(FormBorderStyle != FormBorderStyle.None); return true;
            case Keys.Alt | Keys.Left: active?.Core?.GoBack(); return true;
            case Keys.Alt | Keys.Right: active?.Core?.GoForward(); return true;
        }
        if ((keyData & Keys.Modifiers) == Keys.Control)
        {
            var k = keyData & Keys.KeyCode;
            if (k is >= Keys.D1 and <= Keys.D8 && tabs.Count > k - Keys.D1) { SelectTab(tabs[k - Keys.D1]); return true; }
            if (k == Keys.D9 && tabs.Count > 0) { SelectTab(tabs[^1]); return true; }
        }
        return false;
    }
}
