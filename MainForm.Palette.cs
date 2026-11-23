namespace LiteBrowser;

/// <summary>Палитра команд Ctrl+K.</summary>
sealed partial class MainForm
{
    CommandPalette? palette;

    void ShowPalette()
    {
        if (palette is { IsDisposed: false }) { palette.Activate(); return; }
        palette = new CommandPalette(PaletteEntries);
        palette.FormClosed += (_, _) => { palette?.Dispose(); palette = null; };
        // Откладываем: если Ctrl+K нажали внутри страницы, WebView2 сначала закончит обработку клавиши
        var p = palette;
        BeginInvoke(() => { if (!p.IsDisposed) p.ShowOver(this); });
    }

    List<CommandPalette.Entry> PaletteCommands()
    {
        var c = new List<CommandPalette.Entry>();
        void Cmd(string glyph, string title, Action run, string keys = "", string extra = "") => c.Add(new("cmd", glyph, title, "", run, keys, extra));
        Cmd("", "Новая вкладка", () => _ = AddTab(homeUrl), "Ctrl+T", "new tab открыть");
        Cmd("", "Новое окно", () => BrowserApp.Instance.OpenWindow(false), "Ctrl+N", "window");
        Cmd("", "Новое окно инкогнито", () => BrowserApp.Instance.OpenWindow(true), "Ctrl+Shift+N", "приватное incognito private");
        Cmd("", "Гостевое окно", BrowserApp.Instance.OpenGuest, "", "гость guest чужой");
        Cmd("", "Закрыть вкладку", () => { if (active != null) CloseTab(active); }, "Ctrl+W", "close");
        Cmd("", "Открыть закрытую вкладку", ReopenClosedTab, "Ctrl+Shift+T", "восстановить reopen");
        Cmd("", "Обновить страницу", () => active?.Core?.Reload(), "F5", "reload перезагрузить");
        Cmd("", "Добавить в закладки / убрать", ToggleBookmark, "Ctrl+D", "bookmark звезда");
        Cmd("", "Найти на странице", ShowFind, "Ctrl+F", "find поиск");
        Cmd("", "Режим чтения", () => _ = ToggleReader(), "Ctrl+Alt+R", "reader");
        if (Cfg.AiEnabled)
        {
            Cmd("✦", "Lite AI — ИИ-помощник", () => OpenAi(), "Ctrl+Shift+A", "ai ии чат нейросеть помощник");
            Cmd("✦", "ИИ: кратко пересказать страницу", () => OpenAi("summarize"), "", "ai summary итог суммаризация");
            Cmd("✦", "ИИ: главные мысли страницы", () => OpenAi("keypoints"), "", "ai тезисы");
            Cmd("✦", "ИИ: перевести страницу", () => OpenAi("translate"), "", "ai translate");
            Cmd("✦", "ИИ: пересказать видео YouTube", () => OpenAi("video"), "", "ai youtube видео субтитры summary");
        }
        Cmd("", "Перевести страницу", TranslatePage, "", "translate google яндекс");
        Cmd("", "Читать вслух", () => _ = ReadAloud(), "Ctrl+Shift+U", "озвучить tts speech");
        Cmd("", "Голосовой ввод", () => _ = VoiceInput(), "Ctrl+Shift+M", "микрофон voice");
        Cmd("", "Картинка в картинке", () => _ = PictureInPicture(), "Ctrl+Shift+V", "pip видео");
        Cmd("", "Скриншот видимой части", () => _ = TakeScreenshot(false), "Ctrl+Shift+S", "screenshot снимок");
        Cmd("", "Сравнить цены в магазинах", () => _ = ComparePrices(), "", "цена price магазин ozon wildberries маркет товар купить");
        Cmd("", "Субтитры YouTube на русском", () => _ = YouTubeSubtitles(), "", "youtube субтитры перевод captions subtitles");
        Cmd("", "Текст с экрана (выделить область)", () => _ = RecognizeArea(), "Ctrl+Shift+X", "ocr распознать текст картинка скопировать");
        Cmd("", "Мини-плеер поверх окон", () => { if (active != null) MiniPlayerForm.ShowFor(this, active); }, "", "музыка плеер mini player поверх");
        Cmd("", "Диспетчер задач браузера", () => TaskManagerForm.ShowFor(this), "Shift+Esc", "память memory task manager процессы тормозит");
        Cmd("", "Записать видео вкладки / остановить", () => _ = ToggleRecording(), "Alt+Shift+R", "запись видео record video screencast");
        Cmd("", "Скриншот всей страницы", () => _ = TakeScreenshot(true), "", "screenshot снимок полный");
        Cmd("", "Скачать видео или аудио со страницы", () => _ = DownloadMedia(), "", "download media");
        Cmd("", "QR-код страницы", ShowQr, "", "qr телефон");
        Cmd("", "Тёмная тема для этого сайта", ToggleDarkForSite, "", "dark mode тёмный режим ночь");
        Cmd("", Cfg.TorEnabled ? "Tor: настройки и выключение" : "Включить Tor (обход блокировок)", () => OpenLitePage(LitePages.Settings, "#proxy"), "", "tor тор vpn обход блокировок анонимность");
        Cmd("", "Прочитать позже", AddToReadLater, "Ctrl+Shift+L", "read later потом сохранить список");
        Cmd("", "Список «Прочитать позже»", () => OpenLitePage(LitePages.ReadLaterPage), "", "read later потом список");
        if (active != null && TabUrl(active).StartsWith("http")) { var at = active; Cmd("", "Отложить вкладку до завтра", () => SnoozeTab(at, "tomorrow"), "", "snooze отложить потом напомнить"); }
        if (FocusActive) Cmd("\uF272", "Закончить фокус-режим", EndFocus, "", "focus фокус");
        else foreach (var m in new[] { 25, 50, 60, 120 }) { var mm = m; Cmd("\uF272", $"Фокус-режим на {m} минут", () => StartFocus(mm), "", "focus фокус помидор pomodoro учёба работа"); }
        Cmd("", Cfg.VerticalTabs ? "Вкладки сверху" : "Вертикальные вкладки (слева)", ToggleVerticalTabs, "", "vertical tabs вертикальные вкладки боковые");
        Cmd("", (Cfg.ForceDark ? "Выключить" : "Включить") + " тёмную тему для всех сайтов", () => { bool v = !Cfg.ForceDark; CopyAndSave(x => x.ForceDark = v); foreach (var t in tabs) ApplyDarkScript(t); active?.Core?.Reload(); }, "", "dark mode тёмный режим ночь все");
        foreach (var (u, t) in recentlyClosed.Take(5)) { var uu = u; Cmd("", "Вернуть: " + (t.Length > 0 ? t : u), () => { recentlyClosed.RemoveAll(x => x.url == uu); _ = AddTab(uu); }, "", "закрытая вкладка reopen closed"); }
        Cmd("", "Сохранить для чтения без интернета", () => _ = SaveOffline(), "Ctrl+Shift+Y", "offline офлайн сохранить страницу mhtml");
        Cmd("", "Печать", () => _ = active?.Core?.ExecuteScriptAsync("window.print()"), "Ctrl+P", "print pdf");
        Cmd("", "Боковая панель", () => ToggleSidePanel(!sidePanel.Visible), "Ctrl+B", "sidebar");
        Cmd("", SplitPartner != null ? "Убрать разделение экрана" : "Разделить экран (две вкладки рядом)", ToggleSplit, "Ctrl+\\", "split view рядом два окна");
        if (SplitPartner != null) Cmd("", "Поменять половины местами", SwapSplit, "", "split swap");
        Cmd("", "Панель закладок", ToggleBookmarksBar, "Ctrl+Shift+B", "bookmarks bar");
        Cmd("", "Увеличить масштаб", () => ZoomStep(1), "Ctrl++", "zoom");
        Cmd("", "Уменьшить масштаб", () => ZoomStep(-1), "Ctrl+−", "zoom");
        Cmd("", "Сбросить масштаб", () => SetZoom(Cfg.PageZoom), "Ctrl+0", "zoom 100");
        Cmd("", "Во весь экран", () => SetFullscreen(FormBorderStyle != FormBorderStyle.None), "F11", "fullscreen");
        Cmd("", "Выключить / включить звук вкладки", () => { if (active?.Core != null) active.Core.IsMuted = !active.Core.IsMuted; }, "", "mute звук");
        Cmd("", "Закрепить вкладку", () => { if (active != null) TogglePin(active); }, "", "pin");
        Cmd("", "Сгруппировать вкладки по темам", AutoGroupTabs, "", "group группы темы авто разложить");
        Cmd("", "Убрать все группы вкладок", UngroupAll, "", "ungroup группы");
        Cmd("", "Дублировать вкладку", () => { var u = active?.Core?.Source; if (u != null) _ = AddTab(u); }, "", "duplicate копия");
        Cmd("", "Экономия трафика (без картинок)", () => { bool v = !Cfg.DataSaver; CopyAndSave(x => x.DataSaver = v); active?.Core?.Reload(); }, "", "data saver картинки");
        Cmd("", "Центр безопасности", () => OpenLitePage(LitePages.Security), "", "security privacy трекеры приватность");
        Cmd("", "Инструменты разработчика", OpenDevTools, "Ctrl+Shift+I", "devtools network console сеть консоль");
        Cmd("", "Инструменты Chromium (полные)", () => active?.Core?.OpenDevToolsWindow(), "F12", "devtools inspect");
        Cmd("", "Исходный код страницы", () => { var u = active?.Core?.Source; if (u?.StartsWith("http") == true) _ = AddTab("view-source:" + u); }, "", "view source html");
        Cmd("", "Удалить историю и данные сайтов", () => OpenLitePage(LitePages.Settings, "#data"), "", "clear cookies кэш очистить");
        return c;
    }

