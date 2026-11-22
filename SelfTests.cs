using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace LiteBrowser;

/// <summary>Диагностика загрузки: качает файл в отдельном профиле и пишет, что происходит.</summary>
static class DownloadSelfTest
{
    public static void Run(string url, string outFile, bool handled)
    {
        var log = new List<string>();
        var root = Path.Combine(Path.GetTempPath(), "LiteDlTest-" + Guid.NewGuid().ToString("N"));
        var form = new Form { Width = 900, Height = 700, ShowInTaskbar = false, StartPosition = FormStartPosition.Manual, Location = new Point(-2000, -2000) };
        var view = new WebView2 { Dock = DockStyle.Fill };
        form.Controls.Add(view);
        form.Shown += async (_, _) =>
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                var env = await CoreWebView2Environment.CreateAsync(null, Path.Combine(root, "data"));
                await view.EnsureCoreWebView2Async(env);
                view.CoreWebView2.Settings.IsReputationCheckingRequired = true;
                if (outFile.EndsWith("-filter.txt"))
                {
                    // Как в браузере с блокировщиком: перехватываем все запросы
                    view.CoreWebView2.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All, CoreWebView2WebResourceRequestSourceKinds.All);
                    view.CoreWebView2.WebResourceRequested += (_, e) => { var u = e.Request.Uri; _ = u.Length; };
                    log.Add("перехват всех запросов включён");
                }
                var done = new TaskCompletionSource<bool>();
                view.CoreWebView2.DownloadStarting += (_, e) =>
                {
                    e.Handled = handled;
                    e.ResultFilePath = Path.Combine(root, "file.bin");
                    var op = e.DownloadOperation;
                    log.Add($"{sw.ElapsedMilliseconds} мс: загрузка началась, размер {op.TotalBytesToReceive}");
                    long last = -1;
                    op.BytesReceivedChanged += (_, _) => { if (op.BytesReceived - last > 10_000_000 || op.BytesReceived == (long)(op.TotalBytesToReceive ?? 0)) { last = op.BytesReceived; log.Add($"{sw.ElapsedMilliseconds} мс: получено {op.BytesReceived / 1048576} МБ"); } };
                    op.StateChanged += (_, _) =>
                    {
                        log.Add($"{sw.ElapsedMilliseconds} мс: состояние {op.State}, причина {op.InterruptReason}, получено {op.BytesReceived}");
                        if (op.State != CoreWebView2DownloadState.InProgress) done.TrySetResult(true);
                    };
                };
                view.CoreWebView2.Navigate(url);
                if (await Task.WhenAny(done.Task, Task.Delay(120_000)) != done.Task) log.Add($"{sw.ElapsedMilliseconds} мс: ЗАВИСЛА — за 2 минуты не завершилась");
            }
            catch (Exception ex) { log.Add("исключение: " + ex.Message); }
            finally { File.WriteAllLines(outFile, log); form.Close(); }
        };
        Application.Run(form);
        try { Directory.Delete(root, true); } catch { }
    }
}

