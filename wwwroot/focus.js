const q = new URLSearchParams(location.search);
const host = q.get('host') || '';
const url = q.get('url') || '';
const until = +q.get('until') || 0;
const start = Date.now();
const total = Math.max(1, until - start);
document.getElementById('site').textContent = host.replace(/^www\./, '');

function tick() {
  const left = Math.max(0, until - Date.now());
  const m = Math.floor(left / 60000), s = Math.floor(left / 1000) % 60;
  const h = Math.floor(m / 60);
  document.getElementById('left').textContent = h > 0 ? h + ':' + String(m % 60).padStart(2, '0') + ':' + String(s).padStart(2, '0') : m + ':' + String(s).padStart(2, '0');
  document.getElementById('arc').style.strokeDashoffset = 326.7 * (1 - left / total);
  if (left <= 0 && url) location.href = url; // фокус закончился — открываем сайт
}
tick();
setInterval(tick, 1000);

document.getElementById('back').onclick = () => history.length > 1 ? history.back() : (location.href = 'home.html');
document.getElementById('allow').onclick = () => { location.href = 'focus-allow?url=' + encodeURIComponent(url); };
document.getElementById('end').onclick = () => { location.href = 'focus-end?url=' + encodeURIComponent(url); };
