using System.Text;
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
                // Подсказки поисковиков (по-русски, без кракозябр)
                foreach (var eng in new[] { "google", "yandex", "duckduckgo" })
                {
                    var sg = await RemoteSuggest.Get(eng, "привет", CancellationToken.None);
                    Check($"подсказки {eng}", sg.Count > 0 && sg.Any(x => x.Contains("привет")), string.Join(" | ", sg.Take(3)));
                }

                // Сайты-подделки: ловим маскировки, не трогаем настоящие сайты
                var fakes = new[] { "sberbank-online.xyz", "gosuslugi.ru.secure-login.com", "g00gle.com", "paypa1.com", "yandexx.ru", "wildberies.ru", "vk.com-login.ru", "xn--sberbnk-6fg.ru" };
                var reals = new[] { "sberbank.ru", "online.sberbank.ru", "google.ru", "googleusercontent.com", "mail.google.com", "lenta.ru", "github.io",
                                    "yandex.kz", "steamstatic.com", "email.com", "habr.com", "amazon.de", "news.ycombinator.com", "ozon.ru", "vkvideo.ru" };
                var missed = fakes.Where(f => Lookalike.Check(f) == null).ToList();
                var falseAlarms = reals.Where(r => Lookalike.Check(r) != null).Select(r => r + "→" + Lookalike.Check(r)).ToList();
                Check("подделки распознаются", missed.Count == 0, missed.Count > 0 ? "пропущены: " + string.Join(", ", missed) : string.Join(", ", fakes.Select(f => f + "→" + Lookalike.Check(f))));
                Check("настоящие сайты не трогаются", falseAlarms.Count == 0, string.Join(", ", falseAlarms));

                // Виджеты стартовой страницы
                try
                {
                    var wjson = System.Text.Json.JsonSerializer.Serialize(await Widgets.WeatherAsync("Москва"));
                    Check("виджет: погода", wjson.Contains("\"temp\""), System.Text.RegularExpressions.Regex.Unescape(wjson));
                    var cjson = System.Text.Json.JsonSerializer.Serialize(await Widgets.CurrencyAsync());
                    Check("виджет: курс ЦБ", cjson.Contains("USD") && cjson.Contains("EUR"), cjson);
                }
                catch (Exception ex) { Check("виджеты: сервисы доступны", false, ex.Message); }

                // Пароль на браузер: PIN проверяется, хеш не содержит PIN и каждый раз с новой солью
                {
                    var h1 = AppLock.Hash("4821");
                    var h2 = AppLock.Hash("4821");
                    AppEnv.Settings.LockPinHash = h1;
                    bool good = AppLock.Check("4821"), bad = AppLock.Check("0000");
                    AppEnv.Settings.LockPinHash = "";
                    Check("пароль на браузер: верный PIN открывает, неверный — нет", good && !bad);
                    Check("пароль на браузер: соль разная, PIN в хеше не виден", h1 != h2 && !h1.Contains("4821"));
                }

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
                // Сравнение цен: товар из разметки schema.org и чистка названия
                await core.ExecuteScriptAsync("var s=document.createElement('script');s.type='application/ld+json';s.textContent=JSON.stringify({'@context':'https://schema.org','@graph':[{'@type':'WebPage'},{'@type':'Product','name':'Смартфон Test X 128 ГБ','offers':{'@type':'Offer','price':'19990','priceCurrency':'RUB'}}]});document.head.appendChild(s);");
                var prod = System.Text.Json.JsonSerializer.Deserialize<string>(await core.ExecuteScriptAsync(MainForm.ProductJs)) ?? "";
                Check("цены: товар найден на странице", prod.Contains("Test X 128") && prod.Contains("19990") && prod.Contains("RUB"), prod);
                var cleaned = new[] { MainForm.CleanProductName("Купить Наушники Sony WH-1000XM5 по выгодной цене — интернет-магазин"), MainForm.CleanProductName("Apple iPhone 15 128 ГБ | Ozon") };
                Check("цены: название без «купить» и магазина", cleaned[0] == "Наушники Sony WH-1000XM5" && cleaned[1] == "Apple iPhone 15 128 ГБ", string.Join(" / ", cleaned));
                // Отчёт о сбое: без пути к профилю и имени пользователя, ссылка на обращение не слишком длинная
                Exception boom;
                try { throw new InvalidOperationException("файл " + Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "x.txt") + " пользователя " + Environment.UserName); }
                catch (Exception ex) { boom = ex; }
                var report = CrashReport.Build(boom, "test");
                var issue = CrashReport.IssueUrl(report);
                Check("сбой: отчёт без личных данных", report.Contains("%USERPROFILE%") && !report.Contains(Environment.UserName, StringComparison.OrdinalIgnoreCase) && report.Contains("InvalidOperationException"));
                Check("сбой: ссылка на обращение", issue.StartsWith("https://github.com/") && issue.Contains("InvalidOperationException") && issue.Length < 32000, $"{issue.Length} символов");
                // Громкость вкладки: ставится существующим и новым видео/аудио
                await core.ExecuteScriptAsync("var a1=document.createElement('audio');document.body.appendChild(a1);");
                await core.ExecuteScriptAsync(MainForm.VolumeJs(40));
                var vols = await core.ExecuteScriptAsync("var a2=document.createElement('audio');document.body.appendChild(a2);a2.dispatchEvent(new Event('loadedmetadata'));[a1.volume,a2.volume].join(',')");
                Check("громкость вкладки: 40% у старых и новых элементов", vols.Trim('"') == "0.4,0.4", vols);
                // Поиск по тексту страниц: все слова, отрывок вокруг найденного
                PageIndex.Add("https://test.local/gpu", "Обзор железа", "Вступление. " + new string('x', 300) + " Новые видеокарты RTX стали дешевле, а процессоры — нет. " + new string('y', 300));
                PageIndex.Add("https://test.local/other", "Про котиков", "Котики спят двадцать часов в сутки.");
                var hits = PageIndex.Search("видеокарты дешевле", 10);
                Check("история по тексту: найдено по содержимому", hits.Count == 1 && hits[0].page.Url.EndsWith("/gpu") && hits[0].snippet.Contains("видеокарты") && hits[0].snippet.Length < 260,
                      hits.Count > 0 ? hits[0].snippet : "ничего");
                PageIndex.Remove("https://test.local/gpu"); PageIndex.Remove("https://test.local/other");
                // Windows Hello: системный компонент находится без пакета Windows SDK (само окно в тесте не показываем)
                bool helloOk = WindowsHello.Supported;
                Check("Windows Hello: проверка доступности не падает", true, helloOk ? "компонент есть" : "компонента нет (сервер/старая Windows)");
                // Диспетчер задач: процесс страницы находится по главному фрейму вкладки
                var procs = await env.GetProcessExtendedInfosAsync();
                var mine = procs.FirstOrDefault(p => p.AssociatedFrameInfos.Any(f => f.ParentFrameInfo == null && f.FrameId == core.FrameId));
                Check("диспетчер задач: процесс вкладки найден", mine?.ProcessInfo.Kind == CoreWebView2ProcessKind.Renderer,
                      $"процессов движка {procs.Count}, вкладка → pid {mine?.ProcessInfo.ProcessId}");
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

                // Перевод выделенного: скрипт встаёт на страницу, переводчик отвечает
                await core.ExecuteScriptAsync(SelectionTranslate.Script);
                Check("перевод выделенного: скрипт на странице", await core.ExecuteScriptAsync("typeof window.__liteTr") == "\"function\"");
                try
                {
                    var (tr1, from1) = await SelectionTranslate.TranslateAsync("Good morning, my friend", "ru");
                    Check("перевод: английский → русский", tr1.Any(c => c >= 'А' && c <= 'я'), $"{from1}: {tr1}");
                    var (tr2, from2) = await SelectionTranslate.TranslateAsync("Доброе утро, друг", "ru");
                    Check("перевод: русский → английский", tr2.Any(char.IsAsciiLetter) && from2 == "ru", $"{from2}: {tr2}");
                }
                catch (Exception ex) { Check("перевод: переводчик доступен", false, ex.Message); }

                // PDF для ИИ: создаём документ и читаем текст обратно
                {
                    var builder = new UglyToad.PdfPig.Writer.PdfDocumentBuilder();
                    var font = builder.AddStandard14Font(UglyToad.PdfPig.Fonts.Standard14Fonts.Standard14Font.Helvetica);
                    var pg = builder.AddPage(UglyToad.PdfPig.Content.PageSize.A4);
                    pg.AddText("LiteBrowser PDF test: quarterly revenue grew 42 percent.", 12, new UglyToad.PdfPig.Core.PdfPoint(50, 750), font);
                    var pg2 = builder.AddPage(UglyToad.PdfPig.Content.PageSize.A4);
                    pg2.AddText("Conclusion: the plan works.", 12, new UglyToad.PdfPig.Core.PdfPoint(50, 750), font);
                    var (ptitle, ptext, pcount) = PdfContext.Extract(builder.Build(), 5000);
                    Check("PDF: текст и страницы прочитаны", pcount == 2 && ptext.Contains("42 percent") && ptext.Contains("[стр. 2]"), ptext.Replace('\n', ' '));
                }

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

