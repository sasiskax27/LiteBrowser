using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace LiteBrowser;

/// <summary>
/// Боковая панель: свои страницы (ИИ, закладки, история, загрузки) и веб-приложения (Telegram, WhatsApp, ChatGPT, музыка…).
/// У каждого приложения своя «живая» страница внутри панели — переписка не перезагружается при переключении,
/// вход в аккаунты сохраняется (общий профиль браузера). Свои приложения добавляются кнопкой «+».
/// </summary>
sealed class SidePanel : Panel
{
    public static readonly (string glyph, bool text, string tip, string url)[] Presets =
    {
        ("✦", true, "Lite AI — ИИ-помощник", LitePages.Ai),
        ("", false, "Закладки", LitePages.BookmarksPage),
        ("", false, "История", LitePages.History),
        ("", false, "Загрузки", LitePages.DownloadsPage),
    };

    public static List<HomeTile> DefaultApps() => new()
    {
        new() { Name = "Telegram", Url = "https://web.telegram.org/a/" },
        new() { Name = "ChatGPT", Url = "https://chatgpt.com/" },
        new() { Name = "WhatsApp", Url = "https://web.whatsapp.com/" },
        new() { Name = "Яндекс Музыка", Url = "https://music.yandex.ru/" },
    };

    readonly BufferedPanel header = new() { Dock = DockStyle.Top };
    readonly Panel body = new() { Dock = DockStyle.Fill };
    readonly WebView2 view = new() { Dock = DockStyle.Fill };           // свои страницы и разовые сайты
    readonly Dictionary<string, WebView2> appViews = new();            // по приложению — своя страница
    readonly List<IconButton> buttons = new();
    readonly List<(IconButton btn, HomeTile app)> appButtons = new();
    readonly IconButton add, close;
    readonly ToolTip tips;
    readonly Func<CoreWebView2, string, Task> liteHandler;
    Task? init;
    WebView2 current;

    public event Action<string>? OpenInMain;
    public event Action? CloseRequested;
    public event Action<string>? UrlChanged;
    /// <summary>Список приложений изменился (добавили/убрали) — сохранить в настройках.</summary>
    public event Action<List<HomeTile>>? AppsChanged;
    public CoreWebView2? Core => current.CoreWebView2;
    List<HomeTile> apps = new();

    public SidePanel(ToolTip tips, Func<CoreWebView2, string, Task> liteHandler)
    {
        this.tips = tips;
        this.liteHandler = liteHandler;
        current = view;
        Dock = DockStyle.Right;
        Visible = false;
        foreach (var (glyph, text, tip, url) in Presets)
        {
            var b = new IconButton(glyph, tip, tips) { TextMode = text };
            var u = url;
            b.Click += (_, _) => Navigate(u);
            buttons.Add(b);
        }
        add = new IconButton("", "Добавить приложение в панель", tips);
        add.Click += (_, _) => ShowAddMenu();
        close = new IconButton("", "Закрыть панель (Ctrl+B)", tips);
        close.Click += (_, _) => CloseRequested?.Invoke();
        header.Controls.AddRange(buttons.Cast<Control>().Concat(new Control[] { add, close }).ToArray());
        header.Resize += (_, _) => LayoutHeader();
        body.Controls.Add(view);
        Controls.Add(body);
        Controls.Add(header);
        ApplyTheme();
    }

    /// <summary>Кнопки приложений по списку из настроек.</summary>
    public void SetApps(List<HomeTile> list)
    {
        apps = list.Where(a => a.Url.StartsWith("http")).ToList();
        foreach (var (b, _) in appButtons) { header.Controls.Remove(b); b.Dispose(); }
        appButtons.Clear();
        foreach (var a in apps)
        {
            var app = a;
            var label = AppLabel(a.Name);
            var b = new IconButton(label, a.Name, tips) { TextMode = true, Tile = AppColor(a.Name) };
            b.BackColor = Theme.Toolbar;
            b.Click += (_, _) => ShowApp(app);
            b.MouseUp += (_, e) => { if (e.Button == MouseButtons.Right) ShowAppMenu(app, b); };
            appButtons.Add((b, app));
            header.Controls.Add(b);
        }
        // Приложение убрали — закрываем его страницу
        foreach (var key in appViews.Keys.Where(k => apps.All(a => a.Url != k)).ToList())
        {
            if (current == appViews[key]) ShowView(view);
            body.Controls.Remove(appViews[key]);
            appViews[key].Dispose();
            appViews.Remove(key);
        }
        LayoutHeader();
    }

