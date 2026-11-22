using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace LiteBrowser;

/// <summary>
/// Всплывающее окошко расширений (кнопка-пазл на панели): список с переключателями,
/// а окно самого расширения открывается здесь же, как в Chrome.
/// </summary>
sealed class ExtensionsPanel : Form
{
    readonly BufferedPanel header = new() { Dock = DockStyle.Top };
    readonly WebView2 view = new() { Dock = DockStyle.Fill };
    readonly IconButton back, close, inTab;
    readonly Label title = new() { AutoSize = false, TextAlign = ContentAlignment.MiddleLeft };
    readonly Func<CoreWebView2, string, Task> liteHandler;
    Task? init;
    string? popupUrl;

    public event Action<string>? OpenInTab;
    public CoreWebView2? Core => view.CoreWebView2;

    public ExtensionsPanel(ToolTip tips, Func<CoreWebView2, string, Task> liteHandler)
    {
        this.liteHandler = liteHandler;
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        KeyPreview = true;
        back = new IconButton("", "Назад к списку", tips);
        close = new IconButton("", "Закрыть (Esc)", tips);
        inTab = new IconButton("", "Открыть во вкладке", tips);
        back.Click += (_, _) => ShowList();
        close.Click += (_, _) => Hide();
        inTab.Click += (_, _) => { if (popupUrl != null) { OpenInTab?.Invoke(popupUrl); Hide(); } };
        header.Controls.AddRange(new Control[] { back, title, inTab, close });
        header.Resize += (_, _) => LayoutHeader();
        Controls.Add(view);
        Controls.Add(header);
        Deactivate += (_, _) => Hide();
        KeyDown += (_, e) => { if (e.KeyCode == Keys.Escape) Hide(); };
        ApplyTheme();
    }

    protected override CreateParams CreateParams
    {
        get { var cp = base.CreateParams; cp.ExStyle |= 0x80; cp.ClassStyle |= 0x20000; return cp; } // окно-инструмент с тенью
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        try { Theme.StylePopup(Handle); } catch { }
    }

    public void ApplyTheme()
    {
        BackColor = Theme.Menu;
        header.BackColor = Theme.Menu;
        header.Height = Theme.S(42);
        foreach (var b in new[] { back, close, inTab }) b.BackColor = Theme.Menu;
        title.ForeColor = Theme.Text;
        title.BackColor = Theme.Menu;
        title.Font = Theme.GetFont("Segoe UI", 10.5f, FontStyle.Bold);
        view.DefaultBackgroundColor = Theme.Menu;
        LayoutHeader();
    }

    void LayoutHeader()
    {
        int s = Theme.S(32), y = (header.Height - s) / 2;
        int x = Theme.S(6);
        if (back.Visible) { back.SetBounds(x, y, s, s); x += s + Theme.S(4); } else x += Theme.S(8);
        close.SetBounds(header.Width - s - Theme.S(6), y, s, s);
        int right = close.Left - Theme.S(2);
        if (inTab.Visible) { inTab.SetBounds(right - s, y, s, s); right -= s + Theme.S(2); }
        title.SetBounds(x, 0, Math.Max(10, right - x), header.Height);
    }

    Task Init() => init ??= InitCore();