/// <summary>Самотест встроенного Tor: скачать, подключиться (без мостов, затем с мостами) и проверить выход через Tor.</summary>
static class TorSelfTest
{
    public static void Run(string outFile, string? only = null)
    {
        var log = new List<string>();
        void Check(string name, bool ok, string? extra = null) => log.Add((ok ? "OK   " : "FAIL ") + name + (extra != null ? " — " + extra : ""));
        try
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            TorService.InstallAsync().GetAwaiter().GetResult();
            Check("Tor скачан и проверен (SHA-256)", TorService.Installed, $"{sw.Elapsed.TotalSeconds:0} с");
            foreach (var bridges in only != null ? new[] { only } : new[] { "", "obfs4", "snowflake" })
            {
                sw.Restart();
                TorService.StartAsync(bridges).GetAwaiter().GetResult();
                log.Add($"     {(bridges.Length == 0 ? "без мостов" : bridges)}: {TorService.Progress}% за {sw.Elapsed.TotalSeconds:0} с — {TorService.Status}");
                if (TorService.Progress >= 100)
                {
                    using var http = new HttpClient(new HttpClientHandler { Proxy = new System.Net.WebProxy($"socks5://127.0.0.1:{TorService.SocksPort}"), UseProxy = true })
                    { Timeout = TimeSpan.FromSeconds(60) };
                    var json = http.GetStringAsync("https://check.torproject.org/api/ip").GetAwaiter().GetResult();
                    Check("трафик идёт через Tor" + (bridges.Length > 0 ? $" ({bridges})" : ""), json.Contains("\"IsTor\":true"), json);
                    TorService.Stop();
                    break;
                }
                TorService.Stop();
            }
            if (!log.Any(l => l.StartsWith("OK   трафик"))) Check("подключение к Tor", false, "ни один способ не подключился");
        }
        catch (Exception ex) { log.Add("FAIL исключение: " + ex.Message); }
        finally { TorService.Stop(); File.WriteAllLines(outFile, log); }
    }
}

