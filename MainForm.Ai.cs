using System.Text;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Text.Json;
using System.Web;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace LiteBrowser;

/// <summary>ИИ-помощник Lite AI.</summary>
sealed partial class MainForm
{
    // ================= ИИ-помощник =================

    CancellationTokenSource? aiCts;
    readonly List<AiMessage> aiHistory = new();

    async Task SendAiStatus(CoreWebView2 page)
    {
        var s = Cfg;
        List<string> models = new();
        string? error = null;
        bool ok;
        if (s.AiProvider == "local")
        {
            var m = LocalAi.Find(s.AiLocalModel);
            ok = m != null && LocalAi.IsDownloaded(m);
            if (!ok) error = "Скачайте модель, чтобы начать — это нужно один раз";
        }
        else
        {
            try { models = await AiService.Create(s).ModelsAsync(CancellationToken.None); ok = true; }
            catch (Exception ex)
            {
                ok = false;
                error = s.AiProvider == "ollama"
                    ? "Ollama не отвечает. Запустите её (установка: ollama.com) и скачайте модель командой: ollama pull qwen2.5"
                    : "Сервер не отвечает: " + ex.Message;
            }
            if (ok && s.AiModel.Length == 0 && models.Count > 0) { var first = models[0]; CopyAndSave(x => x.AiModel = first); }
        }
        Post(page, new
        {
            type = "aiStatus", enabled = s.AiEnabled, provider = s.AiProvider, model = Cfg.AiModel, ok, error, models,
            ollamaUrl = s.AiOllamaUrl, baseUrl = s.AiBaseUrl, hasKey = SecureStorage.Has("ai-key"), consent = s.AiCloudConsent,
            localModel = s.AiLocalModel, gpu = s.AiGpu, backend = LocalAi.LoadedBackend,
            downloading = LocalAi.DownloadingId, progress = LocalAi.DownloadProgress, localStatus = LocalAi.Status,
            local = LocalAi.Models.Select(m => new { id = m.Id, name = m.Name, desc = m.Description, size = m.SizeMb, downloaded = LocalAi.IsDownloaded(m) }),
        });
    }

    async Task HandleAiMessage(CoreWebView2 page, string type, JsonElement root)
    {
        string Str(string name) => root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
        bool Bool(string name) => root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;
        switch (type)
        {
            case "ai:status":
                await SendAiStatus(page);
                break;
            case "ai:config":
                var key = Str("key");
                if (key.Length > 0) SecureStorage.Set("ai-key", key.Trim());
                CopyAndSave(x =>
                {
                    if (Str("provider").Length > 0) x.AiProvider = Str("provider");
                    if (Str("localModel").Length > 0) x.AiLocalModel = Str("localModel");
                    if (root.TryGetProperty("gpu", out _)) x.AiGpu = Bool("gpu");
                    if (Str("ollamaUrl").Length > 0) x.AiOllamaUrl = Str("ollamaUrl");
                    if (Str("baseUrl").Length > 0) x.AiBaseUrl = Str("baseUrl");
                    if (root.TryGetProperty("model", out _)) x.AiModel = Str("model");
                });
                await SendAiStatus(page);
                break;
            case "ai:download":
                if (LocalAi.Find(Str("id")) is { } dm)
                {
                    CopyAndSave(x => x.AiLocalModel = dm.Id);
                    _ = LocalAi.DownloadAsync(dm, Cfg.AiGpu);
                }
                await SendAiStatus(page);
                break;
            case "ai:cancelDownload":
                LocalAi.CancelDownload();
                break;
            case "ai:deleteModel":
                if (LocalAi.Find(Str("id")) is { } del) LocalAi.Delete(del);
                await SendAiStatus(page);
                break;
            case "ai:consent":
                CopyAndSave(x => x.AiCloudConsent = true);
                break;
            case "ai:reset":
                aiHistory.Clear();
                break;
            case "ai:stop":
                aiCts?.Cancel();
                break;
            // ---- Ответ ИИ — в заметки к странице ----
            case "ai:note":
                var noteText = Str("text");
                if (noteText.Length is > 0 and < 20000 && active?.Core?.Source is { } nurl && nurl.StartsWith("http"))
                {
                    Store.AddHighlight(new Highlight { Url = Store.PageKey(nurl), Title = active.Title, Note = "✦ " + Str("question") + "\n\n" + noteText });
                    Post(page, new { type = "aiNoted" });
                }
                else Post(page, new { type = "aiError", text = L.T("Откройте страницу, к которой сохранить заметку") });
                break;

            case "ai:ask":
                await AskAi(page, Str("prompt").Trim(), Str("action"), Bool("usePage"));
                break;
        }
    }

