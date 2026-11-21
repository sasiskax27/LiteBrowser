using System.Text;
using System.Text.Json;

namespace LiteBrowser;

/// <summary>Контекст для ИИ: все открытые вкладки и поиск по истории.</summary>
sealed partial class MainForm
{
    /// <summary>Текст всех открытых сайтов (у каждой вкладки — своя доля бюджета). Незагруженные — только название и адрес.</summary>
    async Task<(string context, string title)> TabsContext(int budget)
    {
        var web = tabs.Where(t => (t.Core?.Source ?? t.PendingUrl ?? "").StartsWith("http")).Take(10).ToList();
        if (web.Count == 0) return ("", "");
        int per = Math.Max(600, budget / web.Count);
        var sb = new StringBuilder();
        int n = 0;
        foreach (var t in web)
        {
            n++;
            string body = "", url = t.Core?.Source ?? t.PendingUrl ?? "";
            if (t.Core != null && !t.Frozen)
            {
                try
                {
                    var json = JsonSerializer.Deserialize<string>(await t.Core.ExecuteScriptAsync(AiService.ContextScript)) ?? "{}";
                    using var d = JsonDocument.Parse(json);
                    body = d.RootElement.GetProperty("text").GetString() ?? "";
                }
                catch { }
            }
            if (body.Length > per) body = body[..per] + "…";
            sb.Append($"<page>\nВкладка {n}: {t.Title}\nАдрес: {url}\n\n{(body.Length > 0 ? body : "(вкладка не загружена — известны только название и адрес)")}\n</page>\n\n");
        }
        return (sb.ToString(), $"{web.Count} вкладок");
    }

    /// <summary>Записи истории, похожие на запрос: слова запроса (по основам) ищутся в названии и адресе, свежие — выше.</summary>
    static string HistoryContext(string query, int budget)
    {
        var stems = HistoryStems(query);
        var now = DateTime.Now;
        var seen = new HashSet<string>();
        var scored = new List<(double score, HistoryEntry h)>();
        foreach (var h in Store.History.AsEnumerable().Reverse())
        {
            if (!seen.Add(h.Url)) continue;
            var hay = (h.Title + " " + h.Url).ToLowerInvariant();
            int hits = stems.Count(s => hay.Contains(s, StringComparison.Ordinal));
            if (stems.Count > 0 && hits == 0) continue;
            double age = Math.Max(0, (now - h.Time).TotalDays);
            scored.Add((hits * 10 + 5 / (1 + age), h));
        }
        var top = scored.OrderByDescending(x => x.score).Take(60).ToList();
        if (top.Count == 0) return "";
        var sb = new StringBuilder("<history>\n");
        int i = 0;
        foreach (var (_, h) in top)
        {
            var line = $"{++i}. {h.Time:dd.MM.yyyy HH:mm} — {(h.Title.Length > 0 ? h.Title : "(без названия)")} — {h.Url}\n";
            if (sb.Length + line.Length > budget) break;
            sb.Append(line);
        }
        sb.Append("</history>\nСегодня: ").Append(now.ToString("dd.MM.yyyy, dddd"));
        return sb.ToString();
    }

    static readonly HashSet<string> StopWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "про", "что", "где", "как", "это", "был", "была", "было", "были", "для", "или", "найди", "найти", "покажи", "статья", "статью", "сайт",
        "страница", "страницу", "которую", "который", "которая", "смотрел", "читал", "открывал", "неделе", "недавно", "вчера", "сегодня", "прошлой",
        "the", "and", "about", "with", "page", "site",
    };

    /// <summary>Основы слов запроса: первые 5 букв (грубо отсекает окончания: «видеокарты» → «видео»), без служебных слов.</summary>
    public static List<string> HistoryStems(string query) =>
        query.ToLowerInvariant().Split(new[] { ' ', ',', '.', '?', '!', '«', '»', '"', ':', ';', '(', ')' }, StringSplitOptions.RemoveEmptyEntries)
            .Where(w => w.Length >= 3 && !StopWords.Contains(w))
            .Select(w => w.Length > 6 ? w[..5] : w)
            .Distinct().ToList();
}
