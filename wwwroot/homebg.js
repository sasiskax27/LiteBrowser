// Живые обои стартовой страницы: видео пользователя или встроенная анимация (аврора, звёзды, волны).
// Анимация рисуется только пока вкладка видна (requestAnimationFrame сам останавливается в фоне)
// и выключается, если в Windows отключены анимации.
'use strict';
const HomeBg = (() => {
  let canvas = null, video = null, raf = 0, kind = '';
  const still = matchMedia('(prefers-reduced-motion: reduce)').matches;

  function accent() {
    const cs = getComputedStyle(document.documentElement);
    const hex = s => { s = s.trim().replace('#', ''); if (s.length === 3) s = [...s].map(c => c + c).join(''); const n = parseInt(s, 16); return [n >> 16 & 255, n >> 8 & 255, n & 255]; };
    try { return [hex(cs.getPropertyValue('--accent') || '#7c9cff'), hex(cs.getPropertyValue('--accent2') || '#b07cff')]; }
    catch { return [[124, 156, 255], [176, 124, 255]]; }
  }

  function clear() {
    cancelAnimationFrame(raf); raf = 0;
    if (canvas) { canvas.remove(); canvas = null; }
    if (video) { video.pause(); video.remove(); video = null; }
    document.body.classList.remove('live');
    kind = '';
  }

  function makeCanvas() {
    canvas = document.createElement('canvas');
    canvas.className = 'livebg';
    document.body.prepend(canvas);
    const fit = () => { const d = Math.min(devicePixelRatio || 1, 1.5); canvas.width = innerWidth * d; canvas.height = innerHeight * d; };
    fit(); addEventListener('resize', fit);
    document.body.classList.add('live');
    return canvas.getContext('2d');
  }

  function stars() {
    const g = makeCanvas();
    const pts = Array.from({ length: 220 }, () => ({ x: Math.random(), y: Math.random(), z: Math.random() * .8 + .2, p: Math.random() * 6.28 }));
    const [a] = accent();
    const frame = t => {
      const w = canvas.width, h = canvas.height;
      const sky = g.createLinearGradient(0, 0, 0, h);
      sky.addColorStop(0, '#05060c'); sky.addColorStop(1, `rgb(${a[0] / 6 | 0},${a[1] / 6 | 0},${a[2] / 5 | 0})`);
      g.fillStyle = sky; g.fillRect(0, 0, w, h);
      for (const s of pts) {
        s.x -= .00003 * s.z; if (s.x < 0) s.x += 1;
        const tw = .55 + .45 * Math.sin(t / 900 * s.z + s.p);
        g.globalAlpha = tw * s.z;
        g.fillStyle = s.z > .85 ? `rgb(${a.join(',')})` : '#fff';
        g.beginPath(); g.arc(s.x * w, s.y * h, s.z * 2 * (w / innerWidth), 0, 6.28); g.fill();
      }
      g.globalAlpha = 1;
      if (!still) raf = requestAnimationFrame(frame);
    };
    frame(performance.now()); // первый кадр сразу, дальше — по кадрам экрана
  }

  function waves() {
    const g = makeCanvas();
    const [a, b] = accent();
    const frame = t => {
      const w = canvas.width, h = canvas.height;
      g.fillStyle = '#0b0c12'; g.fillRect(0, 0, w, h);
      for (let i = 0; i < 4; i++) {
        const c = i % 2 ? b : a, amp = h * (.035 + i * .012), base = h * (.62 + i * .09), k = (i + 1.3) / w * 3.1, sp = t / (5200 - i * 700);
        g.beginPath(); g.moveTo(0, h);
        for (let x = 0; x <= w; x += 8) g.lineTo(x, base + Math.sin(x * k + sp) * amp + Math.sin(x * k * .45 - sp * 1.3) * amp * .6);
        g.lineTo(w, h); g.closePath();
        g.fillStyle = `rgba(${c.join(',')},${.10 + i * .045})`; g.fill();
      }
      if (!still) raf = requestAnimationFrame(frame);
    };
    frame(performance.now()); // первый кадр сразу, дальше — по кадрам экрана
  }

  function aurora() {
    const g = makeCanvas();
    const [a, b] = accent();
    const green = [70, 230, 170];
    const frame = t => {
      const w = canvas.width, h = canvas.height;
      g.fillStyle = '#06070d'; g.fillRect(0, 0, w, h);
      g.globalCompositeOperation = 'lighter';
      [green, a, b].forEach((c, i) => {
        const s = t / (9000 + i * 2500);
        for (let j = 0; j < 3; j++) {
          const x = w * (.5 + .38 * Math.sin(s + j * 2.1 + i)), y = h * (.28 + .14 * Math.cos(s * 1.3 + j + i * 1.7));
          const r = Math.max(w, h) * (.32 + .08 * Math.sin(s * 2 + j));
          const gr = g.createRadialGradient(x, y, 0, x, y, r);
          gr.addColorStop(0, `rgba(${c.join(',')},.42)`); gr.addColorStop(1, 'rgba(0,0,0,0)');
          g.fillStyle = gr; g.fillRect(0, 0, w, h);
        }
      });
      g.globalCompositeOperation = 'source-over';
      if (!still) raf = requestAnimationFrame(frame);
    };
    frame(performance.now()); // первый кадр сразу, дальше — по кадрам экрана
  }

  function showVideo(src) {
    video = document.createElement('video');
    Object.assign(video, { src, muted: true, loop: true, autoplay: true, playsInline: true });
    video.className = 'livebg';
    document.body.prepend(video);
    document.body.classList.add('live');
    video.play().catch(() => {});
    // В фоне видео не крутим — бережём батарею
    document.addEventListener('visibilitychange', () => { if (!video) return; document.hidden ? video.pause() : video.play().catch(() => {}); });
  }

  /** settings: wallpaper (bool), wallpaperVideo (bool), wallpaperVersion, homeAnim ('' | aurora | stars | waves) */
  function apply(s) {
    const want = s.wallpaper && s.wallpaperVideo ? 'video' + s.wallpaperVersion : !s.wallpaper && s.homeAnim ? s.homeAnim : '';
    if (want === kind) return;
    clear();
    kind = want;
    if (want.startsWith('video')) showVideo('wallpaper?v=' + s.wallpaperVersion);
    else if (want === 'stars') stars();
    else if (want === 'waves') waves();
    else if (want === 'aurora') aurora();
  }
  return { apply };
})();
