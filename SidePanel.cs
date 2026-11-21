using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace LiteBrowser;

/// <summary>Боковая панель: закладки, история, загрузки или любой сайт рядом с основной страницей.</summary>
sealed class SidePanel : Panel
{
    public static readonly (string glyph, bool text, string tip, string url)[] Presets =
    {
        ("", false, "Закладки", LitePages.BookmarksPage),
        ("", false, "История", LitePages.History),
        ("", false, "Загрузки", LitePages.DownloadsPage),
        ("TG", true, "Telegram", "https://web.telegram.org/a/"),
        ("AI", true, "ChatGPT", "https://chatgpt.com/"),
        ("WA", true, "WhatsApp", "https://web.whatsapp.com/"),
        ("♪", true, "Яндекс Музыка", "https://music.yandex.ru/"),
    };

    readonly BufferedPanel header = new() { Dock = DockStyle.Top };
    readonly WebView2 view = new() { Dock = DockStyle.Fill };
    readonly List<IconButton> buttons = new();
    readonly IconButton custom, close;
    readonly Func<CoreWebView2, string, Task> liteHandler;
    Task? init;

    public event Action<string>? OpenInMain;
    public event Action? CloseRequested;
    public event Action<string>? UrlChanged;
    public CoreWebView2? Core => view.CoreWebView2;

    public SidePanel(ToolTip tips, Func<CoreWebView2, string, Task> liteHandler)
    {
        this.liteHandler = liteHandler;
        Dock = DockStyle.Right;
        Visible = false;
        foreach (var (glyph, text, tip, url) in Presets)
        {
            var b = new IconButton(glyph, tip, tips) { TextMode = text };
            var u = url;
            b.Click += (_, _) => Navigate(u);
            buttons.Add(b);
        }
        custom = new IconButton("", "Открыть свой сайт", tips);
        custom.Click += (_, _) =>
        {
            var u = InputDialog.Ask(FindForm()!, "Адрес сайта для боковой панели", Core?.Source?.StartsWith("http") == true ? Core.Source : "https://");
            if (string.IsNullOrWhiteSpace(u)) return;
            if (!u.Contains("://")) u = "https://" + u;
            if (Uri.TryCreate(u, UriKind.Absolute, out var x) && x.Scheme is "http" or "https") Navigate(x.ToString());
        };
        close = new IconButton("", "Закрыть панель (Ctrl+B)", tips);
        close.Click += (_, _) => CloseRequested?.Invoke();
        header.Controls.AddRange(buttons.Cast<Control>().Concat(new Control[] { custom, close }).ToArray());
        header.Resize += (_, _) => LayoutHeader();
        Controls.Add(view);
        Controls.Add(header);
        ApplyTheme();
    }

    public void ApplyTheme()
    {
        BackColor = Theme.Frame;
        header.BackColor = Theme.Toolbar;
        header.Height = Theme.S(44);
        foreach (var b in buttons.Append(custom).Append(close)) b.BackColor = Theme.Toolbar;
        view.DefaultBackgroundColor = Theme.Frame;
        LayoutHeader();
    }

    void LayoutHeader()
    {
        int s = Theme.S(32), y = (header.Height - s) / 2, x = Theme.S(6);
        foreach (var b in buttons) { b.SetBounds(x, y, s, s); x += s + Theme.S(2); }
        custom.SetBounds(x, y, s, s);
        close.SetBounds(header.Width - s - Theme.S(6), y, s, s);
    }

    Task Init() => init ??= InitCore();

    async Task InitCore()
    {
        var env = await AppEnv.GetEnvironment();
        await view.EnsureCoreWebView2Async(env);
        var core = view.CoreWebView2;
        core.Settings.IsPasswordAutosaveEnabled = false;
        core.Settings.IsGeneralAutofillEnabled = false;
        core.Settings.IsWebMessageEnabled = true;
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
        core.NewWindowRequested += (_, e) => { e.Handled = true; OpenInMain?.Invoke(e.Uri); };
        core.SourceChanged += (_, _) => { if (core.Source.StartsWith("http")) UrlChanged?.Invoke(core.Source); };
    }

    public async void Navigate(string url)
    {
        try
        {
            await Init();
            Core?.Navigate(url);
            UrlChanged?.Invoke(url);
        }
        catch { }
    }

    public void EnsureLoaded(string url)
    {
        if (init == null) Navigate(url);
    }
}