/// <summary>Самотест записи вкладки: 3 секунды анимации → AVI; проверяем заголовок, число кадров и что кадры — JPEG.</summary>
static class RecordSelfTest
{
    public static void Run(string outFile)
    {
        var log = new List<string>();
        void Check(string name, bool ok, string? extra = null) => log.Add((ok ? "OK   " : "FAIL ") + name + (extra != null ? " — " + extra : ""));
        var root = Path.Combine(Path.GetTempPath(), "LiteRecTest-" + Guid.NewGuid().ToString("N"));
        var form = new Form { Width = 900, Height = 600, ShowInTaskbar = false, StartPosition = FormStartPosition.Manual, Location = new Point(-3000, -3000) };
        var view = new WebView2 { Dock = DockStyle.Fill };
        form.Controls.Add(view);
        form.Shown += async (_, _) =>
        {
            try
            {
                // Звук без жеста пользователя — только для теста
                var env = await CoreWebView2Environment.CreateAsync(null, Path.Combine(root, "data"),
                    new CoreWebView2EnvironmentOptions("--autoplay-policy=no-user-gesture-required"));
                await view.EnsureCoreWebView2Async(env);
                var loaded = new TaskCompletionSource<bool>();
                view.CoreWebView2.NavigationCompleted += (_, e) => loaded.TrySetResult(e.IsSuccess);
                // Анимация + тихий тон 18 кГц (почти не слышен), чтобы проверить звук в записи
                view.CoreWebView2.NavigateToString("<html><body style='margin:0;background:#123'><div id=b style='width:120px;height:120px;background:#7c9cff;position:absolute'></div>" +
                    "<script>let t=0;setInterval(()=>{t+=8;b.style.left=(t%700)+'px';b.style.top=(100+Math.sin(t/50)*80)+'px'},30);" +
                    "const ac=new AudioContext();const o=ac.createOscillator();const g=ac.createGain();o.frequency.value=18000;g.gain.value=0.08;o.connect(g).connect(ac.destination);o.start();</script></body></html>");
                await Task.WhenAny(loaded.Task, Task.Delay(15_000));
                await Task.Delay(500);
                var acState = await view.CoreWebView2.ExecuteScriptAsync("ac.state");
                log.Add("INFO звук: AudioContext " + acState + ", играет: " + view.CoreWebView2.IsDocumentPlayingAudio);

                // 1) Звук браузера ловится (только наши процессы)
                int peak = 0; long got = 0;
                var cap = ProcessAudioCapture.TryStart((pcm, _) =>
                {
                    got += pcm.Length;
                    for (int i = 0; i + 1 < pcm.Length; i += 2) peak = Math.Max(peak, Math.Abs((int)BitConverter.ToInt16(pcm, i)));
                }, out var capErr, (int)view.CoreWebView2.BrowserProcessId);
                if (cap != null) { await Task.Delay(1500); log.Add($"INFO звук: пакетов {cap.RealPackets}, тихих {cap.SilentPackets}, досыпано {cap.PaddedChunks}"); cap.Dispose(); }
                Check("звук: захват звука браузера", cap != null && got > 48000 * 4 / 2, cap == null ? capErr : $"{got / 1024} КБ, пик {peak}");
                bool audioWorks = cap != null && peak > 300;
                log.Add((audioWorks ? "OK   " : "INFO ") + $"звук: тон слышен в захвате — пик {peak}" + (audioWorks ? "" : " (нет звуковой карты/звук выключен — не ошибка)"));

                // 2) MP4 со звуком
                var rec = await TabRecorder.StartAsync(view.CoreWebView2, root);
                await Task.Delay(3000);
                var path = await rec.StopAsync();
                long frames = rec.Frames;
                bool mp4 = rec.IsMp4, hasAudio = rec.HasAudio;
                var note = rec.AudioNote;
                rec.Dispose();
                Check("mp4: запись в MP4", mp4 && path.EndsWith(".mp4"), path);
                Check("mp4: кадры записаны", frames >= 40, $"{frames} кадров за 3 с");
                var m = File.ReadAllBytes(path);
                Check("mp4: структура (ftyp, moov, H.264)", IndexOf(m, "ftyp"u8.ToArray()) == 4 && IndexOf(m, "moov"u8.ToArray()) > 0 && IndexOf(m, "avc1"u8.ToArray()) > 0, $"{m.Length / 1024} КБ");
                Check("mp4: дорожка звука AAC", !hasAudio || IndexOf(m, "mp4a"u8.ToArray()) > 0, hasAudio ? "есть" : "без звука: " + note);
                if (Environment.GetEnvironmentVariable("LITEBROWSER_KEEP_TEST_VIDEO") is { } keep) File.Copy(path, keep, true);
                File.Delete(path);

                // 3) Запасной формат AVI
                var rec2 = await TabRecorder.StartAsync(view.CoreWebView2, root, preferMp4: false);
                await Task.Delay(2000);
                path = await rec2.StopAsync();
                frames = rec2.Frames;
                rec2.Dispose();
                Check("avi: кадры записаны", frames >= 15, $"{frames} кадров за 2 с");
                var b = File.ReadAllBytes(path);
                Check("avi: заголовок RIFF AVI", System.Text.Encoding.ASCII.GetString(b, 0, 4) == "RIFF" && System.Text.Encoding.ASCII.GetString(b, 8, 4) == "AVI ");
                Check("avi: размер RIFF совпадает", BitConverter.ToInt32(b, 4) == b.Length - 8, $"{b.Length / 1024} КБ");
                int idx = IndexOf(b, "idx1"u8.ToArray());
                Check("avi: есть индекс кадров", idx > 0 && BitConverter.ToInt32(b, idx + 4) == frames * 16);
                int movi = IndexOf(b, "movi"u8.ToArray());
                int first = movi + BitConverter.ToInt32(b, idx + 8 + 8);
                Check("avi: первый кадр — JPEG", b[first] == '0' && b[first + 8] == 0xFF && b[first + 9] == 0xD8);
                int w = BitConverter.ToInt32(b, 64), h = BitConverter.ToInt32(b, 68);
                Check("avi: размер кадра записан", w > 100 && h > 100, $"{w}×{h}");
                File.Delete(path);
            }
            catch (Exception ex) { log.Add("FAIL исключение: " + ex); }
            finally { File.WriteAllLines(outFile, log); form.Close(); }
        };
        Application.Run(form);
        try { Directory.Delete(root, true); } catch { }
    }