/// <summary>Самотест расширений в отдельной временной папке (не трогает данные пользователя).</summary>
static class ExtensionSelfTest
{
    public static void Run(string outFile)
    {
        var log = new List<string>();
        void Check(string name, bool ok, string? extra = null) => log.Add((ok ? "OK   " : "FAIL ") + name + (extra != null ? " — " + extra : ""));
        var root = Path.Combine(Path.GetTempPath(), "LiteExtTest-" + Guid.NewGuid().ToString("N"));
        Extensions.DirOverride = Path.Combine(root, "extensions");
        Directory.CreateDirectory(Extensions.DirOverride);

        var form = new Form { Width = 900, Height = 700, ShowInTaskbar = false, StartPosition = FormStartPosition.Manual, Location = new Point(-2000, -2000) };
        var view = new WebView2 { Dock = DockStyle.Fill };
        form.Controls.Add(view);
        form.Shown += async (_, _) =>
        {
            try
            {
                var env = await CoreWebView2Environment.CreateAsync(null, Path.Combine(root, "data"),
                    new CoreWebView2EnvironmentOptions { AreBrowserExtensionsEnabled = true });
                await view.EnsureCoreWebView2Async(env);
                view.CoreWebView2.NewWindowRequested += (_, e) => e.Handled = true; // расширения любят открывать свои страницы — в тесте не нужно
                var profile = view.CoreWebView2.Profile;
                log.Add("движок: " + env.BrowserVersionString);

                async Task<ExtensionInfo?> Try(string label, string url)
                {
                    try
                    {
                        var r = Extensions.Parse(url) ?? throw new Exception("ссылка не распознана");
                        var info = await Extensions.Download(r, env.BrowserVersionString);
                        Check(label + ": скачано и распаковано", true, $"{info.Name} {info.Version}, MV{info.ManifestVersion}, права: {string.Join("; ", Extensions.DescribePermissions(info))}");
                        return info;
                    }
                    catch (Exception ex) { Check(label, false, ex.Message); return null; }
                }

                var chrome = await Try("Chrome Web Store (Dark Reader)", "https://chromewebstore.google.com/detail/dark-reader/eimadpbcbfnmbkopoojfekhnkhdbieeh");
                var edge = await Try("Edge Add-ons (Dark Reader)", "https://microsoftedge.microsoft.com/addons/detail/dark-reader/ifoakfbpdcdoeenechcleahebpibofpc");
                if (edge != null) Extensions.Discard(edge);
                try
                {
                    // Подмена: другой ID при том же файле должна отклоняться
                    var raw = await new HttpClient().GetByteArrayAsync("https://clients2.google.com/service/update2/crx?response=redirect&prodversion=157.0&acceptformat=crx3&x=id%3Deimadpbcbfnmbkopoojfekhnkhdbieeh%26uc");
                    bool rejectedId = false, rejectedTamper = false;
                    try { CrxVerifier.Verify(raw, "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"); } catch { rejectedId = true; }
                    raw[^100] ^= 0xFF;
                    try { CrxVerifier.Verify(raw, "eimadpbcbfnmbkopoojfekhnkhdbieeh"); } catch { rejectedTamper = true; }
                    Check("подменённый ID отклоняется", rejectedId);
                    Check("изменённый файл отклоняется по подписи", rejectedTamper);
                }
                catch (Exception ex) { Check("проверка подмены", false, ex.Message); }
                try
                {
                    var ff = Extensions.Parse("https://addons.mozilla.org/ru/firefox/addon/darkreader/")!;
                    var fi = await Extensions.Download(ff, env.BrowserVersionString);
                    Check("Firefox Add-ons (Dark Reader): скачано", true, $"{fi.Name} {fi.Version}");
                    Extensions.Discard(fi);
                }
                catch (NotSupportedException ex) { Check("Firefox Add-ons: понятная ошибка о несовместимости", true, ex.Message); }
                catch (Exception ex) { Check("Firefox Add-ons", false, ex.Message); }

                if (chrome != null)
                {
                    var added = await Extensions.Install(profile, chrome);
                    var list = await profile.GetBrowserExtensionsAsync();
                    Check("расширение установлено в движок", list.Any(x => x.Id == added.Id), $"id {added.Id}, «{added.Name}», включено: {added.IsEnabled}");
                    if (chrome.Popup != null)
                    {
                        var done = new TaskCompletionSource<bool>();
                        view.CoreWebView2.NavigationCompleted += (_, e) => done.TrySetResult(e.IsSuccess);
                        view.CoreWebView2.Navigate($"chrome-extension://{added.Id}/{chrome.Popup.TrimStart('/')}");
                        var ok = await Task.WhenAny(done.Task, Task.Delay(15000)) == done.Task && done.Task.Result;
                        await Task.Delay(1500);
                        var html = ok ? await view.CoreWebView2.ExecuteScriptAsync("document.body ? document.body.innerText.length : -1") : "-";
                        Check("окно расширения открывается во вкладке", ok, "длина текста на странице: " + html);
                    }
                    await Extensions.Remove(profile, added.Id);
                    Check("расширение удаляется", !(await profile.GetBrowserExtensionsAsync()).Any(x => x.Id == added.Id));
                }
            }
            catch (Exception ex) { log.Add("FAIL исключение: " + ex); }
            finally
            {
                File.WriteAllLines(outFile, log);
                form.Close();
            }
        };
        Application.Run(form);
        try { Directory.Delete(root, true); } catch { }
    }
}

