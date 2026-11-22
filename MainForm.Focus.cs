namespace LiteBrowser;

/// <summary>
/// Фокус-режим: на заданное время отвлекающие сайты (соцсети, видео) не открываются.
/// Можно «всё равно открыть» сайт на 5 минут или закончить фокус досрочно.
/// </summary>
sealed partial class MainForm
{
    static DateTime? focusUntil;                                   // общий для всех окон
    static readonly Dictionary<string, DateTime> focusAllowed = new(StringComparer.OrdinalIgnoreCase);

    static bool FocusActive => focusUntil is { } u && u > DateTime.Now;

    static bool FocusBlocks(string host)
    {
        if (!FocusActive || host.Length == 0) return false;
        var h = host.StartsWith("www.") ? host[4..] : host;
        bool listed = AppEnv.Settings.FocusSites.Any(d => h.Equals(d, StringComparison.OrdinalIgnoreCase) || h.EndsWith("." + d, StringComparison.OrdinalIgnoreCase));
        if (!listed) return false;
        var key = PasswordTools.HostKey(h);
        return !(focusAllowed.TryGetValue(key, out var until) && until > DateTime.Now);
    }

    string FocusPageUrl(string url, string host) =>
        LitePages.Prefix + "focus.html?host=" + Uri.EscapeDataString(host) + "&url=" + Uri.EscapeDataString(url) +
        "&until=" + new DateTimeOffset(focusUntil!.Value).ToUnixTimeMilliseconds();

    void StartFocus(int minutes)
    {
        focusUntil = DateTime.Now.AddMinutes(minutes);
        focusAllowed.Clear();
        UpdateFocusButton();
        // Уже открытые отвлекающие вкладки тоже закрываем экраном фокуса
        foreach (var t in tabs)
            if (t.Core != null && FocusBlocks(t.PageHost) && Uri.TryCreate(t.Core.Source, UriKind.Absolute, out _))
                t.Core.Navigate(FocusPageUrl(t.Core.Source, t.PageHost));
    }

    void EndFocus()
    {
        focusUntil = null;
        focusAllowed.Clear();
        UpdateFocusButton();
    }

    System.Windows.Forms.Timer? focusTimer;

    void UpdateFocusButton()
    {
        bool on = FocusActive;
        showFocus = on;
        if (on)
        {
            focusBtn.Glyph = "◉ " + FocusLeftText();
            focusTimer ??= new System.Windows.Forms.Timer { Interval = 20_000 };
            focusTimer.Tick -= FocusTick;
            focusTimer.Tick += FocusTick;
            focusTimer.Start();
        }
        else focusTimer?.Stop();
        LayoutToolbar();
    }

    static string FocusLeftText()
    {
        var left = (focusUntil ?? DateTime.Now) - DateTime.Now;
        return left.TotalMinutes >= 60 ? $"{(int)left.TotalHours}:{left.Minutes:00}" : $"{Math.Max(1, (int)Math.Ceiling(left.TotalMinutes))} " + L.T("мин");
    }

    void FocusTick(object? s, EventArgs e)
    {
        if (!FocusActive && focusUntil != null)
        {
            focusUntil = null;
            infoBar.ShowBar("focus", "", L.T("Время фокуса закончилось — отличная работа!"), ("OK", () => infoBar.HideBar(), true));
        }
        UpdateFocusButton();
    }

    ToolStripMenuItem FocusMenu()
    {
        var sub = SubMenu(FocusActive ? L.T("Фокус-режим") + " — " + FocusLeftText() : "Фокус-режим", "");
        if (FocusActive)
        {
            sub.DropDownItems.Add(Theme.Item("Продлить на 15 минут", () => { focusUntil = focusUntil!.Value.AddMinutes(15); UpdateFocusButton(); }, null, ""));
            sub.DropDownItems.Add(Theme.Item("Закончить фокус", EndFocus, null, ""));
        }
        else
            foreach (var (min, title) in new[] { (25, "25 минут (помидор)"), (50, "50 минут"), (60, "1 час"), (120, "2 часа"), (240, "4 часа") })
            {
                var m = min;
                sub.DropDownItems.Add(Theme.Item(title, () => StartFocus(m), null, ""));
            }
        sub.DropDownItems.Add(new ToolStripSeparator());
        sub.DropDownItems.Add(Theme.Item("Какие сайты блокировать…", () => OpenLitePage(LitePages.Settings, "#tools"), null, ""));
        return sub;
    }

    void ShowFocusButtonMenu()
    {
        var menu = Theme.NewMenu();
        menu.ShowImageMargin = true;
        foreach (var it in FocusMenu().DropDownItems.Cast<ToolStripItem>().ToList()) menu.Items.Add(it);
        menu.Show(focusBtn, new Point(focusBtn.Width - menu.PreferredSize.Width, focusBtn.Height + Theme.S(4)));
    }

    /// <summary>Адреса lite.home/focus-allow и /focus-end — только со страницы фокуса.</summary>
    bool HandleFocusNavigation(Microsoft.Web.WebView2.Core.CoreWebView2 core, Uri u, System.Collections.Specialized.NameValueCollection qs)
    {
        if (u.AbsolutePath is not ("/focus-allow" or "/focus-end")) return false;
        if (!core.Source.StartsWith(LitePages.Prefix + "focus.html", StringComparison.Ordinal)) return true;
        var target = qs["url"];
        if (u.AbsolutePath == "/focus-end") EndFocus();
        else if (target != null && Uri.TryCreate(target, UriKind.Absolute, out var t1))
            focusAllowed[PasswordTools.HostKey(t1.Host.StartsWith("www.") ? t1.Host[4..] : t1.Host)] = DateTime.Now.AddMinutes(5);
        if (target != null && Uri.TryCreate(target, UriKind.Absolute, out var t) && t.Scheme is "http" or "https")
            BeginInvoke(() => core.Navigate(t.ToString()));
        return true;
    }
}