    /// <summary>Фирменный цвет приложения (или цвет из названия — у каждого свой, но постоянный).</summary>
    static Color AppColor(string name)
    {
        var known = new Dictionary<string, Color>(StringComparer.OrdinalIgnoreCase)
        {
            ["Telegram"] = Color.FromArgb(42, 171, 238), ["WhatsApp"] = Color.FromArgb(37, 211, 102), ["ChatGPT"] = Color.FromArgb(16, 163, 127),
            ["Яндекс Музыка"] = Color.FromArgb(255, 204, 0), ["YouTube Music"] = Color.FromArgb(255, 0, 51), ["Discord"] = Color.FromArgb(88, 101, 242),
            ["ВКонтакте"] = Color.FromArgb(0, 119, 255), ["YouTube"] = Color.FromArgb(255, 0, 51),
        };
        if (known.TryGetValue(name, out var c)) return c;
        Color[] pal = { Color.FromArgb(124, 92, 255), Color.FromArgb(236, 72, 153), Color.FromArgb(14, 165, 233), Color.FromArgb(245, 158, 11), Color.FromArgb(16, 185, 129) };
        int h = 0; foreach (var ch in name) h = h * 31 + ch;
        return pal[Math.Abs(h) % pal.Length];
    }

    static string AppLabel(string name)
    {
        var known = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            { ["Telegram"] = "TG", ["WhatsApp"] = "WA", ["ChatGPT"] = "AI", ["Яндекс Музыка"] = "♪", ["YouTube Music"] = "♪", ["Discord"] = "DC", ["ВКонтакте"] = "VK" };
        if (known.TryGetValue(name, out var k)) return k;
        var letters = name.Where(char.IsLetterOrDigit).Take(2).ToArray();
        return letters.Length > 0 ? new string(letters).ToUpperInvariant() : "•";
    }

    public void ApplyTheme()
    {
        BackColor = Theme.Frame;
        header.BackColor = Theme.Toolbar;
        header.Height = Theme.S(44);
        foreach (var b in buttons.Concat(appButtons.Select(x => x.btn)).Append(add).Append(close)) b.BackColor = Theme.Toolbar;
        view.DefaultBackgroundColor = Theme.Frame;
        LayoutHeader();
    }

    void LayoutHeader()
    {
        int s = Theme.S(32), y = (header.Height - s) / 2, x = Theme.S(6);
        int right = header.Width - s - Theme.S(6);
        foreach (var b in buttons.Concat(appButtons.Select(a => a.btn)))
        {
            // Не помещается — прячем (остаются доступны через «+» → список)
            b.Visible = x + s <= right - s - Theme.S(4);
            if (b.Visible) { b.SetBounds(x, y, s, s); x += s + Theme.S(2); }
        }
        add.SetBounds(x, y, s, s);
        close.SetBounds(right, y, s, s);
        // Подсветка активного приложения
        foreach (var (b, a) in appButtons) b.Active = current != view && appViews.GetValueOrDefault(a.Url) == current;
    }

    Task Init() => init ??= Setup(view, lite: true);

    async Task Setup(WebView2 v, bool lite)
    {
        var env = await AppEnv.GetEnvironment();
        await v.EnsureCoreWebView2Async(env);
        var core = v.CoreWebView2;
        core.Settings.IsPasswordAutosaveEnabled = false;
        core.Settings.IsGeneralAutofillEnabled = false;
        core.Settings.IsWebMessageEnabled = true;
        core.NewWindowRequested += (_, e) => { e.Handled = true; OpenInMain?.Invoke(e.Uri); };
        if (!lite) return;
        core.AddWebResourceRequestedFilter(LitePages.Prefix + "*", CoreWebView2WebResourceContext.All);
        core.WebResourceRequested += (_, e) =>
        {
            if (e.Request.Uri.StartsWith(LitePages.Prefix, StringComparison.Ordinal)) e.Response = LitePages.Response(env, e.Request.Uri);
        };
        core.WebMessageReceived += async (_, e) =>
        {
            if (!e.Source.StartsWith(LitePages.Prefix, StringComparison.Ordinal)) return;
            try { await liteHandler(core, e.WebMessageAsJson); } catch { }
        };
        // Ссылки со своих страниц (закладки, история) открываем в основном окне
        core.NavigationStarting += (_, e) =>
        {
            if (core.Source.StartsWith(LitePages.Prefix) && !e.Uri.StartsWith(LitePages.Prefix) && e.Uri.StartsWith("http"))
            {
                e.Cancel = true;
                OpenInMain?.Invoke(e.Uri);
            }
        };
        core.SourceChanged += (_, _) => { if (current == view && core.Source.StartsWith("http")) UrlChanged?.Invoke(core.Source); };
    }

    void ShowView(WebView2 v)
    {
        current = v;
        foreach (Control c in body.Controls) c.Visible = c == v;
        LayoutHeader();
    }

