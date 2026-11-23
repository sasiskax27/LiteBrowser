namespace LiteBrowser;

/// <summary>
/// Правила окон: выбранные сайты (например, Яндекс Музыка, Telegram) всегда открываются в своём отдельном окне.
/// Открыли такой сайт в обычном окне — он переедет в своё окно (или в уже открытое окно этого сайта).
/// </summary>
sealed partial class MainForm
{
    /// <summary>Сайт, для которого это окно выделено (null — обычное окно).</summary>
    public string? DedicatedRule { get; private set; }

    internal void SetDedicated(string? rule)
    {
        DedicatedRule = rule;
        strip.Badge = rule ?? (Guest ? "Гость" : Incognito ? "Инкогнито" : Profiles.Current.Length > 0 ? Profiles.Current : null);
        strip.Invalidate();
        UpdateTitle();
    }

    internal static string? RuleFor(string host)
    {
        host = host.ToLowerInvariant();
        return Cfg.WindowRules.FirstOrDefault(r => host == r || host.EndsWith("." + r));
    }

    /// <summary>
    /// Переход на сайт с правилом в чужом окне: true — переход отменён и сайт открыт в своём окне.
    /// Перенаправления (вход через аккаунт и т. п.) не трогаем — только переходы, которые начал пользователь, и новые вкладки.
    /// </summary>
    bool RouteToRuleWindow(BrowserTab tab, Uri u, bool userInitiated)
    {
        if (Incognito || u.Scheme is not ("http" or "https") || Cfg.WindowRules.Count == 0) return false;
        var rule = RuleFor(u.Host);
        if (rule == null || rule == DedicatedRule) return false;
        bool fresh = tab.Core == null || !tab.Core.CanGoBack || tab.IsHome;
        if (!userInitiated && !fresh) return false;
        var url = u.ToString();
        BeginInvoke(() =>
        {
            BrowserApp.Instance.OpenInRuleWindow(rule, url);
            // Пустая вкладка, открытая только ради этого сайта, больше не нужна
            if (fresh && tabs.Contains(tab) && tabs.Count > 1) CloseTab(tab);
            else if (fresh && tabs.Count == 1 && tab.IsHome) { /* оставляем стартовую в обычном окне */ }
        });
        return true;
    }

    void ToggleWindowRule(BrowserTab tab)
    {
        var src = tab.Core?.Source ?? "";
        if (!Uri.TryCreate(src, UriKind.Absolute, out var u) || u.Scheme is not ("http" or "https")) return;
        var existing = RuleFor(u.Host);
        if (existing != null)
        {
            CopyAndSave(x => x.WindowRules = x.WindowRules.Where(r => r != existing).ToList());
            if (DedicatedRule == existing) SetDedicated(null);
            return;
        }
        var rule = RootDomain(u.Host.ToLowerInvariant());
        CopyAndSave(x => x.WindowRules = x.WindowRules.Append(rule).Distinct().ToList());
        // Сразу переносим вкладку в своё окно
        if (DedicatedRule != rule)
        {
            BrowserApp.Instance.OpenInRuleWindow(rule, src);
            if (tabs.Count > 1) CloseTab(tab);
            else SetDedicated(rule);
        }
    }
}