/// <summary>Самотест лёгких инструментов разработчика и палитры команд: настоящий WebView2 во временной папке.</summary>
static class DevToolsSelfTest
{
    public static void Run(string outFile)
    {
        var log = new List<string>();
        void Check(string name, bool ok, string? extra = null) => log.Add((ok ? "OK   " : "FAIL ") + name + (extra != null ? " — " + extra : ""));
        var root = Path.Combine(Path.GetTempPath(), "LiteDevTest-" + Guid.NewGuid().ToString("N"));
        var form = new Form { Width = 900, Height = 700, ShowInTaskbar = false, StartPosition = FormStartPosition.Manual, Location = new Point(-2000, -2000) };
        var view = new WebView2 { Dock = DockStyle.Fill };
        form.Controls.Add(view);
        form.Shown += async (_, _) =>
        {
            try
            {
                // Палитра команд: нечёткий поиск
                Check("поиск «вкл» находит «Новая вкладка»", CommandPalette.Score("Новая вкладка", "вкл") > 0);
                Check("«нвк» находит «Новая вкладка» по буквам", CommandPalette.Score("Новая вкладка", "нвк") > 0);
                Check("начало строки важнее середины", CommandPalette.Score("История", "ист") > CommandPalette.Score("Очистить историю", "ист"));
                Check("«zzz» ничего не находит", CommandPalette.Score("Новая вкладка", "zzz") == 0);

                var env = await CoreWebView2Environment.CreateAsync(null, Path.Combine(root, "data"));
                await view.EnsureCoreWebView2Async(env);
                var core = view.CoreWebView2;
                var dev = new DevMonitor(core);
                await dev.AttachAsync();
                var loaded = new TaskCompletionSource<bool>();
                core.NavigationCompleted += (_, e) => loaded.TrySetResult(e.IsSuccess);
                dev.OnNavigation("https://example.com/");
                core.Navigate("https://example.com/");
                bool ok = await Task.WhenAny(loaded.Task, Task.Delay(30_000)) == loaded.Task && loaded.Task.Result;
                Check("страница загрузилась", ok);
                await core.ExecuteScriptAsync("console.log('lite-test', {a: 1}, [1,2]); console.warn('lite-warn'); setTimeout(() => { throw new Error('lite-boom'); }, 0); fetch('/lite-404').catch(() => {});");
                await Task.Delay(2500);
                var doc = dev.Net.FirstOrDefault(n => n.Type == "Document");
                Check("сеть: запрос страницы записан", doc != null, doc == null ? null : $"{doc.Status} {doc.Url} {doc.Size} Б {doc.Ms:0} мс");
                Check("сеть: статус 200", doc?.Status == 200);
                var f = dev.Net.FirstOrDefault(n => n.Url.Contains("lite-404"));
                Check("сеть: fetch записан со статусом 404", f?.Status == 404, f == null ? "нет записи" : f.Status.ToString());
                var logEntry = dev.Console.FirstOrDefault(c => c.Text.Contains("lite-test"));
                Check("консоль: console.log пойман", logEntry != null, logEntry?.Text);
                Check("консоль: объект показан", logEntry?.Text.Contains("a: 1") == true || logEntry?.Text.Contains("\"a\"") == true, logEntry?.Text);
                Check("консоль: предупреждение", dev.Console.Any(c => c.Level == "warn" && c.Text.Contains("lite-warn")));
                var err = dev.Console.FirstOrDefault(c => c.Level == "error" && c.Text.Contains("lite-boom"));
                Check("консоль: необработанная ошибка поймана", err != null, err?.Text.Split('\n')[0]);
                var (r1, e1) = await dev.EvaluateAsync("1 + 1");
                Check("выполнение: 1 + 1 = 2", r1 == "2" && !e1, r1);
                var (r2, _) = await dev.EvaluateAsync("document.title");
                Check("выполнение: заголовок страницы", r2.Contains("Example"), r2);
                var (r3, e3) = await dev.EvaluateAsync("nope.x");
                Check("выполнение: ошибка возвращается", e3, r3.Split('\n')[0]);
                var (r4, _) = await dev.EvaluateAsync("let q = 5; q * 2");
                Check("выполнение: let в консоли", r4 == "10", r4);
                var m = await dev.MetricsAsync();
                Check("скорость: метрики получены", m.GetValueOrDefault("Nodes") > 0, $"узлов {m.GetValueOrDefault("Nodes")}, память {m.GetValueOrDefault("JSHeapUsedSize") / 1024:0} КБ");
                var timing = System.Text.Json.JsonSerializer.Deserialize<string>(await core.ExecuteScriptAsync(DevMonitor.TimingScript));
                Check("скорость: замеры страницы", timing?.Contains("\"ttfb\"") == true, timing?.Length > 160 ? timing[..160] : timing);
                // Чтение без интернета: снимок MHTML и открытие из файла
                Offline.UseDir(Path.Combine(root, "offline"));
                var origText = System.Text.Json.JsonSerializer.Deserialize<string>(await core.ExecuteScriptAsync("document.body.innerText.slice(0, 60)")) ?? "";
                var saved = await Offline.SaveAsync(core, "Example Domain");
                Check("офлайн: страница сохранена", File.Exists(Offline.PathOf(saved)) && saved.Size > 500, $"{saved.Size} Б");
                Check("офлайн: повторное сохранение заменяет", (await Offline.SaveAsync(core, "x")).Id == saved.Id && Offline.All.Count == 1);
                var offLoaded = new TaskCompletionSource<bool>();
                void OnDone(object? s, CoreWebView2NavigationCompletedEventArgs e) => offLoaded.TrySetResult(e.IsSuccess);
                core.NavigationCompleted += OnDone;
                core.Navigate(Offline.FileUrl(saved));
                await Task.WhenAny(offLoaded.Task, Task.Delay(15_000));
                core.NavigationCompleted -= OnDone;
                var offText = System.Text.Json.JsonSerializer.Deserialize<string>(await core.ExecuteScriptAsync("document.body ? document.body.innerText.slice(0, 200) : ''")) ?? "";
                Check("офлайн: открывается из файла", origText.Length > 10 && offText.StartsWith(origText), offText.Replace('\n', ' ').Trim());
                Offline.Remove(saved.Id);
                Check("офлайн: удаление", Offline.All.Count == 0 && !File.Exists(Offline.PathOf(saved)));

                // Автогруппировка
                Check("группы: youtube — видео", TabGrouping.Category("https://www.youtube.com/watch?v=1") == "Видео");
                Check("группы: music.youtube — музыка", TabGrouping.Category("https://music.youtube.com/") == "Музыка");
                Check("группы: ozon — покупки", TabGrouping.Category("https://www.ozon.ru/product/123") == "Покупки");
                Check("группы: news.mail.ru — новости", TabGrouping.Category("https://news.mail.ru/") == "Новости");
                var plan = TabGrouping.Plan(new[] { "https://youtube.com/a", "https://rutube.ru/b", "https://ozon.ru/c", "https://site.org/1", "https://blog.site.org/2", "https://one.com/" });
                Check("группы: раскладка", plan.GetValueOrDefault(0) == "Видео" && plan.GetValueOrDefault(1) == "Видео" && !plan.ContainsKey(2)
                    && plan.GetValueOrDefault(3) == "site.org" && plan.GetValueOrDefault(4) == "site.org" && !plan.ContainsKey(5), string.Join(", ", plan.Select(p => p.Key + "=" + p.Value)));

                // Синхронизация: пакет туда-обратно (зашифрован), слияние пространств и сохранённых страниц
                var remoteFile = Path.Combine(root, "sync.bin");
                var mhtml = Path.Combine(root, "page.mhtml");
                File.WriteAllText(mhtml, "MIME-Version: 1.0\r\nContent-Type: text/html\r\n\r\n<html><body>hello</body></html>");
                var bundle = new SyncBundle
                {
                    Workspaces = new() { new Workspace { Name = "Учёба-тест", Session = new Session { Tabs = new() { "https://example.com/" } } } },
                    Offline = new() { new SyncOfflinePage { Info = new OfflinePage { Url = "https://example.net/a", Title = "A", Time = DateTime.Now }, Data = Convert.ToBase64String(File.ReadAllBytes(mhtml)) } },
                    Highlights = new() { new Highlight { Url = "https://example.com/", Text = "тест" } },
                };
                Sync.WriteRemote(remoteFile, "пароль-синхронизации", bundle);
                var back = Sync.ReadRemote(remoteFile, "пароль-синхронизации");
                Check("синхронизация: пакет расшифрован", back?.Workspaces?.Count == 1 && back.Offline?.Count == 1 && back.Highlights?.Count == 1);
                Check("синхронизация: файл зашифрован", !File.ReadAllText(remoteFile).Contains("Учёба-тест"));
                Workspaces.Merge(back!.Workspaces!);
                Check("синхронизация: пространство добавлено", Workspaces.All.Any(w => w.Name == "Учёба-тест" && w.Session.Tabs.Count == 1));
                Workspaces.Merge(back.Workspaces!);
                Check("синхронизация: без дублей пространств", Workspaces.All.Count(w => w.Name == "Учёба-тест") == 1);
                Offline.UseDir(Path.Combine(root, "offline-sync"));
                Check("синхронизация: страница добавлена", Offline.Import(back.Offline!) == 1 && Offline.All.Count == 1);
                Check("синхронизация: без дублей страниц", Offline.Import(back.Offline!) == 0 && Offline.All.Count == 1);
                Check("синхронизация: страница уходит в пакет", Offline.Export().Count == 1);

                // Поиск по истории
                var stems = MainForm.HistoryStems("статья про видеокарты на прошлой неделе");
                Check("история: основы слов", stems.SequenceEqual(new[] { "видео" }), string.Join(",", stems));

                // Тёмная тема для сайта: стиль появляется на светлой странице и не появляется на чужом сайте
                var back2 = new TaskCompletionSource<bool>();
                void OnBack(object? s, CoreWebView2NavigationCompletedEventArgs e) => back2.TrySetResult(e.IsSuccess);
                core.NavigationCompleted += OnBack;
                core.Navigate("https://example.com/");
                await Task.WhenAny(back2.Task, Task.Delay(20_000));
                core.NavigationCompleted -= OnBack;
                // example.com сейчас сам тёмный — делаем страницу светлой, чтобы проверить затемнение
                await core.ExecuteScriptAsync("document.documentElement.style.background = '#fff'; document.body.style.background = '#fff'; document.body.style.color = '#111';");
                await core.ExecuteScriptAsync(MainForm.DarkScriptFor(false, new() { "example.com" }, new()));
                await Task.Delay(300);
                var darkOn = await core.ExecuteScriptAsync("!!document.getElementById('lite-dark')");
                var bgLum = await core.ExecuteScriptAsync("getComputedStyle(document.body).backgroundColor");
                Check("тёмная тема: включается для светлого сайта из списка", darkOn == "true", "фон страницы " + bgLum);
                await core.ExecuteScriptAsync("document.getElementById('lite-dark')?.remove(); document.documentElement.style.background = '#111'; document.body.style.background = '#111';");
                await core.ExecuteScriptAsync(MainForm.DarkScriptFor(false, new() { "example.com" }, new()));
                await Task.Delay(300);
                Check("тёмная тема: не трогает сайт, который уже тёмный", await core.ExecuteScriptAsync("!!document.getElementById('lite-dark')") == "false");
                await core.ExecuteScriptAsync(MainForm.DarkScriptFor(false, new() { "other.org" }, new()));
                Check("тёмная тема: не трогает другие сайты", await core.ExecuteScriptAsync("!!document.getElementById('lite-dark')") == "false");
                await core.ExecuteScriptAsync(MainForm.DarkScriptFor(true, new(), new() { "example.com" }));
                Check("тёмная тема: исключение при общей тёмной теме", await core.ExecuteScriptAsync("!!document.getElementById('lite-dark')") == "false");

                dev.OnNavigation("https://example.org/");
                Check("переход очищает журнал сети", dev.Net.Count == 0);
                dev.Detach();
                Check("отключение от вкладки", !dev.Attached);
                Check("загрузки: .exe считается программой", DownloadGuard.Risk(@"C:\x\setup.EXE") == "exec");
                Check("загрузки: .pdf безопасен", DownloadGuard.Risk(@"C:\x\doc.pdf") == "");
                var tmp = Path.Combine(root, "t.bin");
                File.WriteAllText(tmp, "abc");
                Check("загрузки: SHA-256", await DownloadGuard.Sha256Async(tmp) == "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad");
                DownloadGuard.MarkOfTheWeb(tmp, "https://example.com/file?secret=1");
                var zone = File.ReadAllText(tmp + ":Zone.Identifier");
                Check("загрузки: метка «из интернета»", zone.Contains("ZoneId=3") && !zone.Contains("secret"), zone.Replace("\r\n", " | "));
            }
            catch (Exception ex) { log.Add("FAIL исключение: " + ex); }
            finally { File.WriteAllLines(outFile, log); form.Close(); }
        };
        Application.Run(form);
        try { Directory.Delete(root, true); } catch { }
    }
}