    static readonly (string id, string title, string extra)[] SettingSections =
    {
        ("search", "Поиск и поисковая система", "engine google яндекс duckduckgo подсказки"),
        ("security", "Безопасность: HTTPS, сайты", "https"),
        ("ai", "ИИ-помощник", "ai модель ollama openai"),
        ("tools", "Инструменты: перевод, жесты", "жесты мышью перевод"),
        ("parental", "Родительский контроль", "дети pin"),
        ("adblock", "Блокировка рекламы", "adblock реклама фильтры easylist"),
        ("proxy", "Обход блокировок и прокси", "proxy vpn socks"),
        ("passwords", "Пароли", "менеджер пароль"),
        ("sites", "Надёжные и ненадёжные сайты", "доверенные"),
        ("tabs", "Вкладки и запуск", "сессия заморозка"),
        ("bookmarks", "Закладки и история", "история"),
        ("look", "Внешний вид: тема, цвет, масштаб, обои", "тема тёмная светлая цвет обои размер"),
        ("import", "Перенос из других браузеров", "import chrome edge firefox яндекс"),
        ("sync", "Синхронизация", "sync облако"),
        ("updates", "Обновления", "update версия"),
        ("system", "Браузер по умолчанию", "default система"),
        ("data", "Удаление данных", "очистить cookies"),
    };

