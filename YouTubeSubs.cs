namespace LiteBrowser;

/// <summary>
/// Субтитры YouTube на языке интерфейса: если есть свои русские — включаем их, иначе — автоперевод YouTube
/// с языка оригинала (обычные или автоматические субтитры). Работает в самом плеере, ничего не скачивает.
/// </summary>
static class YouTubeSubs
{
    public static string Script(string lang) => $$"""
(async () => {
  const want = '{{lang}}';
  const p = document.getElementById('movie_player');
  if (!p || !p.getOption) return 'noplayer';
  try { p.loadModule('captions'); } catch (e) { }
  let list = [];
  for (let i = 0; i < 20 && !list.length; i++) { await new Promise(r => setTimeout(r, 250)); try { list = p.getOption('captions', 'tracklist') || []; } catch (e) { } }
  if (!list.length) return 'notracks';
  const own = list.find(t => (t.languageCode || '').startsWith(want) && t.kind !== 'asr') || list.find(t => (t.languageCode || '').startsWith(want));
  if (own) { p.setOption('captions', 'track', own); return 'own:' + own.languageCode; }
  const base = list.find(t => t.kind !== 'asr') || list[0];
  p.setOption('captions', 'track', { languageCode: base.languageCode, translationLanguage: { languageCode: want, languageName: want === 'ru' ? 'Русский' : 'English' } });
  return 'translated:' + base.languageCode;
})()
""";
}