/// <summary>Самотест субтитров YouTube: открывает видео во временной папке и достаёт субтитры, как для пересказа ИИ.</summary>
static class VideoSelfTest
{
    public static void Run(string url, string outFile)
    {
        var log = new List<string>();
        var root = Path.Combine(Path.GetTempPath(), "LiteVideoTest-" + Guid.NewGuid().ToString("N"));
        var form = new Form { Width = 1000, Height = 700, ShowInTaskbar = false, StartPosition = FormStartPosition.Manual, Location = Environment.GetEnvironmentVariable("LITEBROWSER_TEST_VISIBLE") != null ? new Point(60, 60) : new Point(-3000, -3000) };
        var view = new WebView2 { Dock = DockStyle.Fill };
        form.Controls.Add(view);
        form.Shown += async (_, _) =>
        {
            try
            {
                var env = await CoreWebView2Environment.CreateAsync(null, Path.Combine(root, "data"));
                await view.EnsureCoreWebView2Async(env);
                view.CoreWebView2.IsMuted = true;
                var loaded = new TaskCompletionSource<bool>();
                view.CoreWebView2.NavigationCompleted += (_, e) => loaded.TrySetResult(e.IsSuccess);
                view.CoreWebView2.Navigate(url);
                await Task.WhenAny(loaded.Task, Task.Delay(30_000));
                VideoContext.Result? r = null;
                // Как у живого зрителя: видео играет (субтитры YouTube грузит только во время просмотра)
                await Task.Delay(3000);
                await view.CoreWebView2.ExecuteScriptAsync("try { const m = document.getElementById('movie_player'); m.mute(); m.playVideo(); } catch (e) { }");
                for (int i = 0; i < 10; i++)
                {
                    await Task.Delay(1500);
                    r = await VideoContext.GetAsync(view.CoreWebView2);
                    if (r.Error != "noplayer") break;
                }
                log.Add($"видео: {r!.Title} — {r.Author}, {r.Length} с, язык субтитров: {r.Lang}, ошибка: {(r.Error.Length > 0 ? r.Error : "нет")}");
                log.Add($"субтитры: {r.Text.Length} символов; {r.Debug}");
                foreach (var line in r.Text.Split('\n').Take(5)) log.Add("   " + line);
                var fit = VideoContext.Fit(r.Text, 2000);
                log.Add($"сжатие до 2000: {fit.Length} символов, строк {fit.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length}");
                log.Add((r.Text.Length > 200 ? "OK   " : "FAIL ") + "субтитры получены");
            }
            catch (Exception ex) { log.Add("FAIL исключение: " + ex.Message); }
            finally { File.WriteAllLines(outFile, log); form.Close(); }
        };
        Application.Run(form);
        try { Directory.Delete(root, true); } catch { }
    }
}
