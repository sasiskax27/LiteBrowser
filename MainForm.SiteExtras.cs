using System.Text.Json;
using Microsoft.Web.WebView2.Core;

namespace LiteBrowser;

/// <summary>Масштаб для каждого сайта, тёмная тема для любого сайта, недавно закрытые вкладки.</summary>
sealed partial class MainForm
{
    // ================= Масштаб для каждого сайта =================

    static string ZoomKey(string host) => PasswordTools.HostKey(host);

    int WantedZoom(string host) =>
        host.Length > 0 && Cfg.SiteZoom.TryGetValue(ZoomKey(host), out var z) ? z : Cfg.PageZoom;

    /// <summary>При переходе на другой сайт — его сохранённый масштаб (или общий).</summary>
    void ApplySiteZoom(BrowserTab tab)
    {
        if (tab.Core == null || tab.PageHost == LitePages.Host) return;
        int want = WantedZoom(tab.PageHost);
        tab.ExpectedZoom = want;
        if (Math.Abs(tab.View.ZoomFactor * 100 - want) > 0.5) tab.View.ZoomFactor = want / 100.0;
    }

    /// <summary>Пользователь сменил масштаб (Ctrl + колёсико, Ctrl +/−, кнопка «100%») — запоминаем для сайта.</summary>
    void OnZoomChanged(BrowserTab tab)
    {
        if (tab == active) UpdateZoomLabel();
        int pct = (int)Math.Round(tab.View.ZoomFactor * 100);
        if (Math.Abs(pct - tab.ExpectedZoom) <= 0) return; // это мы сами поставили
        tab.ExpectedZoom = pct;
        var host = tab.PageHost;
        if (Incognito || host.Length == 0 || host == LitePages.Host) return;
        var key = ZoomKey(host);
        bool isDefault = pct == Cfg.PageZoom;
        if (isDefault ? !Cfg.SiteZoom.ContainsKey(key) : Cfg.SiteZoom.GetValueOrDefault(key) == pct) return;
        CopyAndSave(x => { if (isDefault) x.SiteZoom.Remove(key); else x.SiteZoom[key] = pct; });
    }

    // ================= Тёмная тема для любого сайта =================

    /// <summary>Скрипт тёмной темы: решает по адресу, нужна ли она, и не трогает сайты, которые и так тёмные.</summary>
    string DarkScript() => DarkScriptFor(Cfg.ForceDark, Cfg.DarkSites, Cfg.DarkExclude);

    public static string DarkScriptFor(bool all, List<string> on, List<string> off)
    {
        var cfg = JsonSerializer.Serialize(new { all, on, off });
        return "(() => { const cfg = " + cfg + """
;
  if (window.top !== window) return;
  const h = location.hostname.replace(/^www\./, '');
  const has = l => l.some(d => h === d || h.endsWith('.' + d));
  if (!(cfg.all ? !has(cfg.off) : has(cfg.on))) return;
  const st = document.createElement('style');
  st.id = 'lite-dark';
  st.textContent = 'html{filter:invert(.9) hue-rotate(180deg)!important;background:#fff!important}' +
    'img,video,picture,canvas,embed,object,iframe,svg image,[style*="url("]{filter:invert(1) hue-rotate(180deg)!important}';
  (document.head || document.documentElement).appendChild(st);
  // Сайт уже тёмный сам по себе — убираем, чтобы не сделать его светлым
  const lum = c => { const m = (c || '').match(/[\d.]+/g); if (!m) return null; if (m.length > 3 && +m[3] === 0) return null; return (0.299 * m[0] + 0.587 * m[1] + 0.114 * m[2]) / 255; };
  const check = () => {
    st.disabled = true;
    let l = lum(getComputedStyle(document.body || document.documentElement).backgroundColor);
    if (l === null) l = lum(getComputedStyle(document.documentElement).backgroundColor);
    st.disabled = false;
    if (l !== null && l < 0.45) st.remove();
  };
  if (document.readyState === 'loading') document.addEventListener('DOMContentLoaded', check); else check();
})();
""";
    }

