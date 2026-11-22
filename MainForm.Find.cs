using System.Text;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Text.Json;
using System.Web;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace LiteBrowser;

/// <summary>Поиск по странице и подсказки адресной строки.</summary>
sealed partial class MainForm
{
    // ================= Поиск по странице =================

    void PlaceFindBar()
    {
        if (findBar == null) return;
        findBar.Location = new Point(content.Width - findBar.Width - Theme.S(20), Theme.S(8));
    }

    void ShowFind()
    {
        if (active?.Core == null) return;
        findBar.ApplyTheme();
        PlaceFindBar();
        findBar.Visible = true;
        findBar.BringToFront();
        findBar.Box.Focus();
        findBar.Box.SelectAll();
        if (findBar.Box.Text.Length > 0) _ = RunFind(findBar.Box.Text);
    }

    async Task RunFind(string term)
    {
        var core = active?.Core;
        if (core == null) return;
        if (term.Length == 0) { core.Find.Stop(); findBar.SetCount(0, 0); return; }
        var env = await AppEnv.GetEnvironment();
        var o = env.CreateFindOptions();
        o.FindTerm = term;
        o.SuppressDefaultFindDialog = true;
        o.ShouldHighlightAllMatches = true;
        await core.Find.StartAsync(o);
        findBar.SetCount(core.Find.ActiveMatchIndex + 1, core.Find.MatchCount);
    }

    void CloseFind()
    {
        if (!findBar.Visible) return;
        try { active?.Core?.Find.Stop(); } catch { }
        findBar.Visible = false;
        active?.View.Focus();
    }

    // ================= Подсказки адресной строки =================

    const string EnableSuggestUrl = "lite-action:enable-suggest";

    /// <summary>Выбор в подсказках: особая строка «Включить подсказки» или переход по адресу.</summary>
    bool HandleSuggestAction(string url)
    {
        if (url != EnableSuggestUrl) return false;
        CopyAndSave(x => x.SearchSuggestions = true);
        address.Box.Focus();
        address.Box.SelectionStart = address.Box.TextLength;
        UpdateSuggestions();
        return true;
    }

    void UpdateSuggestions()
    {
        if (!address.Box.Focused) { suggest.Hide(); return; }
        var q = address.Box.Text.Trim();
        if (q.Length == 0 || q.StartsWith("lite://")) { suggest.Hide(); return; }
        var engine = SearchEngines.Get(Cfg.Engine);
        bool looksUrl = !q.Contains(' ') && q.Contains('.') && !q.EndsWith('.');
        var top = new List<SuggestPopup.Item>();
        if (looksUrl) top.Add(new("\uE774", q, L.T("перейти на сайт"), ToUrl(q)));
        top.Add(new("\uE721", q, L.T("поиск в ") + engine.Name, engine.Url + Uri.EscapeDataString(q)));
        var seen = new HashSet<string>(top.Select(x => x.Url));
        var local = new List<SuggestPopup.Item>();
        foreach (var b in Store.Bookmarks.Where(b => b.Title.Contains(q, StringComparison.OrdinalIgnoreCase) || b.Url.Contains(q, StringComparison.OrdinalIgnoreCase)).Take(2))
            if (seen.Add(b.Url)) local.Add(new("\uE734", b.Title, b.Url, b.Url));
        if (!Incognito)
            foreach (var h in Store.SearchHistory(q, 60).Where(h => h.Url.Length < 500).DistinctBy(h => h.Url).DistinctBy(h => h.Title).Take(8))
            {
                if (local.Count >= 4) break;
                if (seen.Add(h.Url)) local.Add(new("\uE81C", h.Title.Length > 0 ? h.Title : h.Url, h.Url, h.Url));
            }
        bool canRemote = !Incognito && !looksUrl;
        // Подсказки поисковика выключены — предлагаем включить одной строкой (запросы уйдут поисковику по мере ввода)
        var tail = new List<SuggestPopup.Item>();
        if (canRemote && !Cfg.SearchSuggestions)
            tail.Add(new("\uE946", L.T("Включить подсказки ") + engine.Name, L.T("то, что вы печатаете, будет уходить поисковику"), EnableSuggestUrl));
        suggest.ShowItems(address, top.Concat(local).Concat(tail).ToList());

        suggestCts?.Cancel();
        if (!Cfg.SearchSuggestions || !canRemote) return;
        var cts = suggestCts = new CancellationTokenSource();
        _ = Task.Run(async () =>
        {
            await Task.Delay(120, cts.Token);
            var remote = await RemoteSuggest.Get(engine.Id, q, cts.Token);
            if (cts.IsCancellationRequested || remote.Count == 0) return;
            BeginInvoke(() =>
            {
                if (cts.IsCancellationRequested || address.Box.Text.Trim() != q || !address.Box.Focused) return;
                // Как в Chrome: сначала поиск и подсказки поисковика, потом история и закладки
                var merged = top.ToList();
                foreach (var r in remote.Where(r => !r.Equals(q, StringComparison.OrdinalIgnoreCase)).Take(6))
                    merged.Add(new("\uE721", r, "", engine.Url + Uri.EscapeDataString(r)));
                merged.AddRange(local.Take(Math.Max(0, 10 - merged.Count)));
                suggest.ShowItems(address, merged);
            });
        });
    }

}
