namespace LiteBrowser;

/// <summary>
/// Звук по вкладкам: своя громкость у каждой вкладки и пульт «Сейчас играет» (кнопка ♫ появляется, когда где-то играет звук).
/// Громкость меняем у видео и аудио на странице (WebView2 не умеет громкость вкладки целиком): работает на YouTube, VK, музыкальных сайтах.
/// </summary>
sealed partial class MainForm
{
    IconButton mediaBtn = null!;
    bool showMedia;

    /// <summary>Скрипт громкости: ставит её всем &lt;video&gt;/&lt;audio&gt; и новым, которые появятся позже.</summary>
    internal static string VolumeJs(int percent) => $$"""
(() => {
  const v = {{percent}} / 100;
  window.__liteVol = v;
  const apply = el => { try { if (!el.__liteBase) el.__liteBase = 1; el.volume = Math.min(1, v); } catch (e) { } };
  document.querySelectorAll('video, audio').forEach(apply);
  if (!window.__liteVolHooked) {
    window.__liteVolHooked = true;
    document.addEventListener('play', e => { if (window.__liteVol !== undefined && e.target instanceof HTMLMediaElement) apply(e.target); }, true);
    document.addEventListener('loadedmetadata', e => { if (window.__liteVol !== undefined && e.target instanceof HTMLMediaElement) apply(e.target); }, true);
  }
  return document.querySelectorAll('video, audio').length;
})()
""";

    const string PlayPauseJs = """
(() => {
  const els = [...document.querySelectorAll('video, audio')];
  const playing = els.filter(e => !e.paused && !e.ended);
  if (playing.length) { playing.forEach(e => e.pause()); window.__litePaused = playing; return 'paused'; }
  const back = (window.__litePaused || []).filter(e => e.isConnected);
  (back.length ? back : els.slice(0, 1)).forEach(e => e.play().catch(() => { }));
  return 'playing';
})()
""";

    async Task SetTabVolume(BrowserTab tab, int percent)
    {
        tab.Volume = percent;
        if (tab.Core == null) return;
        try { await tab.Core.ExecuteScriptAsync(VolumeJs(percent)); } catch { }
        strip.Invalidate();
    }

    /// <summary>После перехода на новую страницу — вернуть выбранную громкость.</summary>
    void ReapplyVolume(BrowserTab tab)
    {
        if (tab.Volume != 100) _ = SetTabVolume(tab, tab.Volume);
    }

    async Task TogglePlay(BrowserTab tab)
    {
        if (tab.Core == null) return;
        try { await tab.Core.CallDevToolsProtocolMethodAsync("Runtime.evaluate", System.Text.Json.JsonSerializer.Serialize(new { expression = PlayPauseJs, userGesture = true, returnByValue = true })); } catch { }
    }

    ToolStripMenuItem VolumeMenu(BrowserTab tab)
    {
        var sub = Theme.Item(L.T("Громкость") + $": {tab.Volume}%", () => { }, null, "");
        foreach (var v in new[] { 100, 75, 50, 25, 10 })
        {
            int val = v;
            var it = Theme.Item($"{v}%", () => _ = SetTabVolume(tab, val));
            it.Checked = tab.Volume == v;
            sub.DropDownItems.Add(it);
        }
        return sub;
    }

    /// <summary>Пульт «Сейчас играет»: все вкладки со звуком из всех окон.</summary>
    void ShowMediaMenu()
    {
        var menu = Theme.NewMenu();
        var playing = BrowserApp.Instance.AllTabs().Where(x => x.tab.Audio || x.tab.Muted).ToList();
        if (playing.Count == 0) menu.Items.Add(new ToolStripLabel(L.T("Сейчас ничего не играет")) { ForeColor = Theme.TextDim });
        foreach (var (form, tab) in playing)
        {
            var title = tab.Title.Length > 48 ? tab.Title[..48] + "…" : tab.Title;
            menu.Items.Add(new ToolStripLabel((tab.Muted ? "🔇 " : "♫ ") + title) { ForeColor = Theme.Text, Font = Theme.GetFont("Segoe UI", 9.5f, FontStyle.Bold) });
            menu.Items.Add(Theme.Item(L.T("Пауза / продолжить"), () => _ = form.TogglePlay(tab), null, ""));
            menu.Items.Add(Theme.Item(tab.Muted ? L.T("Включить звук") : L.T("Выключить звук"), () => { if (tab.Core != null) tab.Core.IsMuted = !tab.Core.IsMuted; }, null, tab.Muted ? "" : ""));
            menu.Items.Add(form.VolumeMenu(tab));
            menu.Items.Add(Theme.Item(L.T("Мини-плеер поверх окон"), () => MiniPlayerForm.ShowFor(form, tab), null, "\uE8B2"));
            menu.Items.Add(Theme.Item(L.T("Перейти к вкладке"), () => form.ShowTabFromManager(tab), null, ""));
            menu.Items.Add(new ToolStripSeparator());
        }
        if (playing.Count > 1)
            menu.Items.Add(Theme.Item(L.T("Выключить звук везде, кроме текущей"), () =>
            {
                foreach (var (_, t) in playing) if (t != active && t.Core != null) t.Core.IsMuted = true;
            }, null, ""));
        menu.Show(mediaBtn, new Point(mediaBtn.Width - menu.PreferredSize.Width, mediaBtn.Height + Theme.S(4)));
    }

    /// <summary>Кнопка ♫ видна, пока в каком-нибудь окне играет звук.</summary>
    internal void UpdateMediaButton()
    {
        if (IsDisposed || mediaBtn == null) return;
        bool any = BrowserApp.Instance.AllTabs().Any(x => x.tab.Audio);
        if (any == showMedia) return;
        showMedia = any;
        LayoutToolbar();
    }
}