    async void ApplyDarkScript(BrowserTab tab)
    {
        var core = tab.Core;
        if (core == null) return;
        var sig = Cfg.ForceDark + "|" + string.Join(",", Cfg.DarkSites) + "|" + string.Join(",", Cfg.DarkExclude);
        if (sig == tab.DarkSig) return;
        tab.DarkSig = sig;
        try
        {
            if (tab.DarkScriptId != null) { core.RemoveScriptToExecuteOnDocumentCreated(tab.DarkScriptId); tab.DarkScriptId = null; }
            if (Cfg.ForceDark || Cfg.DarkSites.Count > 0) tab.DarkScriptId = await core.AddScriptToExecuteOnDocumentCreatedAsync(DarkScript());
        }
        catch { tab.DarkSig = ""; }
    }

    bool DarkFor(string host)
    {
        var key = PasswordTools.HostKey(host);
        return Cfg.ForceDark ? !Cfg.DarkExclude.Contains(key) : Cfg.DarkSites.Contains(key);
    }

    /// <summary>Тёмная тема для текущего сайта: вкл/выкл (с учётом общей настройки).</summary>
    void ToggleDarkForSite()
    {
        var host = active?.PageHost ?? "";
        if (host.Length == 0 || host == LitePages.Host) return;
        var key = PasswordTools.HostKey(host);
        bool on = !DarkFor(host);
        CopyAndSave(x =>
        {
            if (x.ForceDark) { if (on) x.DarkExclude.Remove(key); else if (!x.DarkExclude.Contains(key)) x.DarkExclude.Add(key); }
            else { if (on) { if (!x.DarkSites.Contains(key)) x.DarkSites.Add(key); } else x.DarkSites.Remove(key); }
        });
        foreach (var t in tabs) ApplyDarkScript(t);
        if (active?.Core != null) active.Core.Reload();
    }

    // ================= Недавно закрытые вкладки =================

    readonly List<(string url, string title)> recentlyClosed = new();

    void RememberClosed(BrowserTab tab, string url)
    {
        recentlyClosed.RemoveAll(x => x.url == url);
        recentlyClosed.Insert(0, (url, tab.Title));
        if (recentlyClosed.Count > 25) recentlyClosed.RemoveAt(recentlyClosed.Count - 1);
    }

    ToolStripMenuItem RecentlyClosedMenu()
    {
        var sub = SubMenu("Недавно закрытые", "");
        if (recentlyClosed.Count == 0)
            sub.DropDownItems.Add(new ToolStripMenuItem(L.T("Пока ничего не закрывали")) { Enabled = false });
        foreach (var (url, title) in recentlyClosed.Take(12))
        {
            var u = url;
            var text = title.Length > 0 ? title : url;
            if (text.Length > 60) text = text[..57] + "…";
            var item = new ToolStripMenuItem(text) { ForeColor = Theme.Text, Padding = new Padding(4, 4, 6, 4), ToolTipText = url };
            item.Click += (_, _) => { recentlyClosed.RemoveAll(x => x.url == u); _ = AddTab(u); };
            sub.DropDownItems.Add(item);
        }
        if (recentlyClosed.Count > 0)
        {
            sub.DropDownItems.Add(new ToolStripSeparator());
            sub.DropDownItems.Add(Theme.Item("Открыть закрытую вкладку", ReopenClosedTab, "Ctrl+Shift+T", ""));
        }
        return sub;
    }

    // ================= Вертикальные вкладки =================

    /// <summary>Вкладки сверху или слева (вертикально), в том числе свёрнутые до значков.</summary>
    void ApplyTabLayout()
    {
        bool v = Cfg.VerticalTabs && !(FormBorderStyle == FormBorderStyle.None && WindowState == FormWindowState.Maximized && !strip.Visible);
        strip.Vertical = Cfg.VerticalTabs;
        strip.Compact = Cfg.VerticalTabsCompact;
        if (Cfg.VerticalTabs)
        {
            if (strip.Dock != DockStyle.Left) strip.Dock = DockStyle.Left;
            strip.Width = Theme.S(Cfg.VerticalTabsCompact ? 56 : 250);
        }
        else
        {
            if (strip.Dock != DockStyle.Top) strip.Dock = DockStyle.Top;
            strip.Height = Theme.S(44);
        }
        strip.Invalidate();
    }

    void ToggleVerticalTabs()
    {
        bool v = !Cfg.VerticalTabs;
        CopyAndSave(x => x.VerticalTabs = v);
    }
}