    List<CommandPalette.Entry> PaletteEntries(string q)
    {
        var scored = new List<(int score, int group, CommandPalette.Entry e)>();
        void Add(int group, CommandPalette.Entry e, params string[] texts)
        {
            // В английском интерфейсе ищем и по переводу, и по-русски
            int s = texts.Max(t => Math.Max(CommandPalette.Score(t, q), L.En ? CommandPalette.Score(L.T(t), q) : 0));
            if (s > 0) scored.Add((s, group, e));
        }

        // Вкладки
        foreach (var t in tabs)
        {
            var tt = t;
            var url = t.Core?.Source ?? t.PendingUrl ?? "";
            var shown = LitePages.Display(url) ?? url;
            Add(q.Length == 0 ? 1 : 0, new("tab", "", t.Title, shown, () => SelectTab(tt), t == active ? "текущая" : ""), t.Title, shown);
        }
        foreach (var c in PaletteCommands()) Add(q.Length == 0 ? 0 : 1, c, c.Title, c.Title + " " + c.Extra);
        if (WorkspacesHere && q.Length > 0)
        {
            foreach (var w in Workspaces.All.Where(w => w != Workspaces.Current))
            {
                var ws = w;
                Add(1, new("cmd", "", "Пространство: " + w.Name, $"{w.Session.Tabs.Count} вкладок", () => SwitchWorkspace(ws), "", "workspace"), "пространство " + w.Name, w.Name);
            }
            Add(1, new("cmd", "", "Новое рабочее пространство…", "", NewWorkspace, "", "workspace"), "новое рабочее пространство workspace");
        }
        if (q.Length == 0)
        {
            // Без запроса — вкладки и самые нужные команды
            return scored.OrderBy(x => x.group).ThenByDescending(x => x.e.Kind == "tab" ? 0 : 1).Select(x => x.e).Take(40).ToList();
        }

        // Страницы браузера и разделы настроек
        foreach (var (title, page, extra) in new[]
        {
            ("Настройки", LitePages.Settings, "settings параметры"), ("История", LitePages.History, "history"), ("Пароли", LitePages.Passwords, "passwords"),
            ("Загрузки", LitePages.DownloadsPage, "downloads"), ("Закладки", LitePages.BookmarksPage, "bookmarks"), ("Расширения", LitePages.ExtensionsPage, "extensions"),
            ("Заметки и выделения", LitePages.Notes, "notes"), ("Сохранённые страницы", LitePages.OfflinePage, "offline офлайн без интернета"),
            ("Центр безопасности", LitePages.Security, "security"),
        })
        {
            var p = page;
            Add(2, new("page", "", title, LitePages.Display(page) ?? "", () => OpenLitePage(p)), title, extra);
        }
        foreach (var (id, title, extra) in SettingSections)
        {
            var sid = id;
            Add(3, new("set", "", "Настройки: " + title, "", () => OpenLitePage(LitePages.Settings, "#" + sid)), title, extra);
        }

        // Закладки и история (только лучшие совпадения)
        foreach (var b in Store.Bookmarks)
        {
            var bu = b.Url;
            Add(4, new("bm", "", b.Title.Length > 0 ? b.Title : b.Url, b.Url, () => Navigate(bu)), b.Title, b.Url);
        }
        var seen = new HashSet<string>(Store.Bookmarks.Select(b => b.Url));
        foreach (var h in Store.History.AsEnumerable().Reverse().Take(3000))
        {
            if (!seen.Add(h.Url)) continue;
            var hu = h.Url;
            Add(5, new("hist", "", h.Title.Length > 0 ? h.Title : h.Url, h.Url, () => Navigate(hu)), h.Title, h.Url);
        }

        var result = scored.OrderByDescending(x => x.score / 100).ThenBy(x => x.group).ThenByDescending(x => x.score)
            .Select(x => x.e).Take(60).ToList();
        // Перейти по адресу или искать
        var target = ToUrl(q);
        bool isUrl = !target.StartsWith(SearchEngines.Get(Cfg.Engine).Url, StringComparison.Ordinal);
        var go = new CommandPalette.Entry("go", isUrl ? "" : "", isUrl ? "Открыть " + q : "Искать «" + q + "»", isUrl ? "" : SearchEngines.Get(Cfg.Engine).Name,
            () => Navigate(target));
        // Похоже на адрес — переход первым, иначе поиск последним
        if (isUrl) result.Insert(0, go); else result.Add(go);
        return result;
    }
}
