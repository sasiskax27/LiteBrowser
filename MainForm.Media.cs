using System.Text;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Text.Json;
using System.Web;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace LiteBrowser;

/// <summary>Видео, скриншоты, перевод, голос, контекстное меню и заметки.</summary>
sealed partial class MainForm
{
    // ================= Видео, медиа, скриншоты, чтение =================

    async Task PictureInPicture()
    {
        var core = active?.Core;
        if (core == null) return;
        string r;
        try { r = await PageTools.EvalAsync(core, PageTools.PictureInPicture); } catch { r = "fail"; }
        if (r is "ok" or "exit") return;
        // Запасной вариант для YouTube — свой мини-плеер поверх окон
        if ((active!.PageHost.EndsWith("youtube.com") || active.PageHost == "youtu.be"))
        {
            var info = (await core.ExecuteScriptAsync(PageTools.YouTubeInfo)).Trim('"');
            var parts = info.Split('|');
            if (parts.Length == 2 && int.TryParse(parts[1], out var t)) { new MiniPlayer(parts[0], t).Show(); return; }
        }
        MessageBox.Show(this, r == "novideo" ? "На странице нет видео." : "Этот сайт не разрешает «картинку в картинке».", "Картинка в картинке");
    }

    async Task SetSpeed(double rate)
    {
        var core = active?.Core;
        if (core == null) return;
        var n = await core.ExecuteScriptAsync(PageTools.SetSpeed(rate));
        infoBar.ShowBar("speed", "", n == "0" ? "На странице нет видео или аудио" : $"Скорость воспроизведения: ×{rate:0.##}",
            ("Обычная скорость", () => { _ = SetSpeed(1); infoBar.HideBar(); }, false));
        if (rate == 1) infoBar.HideBar();
    }

    async Task DownloadMedia()
    {
        var core = active?.Core;
        if (core == null) return;
        string json;
        try { json = JsonSerializer.Deserialize<string>(await core.ExecuteScriptAsync(PageTools.FindMedia)) ?? "{}"; } catch { json = "{}"; }
        using var doc = JsonDocument.Parse(json);
        var items = doc.RootElement.TryGetProperty("items", out var it) ? it.EnumerateArray().ToList() : new();
        if (items.Count == 0)
        {
            MessageBox.Show(this, "На этой странице нет файлов видео или аудио, которые можно скачать напрямую.\n\n" +
                "Сайты вроде YouTube, ВК и Netflix передают видео потоком по кусочкам и не разрешают его скачивать.", "Скачать медиа");
            return;
        }
        var title = doc.RootElement.TryGetProperty("title", out var tt) ? tt.GetString() ?? "" : "";
        var page = core.Source;
        var menu = Theme.NewMenu();
        menu.Items.Add(new ToolStripMenuItem(L.T("Что скачать?")) { Enabled = false });
        int n = 0;
        foreach (var m in items)
        {
            var src = m.GetProperty("src").GetString()!;
            var kind = m.GetProperty("kind").GetString();
            var fileName = Path.GetFileName(new Uri(src).AbsolutePath);
            var label = $"{kind}: {(fileName.Length > 0 ? fileName : new Uri(src).Host)}";
            var idx = ++n;
            menu.Items.Add(Theme.Item(label.Length > 70 ? label[..68] + "…" : label, async () =>
            {
                string cookies = "";
                try { cookies = string.Join("; ", (await core.CookieManager.GetCookiesAsync(src)).Select(c => c.Name + "=" + c.Value)); } catch { }
                var ext = Path.GetExtension(new Uri(src).AbsolutePath);
                var nice = title.Length > 0 ? title + (items.Count > 1 ? $" ({idx})" : "") + (ext.Length is > 1 and < 6 ? ext : kind == "аудио" ? ".mp3" : ".mp4") : null;
                Downloads.StartDirect(src, page, cookies, nice);
                showDownloads = true;
                LayoutToolbar();
                downloadsPopup.ShowBelow(downloadsBtn);
            }));
        }
        menu.Show(menuBtn, new Point(menuBtn.Width - menu.PreferredSize.Width, menuBtn.Height + Theme.S(4)));
    }