    /// <summary>Приложение: своя страница, создаётся при первом открытии и дальше живёт в фоне.</summary>
    async void ShowApp(HomeTile app)
    {
        if (!appViews.TryGetValue(app.Url, out var v))
        {
            v = new WebView2 { Dock = DockStyle.Fill, DefaultBackgroundColor = Theme.Frame };
            appViews[app.Url] = v;
            body.Controls.Add(v);
            ShowView(v);
            try { await Setup(v, lite: false); v.CoreWebView2.Navigate(app.Url); } catch { }
        }
        else ShowView(v);
        UrlChanged?.Invoke(app.Url);
    }

    void ShowAddMenu()
    {
        var menu = Theme.NewMenu();
        var src = current.CoreWebView2?.Source ?? "";
        if (src.StartsWith("http") && apps.All(a => a.Url != src))
            menu.Items.Add(Theme.Item("Добавить открытый здесь сайт", () => AddApp(src)));
        menu.Items.Add(Theme.Item("Добавить сайт по адресу…", () =>
        {
            var u = InputDialog.Ask(FindForm()!, L.T("Адрес сайта для боковой панели"), "https://");
            if (string.IsNullOrWhiteSpace(u)) return;
            if (!u.Contains("://")) u = "https://" + u;
            if (Uri.TryCreate(u, UriKind.Absolute, out var x) && x.Scheme is "http" or "https") AddApp(x.ToString());
        }));
        var hidden = appButtons.Where(a => !a.btn.Visible).ToList();
        if (hidden.Count > 0)
        {
            menu.Items.Add(new ToolStripSeparator());
            foreach (var (_, app) in hidden) { var ap = app; menu.Items.Add(Theme.Item(ap.Name, () => ShowApp(ap))); }
        }
        foreach (var (name, url) in new[] { ("Discord", "https://discord.com/app"), ("YouTube Music", "https://music.youtube.com/"), ("ВКонтакте", "https://vk.com/im"), ("Gmail", "https://mail.google.com/"), ("Яндекс Почта", "https://mail.yandex.ru/") })
            if (apps.All(a => a.Url != url))
            {
                if (menu.Items.Count > 0 && menu.Items[^1] is not ToolStripSeparator && !menu.Items.OfType<ToolStripMenuItem>().Any(i => i.Tag as string == "sugg")) menu.Items.Add(new ToolStripSeparator());
                var n = name; var uu = url;
                var it = Theme.Item("＋ " + n, () => AddApp(uu, n));
                it.Tag = "sugg";
                menu.Items.Add(it);
            }
        menu.Show(add, new Point(0, add.Height + Theme.S(4)));
    }

    void AddApp(string url, string? name = null)
    {
        if (apps.Any(a => a.Url == url)) return;
        var host = Uri.TryCreate(url, UriKind.Absolute, out var u) ? u.Host.Replace("www.", "") : url;
        var title = name ?? (current.CoreWebView2?.Source == url ? current.CoreWebView2.DocumentTitle : "");
        if (string.IsNullOrWhiteSpace(title) || title.Length > 30) title = host.Split('.')[0];
        title = char.ToUpper(title[0]) + title[1..];
        var list = apps.Append(new HomeTile { Name = title, Url = url }).ToList();
        SetApps(list);
        AppsChanged?.Invoke(list);
        ShowApp(list[^1]);
    }

    void ShowAppMenu(HomeTile app, Control anchor)
    {
        var menu = Theme.NewMenu();
        menu.Items.Add(Theme.Item("Обновить", () => appViews.GetValueOrDefault(app.Url)?.CoreWebView2?.Reload()));
        menu.Items.Add(Theme.Item("Открыть во вкладке", () => OpenInMain?.Invoke(appViews.GetValueOrDefault(app.Url)?.CoreWebView2?.Source ?? app.Url)));
        menu.Items.Add(Theme.Item("Убрать из панели", () =>
        {
            var list = apps.Where(a => a != app).ToList();
            SetApps(list);
            AppsChanged?.Invoke(list);
        }));
        menu.Show(anchor, new Point(0, anchor.Height + Theme.S(4)));
    }

    public async void Navigate(string url)
    {
        // Адрес приложения из списка — открываем его «живую» страницу
        var app = apps.FirstOrDefault(a => a.Url == url);
        if (app != null) { ShowApp(app); return; }
        try
        {
            ShowView(view);
            await Init();
            view.CoreWebView2?.Navigate(url);
            UrlChanged?.Invoke(url);
        }
        catch { }
    }

    public void EnsureLoaded(string url)
    {
        if (init == null && appViews.Count == 0) Navigate(url);
    }
}