    async Task AskAi(CoreWebView2 page, string prompt, string action, bool usePage)
    {
        var s = Cfg;
        if (!s.AiEnabled) { Post(page, new { type = "aiError", text = "ИИ-помощник выключен в настройках" }); return; }
        bool needsPage = usePage || action.Length > 0;
        if (s.AiProvider == "openai" && needsPage && !s.AiCloudConsent) { Post(page, new { type = "aiNeedConsent" }); return; }
        if (s.AiProvider != "local" && s.AiModel.Length == 0) { Post(page, new { type = "aiError", text = "Выберите модель в настройках помощника" }); return; }
        if (s.AiProvider == "local" && !(LocalAi.Find(s.AiLocalModel) is { } lm && LocalAi.IsDownloaded(lm)))
        { Post(page, new { type = "aiError", text = "Сначала скачайте модель (кнопка выше)" }); return; }

        // Контекст страницы — из активной вкладки основного окна; это недоверенные данные
        string context = "", title = "";
        // Пересказ страницы YouTube-видео — по субтитрам, а не по тексту страницы
        if (action == "summarize" && active?.Core?.Source is { } ytUrl && VideoContext.IsYouTubeVideo(ytUrl)) action = "video";
        if (action == "video")
        {
            if (active?.Core is not { } vcore || !VideoContext.IsYouTubeVideo(vcore.Source)) { Post(page, new { type = "aiError", text = L.T("Откройте видео на YouTube в активной вкладке") }); return; }
            VideoContext.Result v;
            try { v = await VideoContext.GetAsync(vcore); }
            catch { Post(page, new { type = "aiError", text = L.T("Не удалось прочитать страницу видео") }); return; }
            if (v.Error == "noplayer") { Post(page, new { type = "aiError", text = L.T("Плеер ещё не загрузился — подождите пару секунд") }); return; }
            int vbudget = s.AiProvider == "local" ? 8000 : 60000;
            var body = v.Text.Length > 0
                ? "Субтитры" + (v.Lang.Length > 0 ? $" ({v.Lang})" : "") + ":\n" + VideoContext.Fit(v.Text, vbudget)
                : "Субтитров у видео нет. Описание видео:\n" + v.Description;
            title = v.Title;
            context = $"<page>\nВидео: {v.Title}\nКанал: {v.Author}\nДлительность: {v.Length / 60}:{v.Length % 60:00}\n\n{body}\n</page>";
        }
        else if (action is "tabs" or "history")
        {
            if (action == "history" && prompt.Length == 0) { Post(page, new { type = "aiError", text = "Напишите, что искать в истории" }); return; }
            int budget = s.AiProvider == "local" ? 9000 : 40000;
            (context, title) = action == "tabs" ? await TabsContext(budget) : (HistoryContext(prompt, budget), "История");
            if (context.Length == 0) { Post(page, new { type = "aiError", text = action == "tabs" ? "Нет открытых сайтов" : "В истории ничего похожего не нашлось" }); return; }
        }
        else if (needsPage && active?.Core is { } pdfCore && (pdfCore.Source.StartsWith("http") || pdfCore.Source.StartsWith("file:")) && await PdfContext.IsPdfAsync(pdfCore))
        {
            // PDF во вкладке: текст берём из самого документа
            bool selAction = action is "explain" or "translate" or "rewrite";
            if (selAction) { Post(page, new { type = "aiError", text = L.T("В PDF выделение недоступно — задайте вопрос по документу") }); return; }
            try
            {
                int budget = s.AiProvider == "local" ? 9000 : 60000;
                var (ptitle, ptext, pages) = await PdfContext.ReadAsync(pdfCore.Source, budget);
                if (ptext.Trim().Length == 0) { Post(page, new { type = "aiError", text = L.T("В этом PDF нет текста (скорее всего, это сканы страниц)") }); return; }
                title = ptitle.Length > 0 ? ptitle : (active?.Title ?? "PDF");
                context = $"<page>\nPDF-документ: {title}\nСтраниц: {pages}\nАдрес: {pdfCore.Source}\n\n{ptext}\n</page>";
            }
            catch (Exception ex) { Post(page, new { type = "aiError", text = L.T("Не удалось прочитать PDF: ") + ex.Message }); return; }
        }
        else if (needsPage && active?.Core is { } tabCore && tabCore.Source.StartsWith("http"))
        {
            try
            {
                var json = JsonSerializer.Deserialize<string>(await tabCore.ExecuteScriptAsync(AiService.ContextScript)) ?? "{}";
                using var d = JsonDocument.Parse(json);
                title = d.RootElement.GetProperty("title").GetString() ?? "";
                var url = d.RootElement.GetProperty("url").GetString() ?? "";
                var sel = d.RootElement.GetProperty("selection").GetString() ?? "";
                var text = d.RootElement.GetProperty("text").GetString() ?? "";
                bool selAction = action is "explain" or "translate" or "rewrite";
                if (selAction && sel.Length == 0) { Post(page, new { type = "aiError", text = "Сначала выделите текст на странице" }); return; }
                var body = selAction ? sel : (sel.Length > 0 && action.Length == 0 ? $"Выделенный фрагмент:\n{sel}\n\nТекст страницы:\n{text}" : text);
                if (s.AiProvider == "local") body = body.Length > 9000 ? body[..9000] : body; // помещаемся в контекст модели
                context = $"<page>\nНазвание: {title}\nАдрес: {url}\n\n{body}\n</page>";
            }
            catch { }
        }
        else if (needsPage && action.Length > 0) { Post(page, new { type = "aiError", text = "Откройте сайт в активной вкладке" }); return; }

        var instruction = AiService.ActionPrompt(action);
        var userText = string.Join("\n\n", new[] { context, instruction, prompt }.Where(x => x.Length > 0));
        if (userText.Length == 0) return;
        var messages = new List<AiMessage> { new("system", AiService.SystemPrompt) };
        messages.AddRange(aiHistory.TakeLast(8));
        messages.Add(new AiMessage("user", userText));

        aiCts?.Cancel();
        var cts = aiCts = new CancellationTokenSource();
        var shown = prompt.Length > 0 ? prompt : action switch
        {
            "summarize" => "Перескажи страницу", "keypoints" => "Ключевые мысли страницы", "explain" => "Объясни выделенное",
            "translate" => "Переведи выделенное", "rewrite" => "Перепиши выделенное", "code" => "Объясни код", "search" => "Что поискать по теме",
            "tabs" => "Сравни открытые вкладки", "video" => "Перескажи видео", _ => prompt,
        };
        Post(page, new { type = "aiStart", question = shown, page = title });
        var sb = new StringBuilder();
        var last = DateTime.MinValue;
        try
        {
            var provider = AiService.Create(s);
            await Task.Run(() => provider.StreamAsync(messages, t =>
            {
                lock (sb) sb.Append(t);
                if ((DateTime.Now - last).TotalMilliseconds < 80) return;
                last = DateTime.Now;
                string snapshot; lock (sb) snapshot = sb.ToString();
                BeginInvoke(() => Post(page, new { type = "aiToken", text = snapshot }));
            }, cts.Token), cts.Token);
            Post(page, new { type = "aiDone", text = sb.ToString() });
        }
        catch (OperationCanceledException) { Post(page, new { type = "aiDone", text = sb + "\n\n_(остановлено)_" }); }
        catch (Exception ex) { Post(page, new { type = "aiError", text = ex.Message }); return; }
        // В истории храним вопрос без текста страницы — чтобы не раздувать контекст
        aiHistory.Add(new AiMessage("user", (title.Length > 0 ? $"[Страница: {title}] " : "") + shown));
        aiHistory.Add(new AiMessage("assistant", sb.ToString()));
    }

}