    async Task TakeScreenshot(bool full)
    {
        var core = active?.Core;
        if (core == null) return;
        try
        {
            var path = await PageTools.Screenshot(core, full);
            infoBar.ShowBar("shot", "", $"Скриншот сохранён и скопирован в буфер обмена: {Path.GetFileName(path)}",
                ("Редактировать", () => { infoBar.HideBar(); using var ed = new ScreenshotEditor(path); ed.ShowDialog(this); }, true),
                ("Открыть", () => System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true }), false),
                ("Показать в папке", () => System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true }), false));
        }
        catch (Exception ex) { MessageBox.Show(this, "Не удалось сделать скриншот: " + ex.Message, "Скриншот"); }
    }

    // ================= Текст с картинки (OCR) =================

    /// <summary>Выделить область вкладки мышью — текст из неё распознаётся (OCR Windows, без интернета) и копируется.</summary>
    async Task RecognizeArea()
    {
        var tab = active;
        if (tab?.Core == null) return;
        if (!WinOcr.Supported) { MessageBox.Show(this, L.T("В этой Windows нет распознавания текста."), L.T("Текст с экрана")); return; }
        Bitmap shot;
        try
        {
            using var ms = new MemoryStream();
            await tab.Core.CapturePreviewAsync(Microsoft.Web.WebView2.Core.CoreWebView2CapturePreviewImageFormat.Png, ms);
            ms.Position = 0;
            shot = new Bitmap(ms);
        }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, L.T("Текст с экрана")); return; }
        using (shot)
        {
            var bounds = tab.View.RectangleToScreen(tab.View.ClientRectangle);
            using var pick = new AreaSelectForm(shot, bounds);
            if (pick.ShowDialog(this) != DialogResult.OK) return;
            var r = Rectangle.Intersect(pick.Selection, new Rectangle(Point.Empty, shot.Size));
            if (r.Width < 4 || r.Height < 4) return;
            using var part = shot.Clone(r, shot.PixelFormat);
            string text;
            try { text = await WinOcr.RecognizeAsync(part); }
            catch (Exception ex) { MessageBox.Show(this, ex.Message, L.T("Текст с экрана")); return; }
            if (string.IsNullOrWhiteSpace(text)) { infoBar.ShowBar("ocr", "\uE8D2", L.T("Текст в этой области не найден")); return; }
            Clipboard.SetText(text);
            var preview = text.Replace("\r", " ").Replace("\n", " ");
            if (preview.Length > 60) preview = preview[..60] + "…";
            infoBar.ShowBar("ocr", "\uE8D2", L.T("Текст скопирован: ") + "«" + preview + "»",
                (L.T("Показать"), () => { using var f = new TextResultForm(L.T("Текст с экрана"), text); f.ShowDialog(this); }, false));
        }
    }

    // ================= Сбои движка =================

    /// <summary>
    /// Процесс страницы упал или завис — предлагаем перезагрузить вкладку (вместо пустого окна).
    /// Падение основного процесса движка записываем в отчёт: вкладки этого окна придётся открыть заново.
    /// </summary>
    void OnProcessFailed(BrowserTab tab, CoreWebView2ProcessFailedEventArgs e)
    {
        var kind = e.ProcessFailedKind;
        if (kind == CoreWebView2ProcessFailedKind.RenderProcessUnresponsive)
        {
            if (tab != active) return;
            infoBar.ShowBar("hang", "\uE916", L.T("Страница не отвечает"),
                (L.T("Перезагрузить"), () => { try { tab.Core?.Reload(); } catch { } }, true),
                (L.T("Подождать"), () => { }, false));
            return;
        }
        if (kind is CoreWebView2ProcessFailedKind.RenderProcessExited or CoreWebView2ProcessFailedKind.FrameRenderProcessExited)
        {
            if (kind == CoreWebView2ProcessFailedKind.FrameRenderProcessExited) return; // упал встроенный фрейм — страница жива
            tab.Title = L.T("Страница упала");
            strip.Invalidate();
            if (tab == active)
                infoBar.ShowBar("crashtab", "\uE7BA", L.T("Страница аварийно закрылась (") + e.Reason + L.T("). Обычно помогает перезагрузка"),
                    (L.T("Перезагрузить"), () => { try { tab.Core?.Reload(); } catch { } }, true));
            CrashReport.Write(new Exception($"Render process failed: {e.Reason}, exit code {e.ExitCode}"), "render");
            return;
        }
        if (kind == CoreWebView2ProcessFailedKind.BrowserProcessExited)
        {
            var f = CrashReport.Write(new Exception($"Browser process exited: {e.Reason}, exit code {e.ExitCode}"), "engine");
            if (tab == active && f != null)
                BeginInvoke(() => CrashReport.Ask(f, L.T("Движок браузера аварийно завершился. Закройте это окно и откройте LiteBrowser снова.")));
            return;
        }
        // Остальное (GPU, служебные процессы) движок перезапускает сам — только запись
        CrashReport.Write(new Exception($"Engine process failed: {kind}, {e.Reason}, exit code {e.ExitCode}"), "engine-" + kind.ToString().ToLowerInvariant());
    }

    // ================= Запись видео вкладки =================
    TabRecorder? recorder;
    BrowserTab? recordingTab;

    /// <summary>Начать или остановить запись видео текущей вкладки.</summary>
    async Task ToggleRecording()
    {
        if (recorder != null) { await StopRecording(); return; }
        var tab = active;
        if (tab?.Core == null) return;
        try
        {
            recorder = await TabRecorder.StartAsync(tab.Core);
            recordingTab = tab;
            recorder.AutoStopped += _ => BeginInvoke(async () => await StopRecording(L.T("Запись остановлена — достигнут предел длины. ")));
            var what = recorder.HasAudio ? L.T("Идёт запись этой вкладки со звуком браузера")
                : recorder.IsMp4 ? L.T("Идёт запись этой вкладки (без звука: ") + recorder.AudioNote + ")" : L.T("Идёт запись этой вкладки (без звука)");
            infoBar.ShowBar("rec", "\uE7C8", what + L.T(". Можно переключаться — запишется, когда вернётесь"),
                (L.T("Остановить"), () => _ = StopRecording(), true));
        }
        catch (Exception ex)
        {
            recorder = null; recordingTab = null;
            MessageBox.Show(this, L.T("Не удалось начать запись: ") + ex.Message, L.T("Запись вкладки"));
        }
    }

    async Task StopRecording(string prefix = "")
    {
        var r = recorder;
        if (r == null) return;
        recorder = null; recordingTab = null;
        var path = await r.StopAsync();
        var time = r.Elapsed;
        long frames = r.Frames;
        r.Dispose();
        if (IsDisposed) return;
        if (frames == 0) { infoBar.ShowBar("rec", "\uE7C8", L.T("Запись пустая — страница не менялась")); return; }
        infoBar.ShowBar("rec", "\uE7C8", prefix + L.T("Видео сохранено: ") + $"{Path.GetFileName(path)} ({(int)time.TotalMinutes}:{time.Seconds:00})",
            (L.T("Открыть"), () => System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true }), true),
            (L.T("Показать в папке"), () => System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true }), false));
    }

    async Task ToggleReader()
    {
        var core = active?.Core;
        if (core == null || !core.Source.StartsWith("http")) return;
        var r = (await core.ExecuteScriptAsync(PageTools.Reader)).Trim('"');
        if (r == "notarticle") infoBar.ShowBar("reader", "", "На этой странице не нашлось статьи для режима чтения", ("OK", () => infoBar.HideBar(), false));
    }

    // ================= Перевод, голос, чтение вслух =================

    readonly HashSet<string> translateOffered = new();
    DateTime lastGesture;

    // ================= Контекстное меню, выделения, заметки =================

    void SetupContextMenu(BrowserTab tab, CoreWebView2 core, CoreWebView2Environment env)
    {
        core.ContextMenuRequested += (_, e) =>
        {
            var target = e.ContextMenuTarget;
            var src = core.Source;
            if (!src.StartsWith("http")) return;
            var items = e.MenuItems;
            CoreWebView2ContextMenuItem Item(string label, Action act)
            {
                var it = env.CreateContextMenuItem(L.T(label), null, CoreWebView2ContextMenuItemKind.Command);
                it.CustomItemSelected += (_, _) => BeginInvoke(act);
                return it;
            }
            int pos = 0;
            if (target.HasSelection)
            {
                var text = target.SelectionText.Trim();
                if (text.Length > 0)
                {
                    if (Cfg.AiEnabled)
                    {
                        items.Insert(pos++, Item("✦ Объяснить с помощью ИИ", () => OpenAi("explain")));
                        items.Insert(pos++, Item("✦ Перевести с помощью ИИ", () => OpenAi("translate")));
                    }
                    items.Insert(pos++, Item("🔊 Прочитать вслух", () => _ = ReadAloud()));
                    if (!Incognito)
                        items.Insert(pos++, Item("🖍 Выделить маркером", () =>
                        {
                            Store.AddHighlight(new Highlight { Url = Store.PageKey(src), Title = core.DocumentTitle, Text = text.Length > 1000 ? text[..1000] : text });
                            _ = ApplyHighlights(tab);
                        }));
                    items.Insert(pos++, env.CreateContextMenuItem("", null, CoreWebView2ContextMenuItemKind.Separator));
                }
            }
            else if (!target.HasLinkUri)
            {
                if (Cfg.AiEnabled) items.Insert(pos++, Item("✦ Пересказать страницу (ИИ)", () => OpenAi("summarize")));
                items.Insert(pos++, Item("🔊 Читать страницу вслух", () => _ = ReadAloud()));
                if (!Incognito)
                    items.Insert(pos++, Item("📝 Заметка к странице…", () =>
                    {
                        var note = InputDialog.Ask(this, "Заметка к странице", "");
                        if (!string.IsNullOrWhiteSpace(note))
                            Store.AddHighlight(new Highlight { Url = Store.PageKey(src), Title = core.DocumentTitle, Note = note });
                    }));
                items.Insert(pos++, Item("Перевести страницу", TranslatePage));
                items.Insert(pos++, Item("QR-код страницы", ShowQr));
                items.Insert(pos++, env.CreateContextMenuItem("", null, CoreWebView2ContextMenuItemKind.Separator));
            }
        };
    }

    /// <summary>Подсветить сохранённые выделения на странице.</summary>
    async Task ApplyHighlights(BrowserTab tab)
    {
        var core = tab.Core;
        if (core == null || Incognito) return;
        var key = Store.PageKey(core.Source);
        var texts = Store.Highlights.Where(h => h.Url == key && h.Text.Length > 0).Select(h => h.Text).Distinct().Take(100).ToList();
        if (texts.Count == 0) return;
        var script = "(texts => { const walk = document.createTreeWalker(document.body, NodeFilter.SHOW_TEXT); const nodes = []; " +
                     "while (walk.nextNode()) nodes.push(walk.currentNode); " +
                     "for (const t of texts) { for (const n of nodes) { const i = n.nodeValue.indexOf(t); if (i < 0 || n.parentElement.closest('mark.lite-hl, script, style')) continue; " +
                     "const r = document.createRange(); r.setStart(n, i); r.setEnd(n, i + t.length); const m = document.createElement('mark'); m.className = 'lite-hl'; " +
                     "m.style.cssText = 'background:#ffe066;color:#111;border-radius:3px;padding:0 1px'; try { r.surroundContents(m); } catch (e) {} break; } } })(" +
                     JsonSerializer.Serialize(texts) + ")";
        try { await core.ExecuteScriptAsync(script); } catch { }
    }

    /// <summary>Открыть ИИ-помощника в боковой панели (и сразу выполнить действие).</summary>
    void OpenAi(string? action = null)
    {
        ToggleSidePanel(true);
        if (action != null && sidePanel.Core?.Source?.StartsWith(LitePages.Ai) == true)
            Post(sidePanel.Core, new { type = "aiRun", action });
        else
            sidePanel.Navigate(LitePages.Ai + (action != null ? "?action=" + action : ""));
    }

    void ShowQr()
    {
        var src = active?.Core?.Source ?? "";
        if (!src.StartsWith("http")) return;
        try { using var f = new QrForm(src); f.ShowDialog(this); }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "QR-код"); }
    }

    void TranslatePage()
    {
        var src = active?.Core?.Source ?? "";
        if (!src.StartsWith("http") || src.Contains("translate.goog") || src.Contains("translate.yandex")) return;
        Navigate(PageTools.TranslateUrl(Cfg.TranslateService, src));
    }

    async Task OfferTranslate(BrowserTab tab)
    {
        var core = tab.Core;
        if (core == null || !Cfg.OfferTranslate || tab != active || infoBar.Key != null) return;
        var src = core.Source;
        if (!src.StartsWith("https://") || src.StartsWith(LitePages.Prefix) || src.Contains("translate")) return;
        if (!translateOffered.Add(tab.PageHost)) return;
        string lang;
        try { lang = (await core.ExecuteScriptAsync(PageTools.PageLang)).Trim('"'); } catch { return; }
        if (lang.Length != 2 || lang == "ru" || tab != active) return;
        var name = PageTools.LangNames.TryGetValue(lang, out var n) ? n : "иностранном";
        infoBar.ShowBar("translate", "", $"Страница на {name} языке. Перевести на русский?",
            ("Перевести", () => { infoBar.HideBar(); TranslatePage(); }, true),
            ("Не сейчас", () => infoBar.HideBar(), false),
            ("Не предлагать", () => { CopyAndSave(x => x.OfferTranslate = false); infoBar.HideBar(); }, false));
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);

    /// <summary>Голосовой ввод: ставим курсор в адресную строку и включаем диктовку Windows (Win+H).</summary>
    async Task VoiceInput()
    {
        address.Box.Focus();
        address.Box.SelectAll();
        await Task.Delay(120);
        const byte LWIN = 0x5B, H = 0x48;
        keybd_event(LWIN, 0, 0, UIntPtr.Zero);
        keybd_event(H, 0, 0, UIntPtr.Zero);
        keybd_event(H, 0, 2, UIntPtr.Zero);
        keybd_event(LWIN, 0, 2, UIntPtr.Zero);
    }


}
