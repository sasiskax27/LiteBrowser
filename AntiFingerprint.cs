using System.Security.Cryptography;
using System.Text.Json;

namespace LiteBrowser;

/// <summary>
/// Защита от слежки по «отпечатку» браузера: сайтам отдаём чуть искажённые данные холста (canvas), звука и видеокарты,
/// а число ядер и памяти — округлёнными. В пределах сеанса искажение для сайта одно и то же (сайты не ломаются),
/// но у разных сайтов и в разных сеансах оно разное — связать вас между сайтами по отпечатку не получится.
/// Сайты из исключений блокировщика (щит в адресной строке) не трогаем.
/// </summary>
static class AntiFingerprint
{
    static readonly string sessionKey = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));

    public static string Script(IEnumerable<string> excluded) => Template
        .Replace("__KEY__", sessionKey)
        .Replace("__EXCLUDED__", JsonSerializer.Serialize(excluded.ToArray()));

    const string Template = """
(() => {
  if (window.__liteFp) return; window.__liteFp = 1;
  const host = location.hostname;
  const excluded = __EXCLUDED__;
  if (!host || excluded.some(h => host === h || host.endsWith('.' + h))) return;
  // Зерно: ключ сеанса + сайт (первого уровня) — одинаково на всех страницах сайта в этом сеансе
  const site = host.split('.').slice(-2).join('.');
  let seed = 2166136261;
  for (const ch of '__KEY__' + site) { seed ^= ch.charCodeAt(0); seed = Math.imul(seed, 16777619) >>> 0; }
  const rnd = () => { seed ^= seed << 13; seed >>>= 0; seed ^= seed >> 17; seed ^= seed << 5; seed >>>= 0; return seed / 4294967296; };
  const noise = [...Array(64)].map(() => rnd());
  // Обёртки как у «родных» функций: toString() показывает [native code]
  const wrap = (obj, name, make) => {
    const orig = obj[name]; if (typeof orig !== 'function') return;
    obj[name] = new Proxy(orig, { apply: (t, self, args) => make(t, self, args) });
  };

  // ---- Холст: едва заметный шум в нескольких пикселях при чтении картинки ----
  const shift = (data, w, h) => {
    if (!data || data.length < 16) return;
    for (let i = 0; i < 10; i++) {
      const px = Math.floor(noise[i] * (data.length / 4)) * 4;
      const c = Math.floor(noise[i + 10] * 3);
      data[px + c] = data[px + c] ^ 1;
    }
  };
  wrap(CanvasRenderingContext2D.prototype, 'getImageData', (t, self, args) => {
    const img = Reflect.apply(t, self, args);
    shift(img.data); return img;
  });
  const noisyCopy = canvas => {
    try {
      if (!canvas.width || !canvas.height || canvas.width * canvas.height > 4000000) return canvas;
      const c = document.createElement('canvas'); c.width = canvas.width; c.height = canvas.height;
      const x = c.getContext('2d'); x.drawImage(canvas, 0, 0);
      const img = CanvasRenderingContext2D.prototype.getImageData.call(x, 0, 0, c.width, c.height); // уже с шумом
      x.putImageData(img, 0, 0);
      return c;
    } catch (e) { return canvas; }
  };
  wrap(HTMLCanvasElement.prototype, 'toDataURL', (t, self, args) => Reflect.apply(t, noisyCopy(self), args));
  wrap(HTMLCanvasElement.prototype, 'toBlob', (t, self, args) => Reflect.apply(t, noisyCopy(self), args));

  // ---- Видеокарта: общие названия вместо точной модели ----
  const GL_VENDOR = 0x9245, GL_RENDERER = 0x9246;
  for (const proto of [window.WebGLRenderingContext && WebGLRenderingContext.prototype, window.WebGL2RenderingContext && WebGL2RenderingContext.prototype]) {
    if (!proto) continue;
    wrap(proto, 'getParameter', (t, self, args) => {
      if (args[0] === GL_VENDOR) return 'Google Inc.';
      if (args[0] === GL_RENDERER) return 'ANGLE (Generic, Direct3D11)';
      return Reflect.apply(t, self, args);
    });
  }

  // ---- Звук: крошечный шум в обработанном сигнале (неслышно) ----
  if (window.AudioBuffer) wrap(AudioBuffer.prototype, 'getChannelData', (t, self, args) => {
    const d = Reflect.apply(t, self, args);
    if (!d.__lite) { for (let i = 0; i < d.length; i += 100) d[i] += (noise[i % 64] - 0.5) * 1e-7; try { Object.defineProperty(d, '__lite', { value: 1 }); } catch (e) { } }
    return d;
  });
  if (window.AnalyserNode) wrap(AnalyserNode.prototype, 'getFloatFrequencyData', (t, self, args) => {
    const r = Reflect.apply(t, self, args);
    const a = args[0]; if (a) for (let i = 0; i < a.length; i += 10) a[i] += (noise[i % 64] - 0.5) * 0.1;
    return r;
  });

  // ---- Железо: округлённые значения, как у большинства ----
  try {
    const cores = Math.min(navigator.hardwareConcurrency || 4, 8) <= 4 ? 4 : 8;
    Object.defineProperty(Navigator.prototype, 'hardwareConcurrency', { get: () => cores, configurable: true });
    if ('deviceMemory' in navigator) Object.defineProperty(Navigator.prototype, 'deviceMemory', { get: () => 8, configurable: true });
  } catch (e) { }
})();
""";
}
