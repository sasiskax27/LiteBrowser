namespace LiteBrowser;

/// <summary>
/// Прокси по сайтам одной кнопкой: меню кнопки обхода блокировок — «Этот сайт через прокси», включить/выключить, настройки.
/// Движок применяет прокси только при запуске, поэтому после изменения предлагаем перезапуск (вкладки вернутся).
/// </summary>
sealed partial class MainForm
{
    void ShowProxyMenu()
    {
        var menu = Theme.NewMenu();
        var host = ActiveHostForProxy();
        bool configured = Cfg.ProxyHost.Length > 0 && Cfg.ProxyPort > 0;
        string state = Cfg.ProxyMode switch
        {
            "all" => L.T("Все сайты через прокси"),
            "list" => L.T("Через прокси — сайты из списка") + $" ({Cfg.ProxySites.Count})",
            _ => L.T("Прокси выключен"),
        };
        menu.Items.Add(new ToolStripLabel(state + $" · {Cfg.ProxyHost}:{Cfg.ProxyPort}") { ForeColor = Theme.TextDim });
        menu.Items.Add(new ToolStripSeparator());
        if (host != null && configured && Cfg.ProxyMode != "all")
        {
            bool inList = Cfg.ProxySites.Any(s => host == s || host.EndsWith("." + s));
            menu.Items.Add(Theme.Item(inList ? L.T("Открывать ") + host + L.T(" напрямую") : L.T("Открывать ") + host + L.T(" через прокси"), () =>
            {
                var root = RootDomain(host);
                CopyAndSave(x =>
                {
                    if (inList) x.ProxySites = x.ProxySites.Where(s => !(host == s || host.EndsWith("." + s))).ToList();
                    else { x.ProxySites = x.ProxySites.Append(root).Distinct().ToList(); if (x.ProxyMode == "off") x.ProxyMode = "list"; }
                });
                OfferProxyRestart();
            }, null, inList ? "" : ""));
        }
        if (configured)
        {
            if (Cfg.ProxyMode == "off")
                menu.Items.Add(Theme.Item(L.T("Включить для сайтов из списка"), () => { CopyAndSave(x => x.ProxyMode = "list"); OfferProxyRestart(); }, null, ""));
            if (Cfg.ProxyMode != "all")
                menu.Items.Add(Theme.Item(L.T("Все сайты через прокси"), () => { CopyAndSave(x => x.ProxyMode = "all"); OfferProxyRestart(); }, null, ""));
            if (Cfg.ProxyMode != "off")
                menu.Items.Add(Theme.Item(L.T("Выключить прокси"), () => { CopyAndSave(x => x.ProxyMode = "off"); OfferProxyRestart(); }, null, ""));
        }
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(Theme.Item(L.T("Настройки обхода блокировок"), () => OpenLitePage(LitePages.Settings, "#proxy"), null, ""));
        menu.Show(proxyBtn, new Point(proxyBtn.Width - menu.PreferredSize.Width, proxyBtn.Height + Theme.S(4)));
    }

    string? ActiveHostForProxy()
    {
        var src = active?.Core?.Source;
        if (src == null || !src.StartsWith("http") || src.StartsWith(LitePages.Prefix)) return null;
        try { return new Uri(src).Host.ToLowerInvariant(); } catch { return null; }
    }

    /// <summary>youtube.com из www.youtube.com / m.youtube.com — чтобы в список попал сайт целиком.</summary>
    internal static string RootDomain(string host)
    {
        var parts = host.Split('.');
        if (parts.Length <= 2) return host;
        // co.uk, com.ru и т. п.: если предпоследняя часть короткая — берём три части
        int take = parts[^1].Length == 2 && parts[^2] is "co" or "com" or "net" or "org" or "gov" or "edu" or "ac" or "msk" or "spb" ? 3 : 2;
        return string.Join('.', parts[^take..]);
    }

    void OfferProxyRestart()
    {
        if (!AppEnv.RestartNeeded) { infoBar.HideBar(); return; }
        infoBar.ShowBar("proxy", "", L.T("Прокси изменится после перезапуска браузера — вкладки вернутся"),
            (L.T("Перезапустить"), () => BrowserApp.Instance.Restart(), true));
    }
}