    async Task InitCore()
    {
        var env = await AppEnv.GetEnvironment();
        await view.EnsureCoreWebView2Async(env);
        var core = view.CoreWebView2;
        // Escape закрывает окошко, даже когда фокус внутри страницы (клавиши получает движок, а не окно)
        var field = typeof(WebView2).GetField("_coreWebView2Controller", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        if (field?.GetValue(view) is CoreWebView2Controller ctl)
            ctl.AcceleratorKeyPressed += (_, e) =>
            {
                if (e.KeyEventKind == CoreWebView2KeyEventKind.KeyDown && e.VirtualKey == (uint)Keys.Escape) { e.Handled = true; BeginInvoke(Hide); }
            };
        core.Settings.IsPasswordAutosaveEnabled = false;
        core.Settings.IsGeneralAutofillEnabled = false;
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
        // Ссылки и новые окна из расширения — во вкладку основного окна
        core.NewWindowRequested += (_, e) => { e.Handled = true; OpenInTab?.Invoke(e.Uri); Hide(); };
        core.NavigationStarting += (_, e) =>
        {
            if (e.Uri.StartsWith("http", StringComparison.OrdinalIgnoreCase) && !e.Uri.StartsWith(LitePages.Prefix))
            {
                e.Cancel = true;
                OpenInTab?.Invoke(e.Uri);
                Hide();
            }
        };
        // window.close() из окна расширения
        core.WindowCloseRequested += (_, _) => ShowList();
        // Окно расширения — подгоняем окошко под его размер (как в Chrome)
        core.NavigationCompleted += async (_, _) =>
        {
            if (popupUrl == null || !core.Source.StartsWith("chrome-extension://")) return;
            foreach (var delay in new[] { 150, 700 })
            {
                await Task.Delay(delay);
                if (popupUrl == null || !Visible) return;
                try
                {
                    // Ширина — по правому краю содержимого (scrollWidth равен ширине окна, если содержимое уже)
                    var r = await core.ExecuteScriptAsync("(() => { const b = document.body; if (!b) return '[0,0]'; let right = 0; for (const e of b.querySelectorAll('body > *')) { const x = e.getBoundingClientRect(); if (x.width > 0) right = Math.max(right, x.right); } right = Math.max(right, b.getBoundingClientRect().right > innerWidth - 2 ? 0 : b.getBoundingClientRect().right); return JSON.stringify([Math.ceil(right || document.documentElement.scrollWidth), Math.max(document.documentElement.scrollHeight, b.scrollHeight)]); })()");
                    var json = System.Text.Json.JsonSerializer.Deserialize<string>(r) ?? "[0,0]";
                    var wh = System.Text.Json.JsonSerializer.Deserialize<int[]>(json) ?? new[] { 0, 0 };
                    if (wh[0] < 100 || wh[1] < 60 || anchor == null) continue;
                    // Размеры страницы — в CSS-пикселях, окно — в пикселях экрана
                    double z = view.ZoomFactor * DeviceDpi / 96.0;
                    int w = Math.Clamp((int)(wh[0] * z) + Theme.S(4), Theme.S(260), Theme.S(800));
                    int h = Math.Clamp((int)(wh[1] * z) + header.Height + Theme.S(4), Theme.S(200), Theme.S(620));
                    Place(anchor, w, h);
                }
                catch { }
            }
        };
    }

    void Place(Control anchor, int w, int h)
    {
        var p = anchor.PointToScreen(new Point(anchor.Width, anchor.Height + Theme.S(4)));
        var wa = Screen.FromControl(anchor).WorkingArea;
        int x = Math.Clamp(p.X - w, wa.Left + Theme.S(4), wa.Right - w - Theme.S(4));
        int y = Math.Min(p.Y, wa.Bottom - h - Theme.S(4));
        Bounds = new Rectangle(x, y, w, h);
    }

    Control? anchor;

    public async void ShowBelow(Control anchorControl)
    {
        anchor = anchorControl;
        ApplyTheme();
        Place(anchorControl, Theme.S(380), Theme.S(500));
        ShowListHeader();
        if (!Visible) Show(anchorControl.FindForm());
        Activate();
        try
        {
            await Init();
            view.CoreWebView2.Navigate(LitePages.ExtPanel);
        }
        catch { }
    }

    void ShowListHeader()
    {
        popupUrl = null;
        back.Visible = inTab.Visible = false;
        title.Text = L.T("Расширения");
        LayoutHeader();
    }

    public void ShowList()
    {
        ShowListHeader();
        if (anchor != null) Place(anchor, Theme.S(380), Theme.S(500));
        try { view.CoreWebView2?.Navigate(LitePages.ExtPanel); } catch { }
    }

    /// <summary>Открыть окно расширения (popup) прямо в этом окошке.</summary>
    public void ShowPopup(string name, string url)
    {
        popupUrl = url;
        back.Visible = inTab.Visible = true;
        title.Text = name;
        if (anchor != null) Place(anchor, Theme.S(420), Theme.S(600));
        LayoutHeader();
        try { view.CoreWebView2?.Navigate(url); } catch { }
    }
}