    static int IndexOf(byte[] b, byte[] pat)
    {
        for (int i = 0; i + pat.Length <= b.Length; i++)
        {
            int j = 0;
            while (j < pat.Length && b[i + j] == pat[j]) j++;
            if (j == pat.Length) return i;
        }
        return -1;
    }
}

/// <summary>
/// Самотест прямой загрузки: локальный сервер с поддержкой частей (Range) и ограничением скорости;
/// 4 потока, пауза, продолжение, побайтная проверка файла. Папка «Загрузки» пользователя не трогается.
/// </summary>
static class DirectDownloadSelfTest
{
    public static void Run(string outFile)
    {
        var log = new List<string>();
        void Check(string name, bool ok, string? extra = null) => log.Add((ok ? "OK   " : "FAIL ") + name + (extra != null ? " — " + extra : ""));
        var root = Path.Combine(Path.GetTempPath(), "LiteDirectDl-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        Downloads.FolderOverride = root;
        var data = new byte[20 * 1024 * 1024];
        new Random(42).NextBytes(data);
        var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        int port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        int rangeRequests = 0;
        var cts = new CancellationTokenSource();
        _ = Task.Run(async () =>
        {
            while (!cts.IsCancellationRequested)
            {
                System.Net.Sockets.TcpClient c;
                try { c = await listener.AcceptTcpClientAsync(cts.Token); } catch { break; }
                _ = Task.Run(async () =>
                {
                    using var client = c;
                    try
                    {
                        var ns = client.GetStream();
                        var head = new StringBuilder();
                        var one = new byte[1];
                        while (!head.ToString().EndsWith("\r\n\r\n") && await ns.ReadAsync(one) > 0) head.Append((char)one[0]);
                        long from = 0, to = data.Length - 1; bool partial = false;
                        var mr = System.Text.RegularExpressions.Regex.Match(head.ToString(), @"Range: bytes=(\d+)-(\d*)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                        if (mr.Success) { partial = true; Interlocked.Increment(ref rangeRequests); from = long.Parse(mr.Groups[1].Value); if (mr.Groups[2].Value.Length > 0) to = long.Parse(mr.Groups[2].Value); }
                        long len = to - from + 1;
                        var hdr = (partial ? "HTTP/1.1 206 Partial Content\r\n" : "HTTP/1.1 200 OK\r\n") + "Content-Type: application/octet-stream\r\nAccept-Ranges: bytes\r\n" +
                                  $"Content-Length: {len}\r\n" + (partial ? $"Content-Range: bytes {from}-{to}/{data.Length}\r\n" : "") + "Connection: close\r\n\r\n";
                        await ns.WriteAsync(Encoding.ASCII.GetBytes(hdr));
                        // ~2 МБ/с на поток — чтобы успеть поставить на паузу
                        for (long p = from; p <= to; p += 65536)
                        {
                            int n = (int)Math.Min(65536, to - p + 1);
                            await ns.WriteAsync(data.AsMemory((int)p, n));
                            await Task.Delay(30);
                        }
                    }
                    catch { }
                });
            }
        });
        try
        {
            var it = Downloads.StartDirect($"http://127.0.0.1:{port}/big.bin", null, null, "big.bin");
            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (it.Received < 3_000_000 && sw.Elapsed.TotalSeconds < 20 && it.State == DlState.InProgress) Thread.Sleep(50);
            Check("4 потока для большого файла", it.Threads == Downloads.MaxThreads && it.CanPause, $"потоков {it.Threads}, получено {it.Received / 1024} КБ");
            it.Pause();
            Thread.Sleep(800);
            long atPause = it.Received;
            Thread.Sleep(1200);
            Check("пауза останавливает загрузку", it.Paused && it.Received == atPause && it.State == DlState.InProgress, $"{atPause / 1024} КБ");
            int beforeResume = rangeRequests;
            it.Resume();
            sw.Restart();
            while (it.State == DlState.InProgress && sw.Elapsed.TotalSeconds < 60) Thread.Sleep(100);
            Check("продолжение после паузы — докачка частями", it.State == DlState.Completed && rangeRequests > beforeResume, $"{it.State}, запросов частей после паузы {rangeRequests - beforeResume}");
            var got = File.Exists(it.Path) ? File.ReadAllBytes(it.Path) : Array.Empty<byte>();
            Check("файл совпадает побайтно", got.AsSpan().SequenceEqual(data), $"{got.Length / 1024} КБ из {data.Length / 1024}");
        }
        catch (Exception ex) { log.Add("FAIL исключение: " + ex); }
        finally
        {
            cts.Cancel(); listener.Stop();
            File.WriteAllLines(outFile, log);
            try { Directory.Delete(root, true); } catch { }
        }
    }
}

