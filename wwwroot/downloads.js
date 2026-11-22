if (window.top !== window) document.documentElement.innerHTML = '';

const $ = id => document.getElementById(id);
const wv = window.chrome && window.chrome.webview;
const send = m => wv && wv.postMessage(m);
let items = [], timer = 0;
const hashes = {};

const size = b => b < 1024 ? b + ' Б' : b < 1048576 ? (b / 1024).toFixed(0) + ' КБ' : b < 1073741824 ? (b / 1048576).toFixed(1) + ' МБ' : (b / 1073741824).toFixed(2) + ' ГБ';
function dayName(d) {
  const t = new Date(); t.setHours(0, 0, 0, 0);
  const x = new Date(d); x.setHours(0, 0, 0, 0);
  const diff = Math.round((t - x) / 86400000);
  return diff === 0 ? 'Сегодня' : diff === 1 ? 'Вчера' : d.toLocaleDateString('ru-RU', { day: 'numeric', month: 'long', year: 'numeric' });
}
const ext = n => (n.split('.').pop() || '').toLowerCase();
const glyph = n => {
  const e = ext(n);
  if (['mp4', 'mkv', 'webm', 'avi', 'mov'].includes(e)) return '&#xE714;';
  if (['mp3', 'wav', 'flac', 'ogg', 'm4a'].includes(e)) return '&#xE8D6;';
  if (['jpg', 'jpeg', 'png', 'gif', 'webp', 'svg'].includes(e)) return '&#xEB9F;';
  if (['zip', 'rar', '7z', 'tar', 'gz'].includes(e)) return '&#xF012;';
  if (['exe', 'msi'].includes(e)) return '&#xE7AC;';
  if (['pdf', 'doc', 'docx', 'txt', 'xlsx'].includes(e)) return '&#xE8A5;';
  return '&#xE896;';
};

function render() {
  const q = $('q').value.trim().toLowerCase();
  const out = $('out');
  out.innerHTML = '';
  const shown = items.filter(i => !q || i.name.toLowerCase().includes(q) || i.url.toLowerCase().includes(q));
  if (!shown.length) { out.innerHTML = `<div class="empty">${q ? 'Ничего не найдено' : 'Загрузок пока нет'}</div>`; return; }
  let list = null, last = '';
  for (const it of shown) {
    const d = new Date(it.time), day = dayName(d);
    if (day !== last) {
      const h = document.createElement('div'); h.className = 'day'; h.textContent = day; out.appendChild(h);
      list = document.createElement('div'); list.className = 'list'; out.appendChild(list);
      last = day;
    }
    const active = it.state === 'InProgress', done = it.state === 'Completed';
    const row = document.createElement('div');
    row.className = 'item';
    row.innerHTML = `<div class="fi icon">${glyph(it.name)}</div>
      <div class="txt"><div class="name"></div><div class="meta"></div>${active ? '<div class="prog"><div></div></div>' : ''}</div>
      <div class="acts"></div>`;
    const name = row.querySelector('.name'), meta = row.querySelector('.meta'), acts = row.querySelector('.acts');
    name.textContent = it.name;
    if (done && !it.exists) name.classList.add('gone');
    let host = ''; try { host = new URL(it.url).hostname; } catch (e) { }
    meta.textContent = active
      ? `${it.paused ? 'Пауза · ' : ''}${size(it.received)}${it.total ? ' из ' + size(it.total) : ''}${it.speed && !it.paused ? ' · ' + size(it.speed) + '/с' : ''}${it.threads > 1 ? ' · ' + it.threads + ' потока' : ''} · ${host}`
      : done ? `${size(Math.max(it.total, it.received))} · ${host} · ${d.toLocaleTimeString('ru-RU', { hour: '2-digit', minute: '2-digit' })}${it.exists ? '' : ' · файл удалён'}`
      : it.canceled ? `Отменено · ${host}` : `Ошибка загрузки · ${host}`;
    if (!active && !done && !it.canceled) meta.classList.add('bad');
    if (done && it.exists && it.risk) {
      const w = document.createElement('div'); w.className = 'meta risk'; w.textContent = '⚠ ' + it.riskText; meta.after(w);
    }
    if (hashes[it.id]) {
      const h = document.createElement('div'); h.className = 'meta hash'; h.textContent = hashes[it.id]; h.title = 'Нажмите, чтобы скопировать';
      h.onclick = e => { e.stopPropagation(); navigator.clipboard.writeText(hashes[it.id].replace(/^SHA-256: /, '')); };
      (row.querySelector('.risk') || meta).after(h);
    }
    if (active) row.querySelector('.prog div').style.width = (it.total ? it.received / it.total * 100 : 30) + '%';
    const btn = (g, title, fn) => { const b = document.createElement('button'); b.className = 'act icon'; b.innerHTML = g; b.title = title; b.onclick = e => { e.stopPropagation(); fn(); }; acts.appendChild(b); };
    if (active && it.paused) btn('&#xE768;', 'Продолжить', () => send({ type: 'dl:resume', id: it.id }));
    else if (active && it.canPause) btn('&#xE769;', 'Пауза', () => send({ type: 'dl:pause', id: it.id }));
    if (active) btn('&#xE711;', 'Отменить', () => send({ type: 'dl:cancel', id: it.id }));
    else {
      if (done && it.exists) { btn('&#xE8E5;', 'Открыть', () => send({ type: 'dl:open', id: it.id })); btn('&#xE838;', 'Показать в папке', () => send({ type: 'dl:folder', id: it.id })); btn('&#xE72E;', 'Контрольная сумма SHA-256 (сравните с указанной на сайте)', () => { hashes[it.id] = 'Считаю SHA-256…'; render(); send({ type: 'dl:hash', id: it.id }); }); }
      btn('&#xE74D;', 'Убрать из списка', () => send({ type: 'dl:remove', id: it.id }));
    }
    if (done && it.exists) { row.style.cursor = 'pointer'; row.onclick = () => send({ type: 'dl:open', id: it.id }); }
    list.appendChild(row);
  }
}

$('q').oninput = render;
$('openFolder').onclick = () => send({ type: 'dl:folder', id: '' });
$('clear').onclick = () => { if (confirm('Очистить список загрузок? Сами файлы останутся на диске.')) send({ type: 'dl:clear' }); };

if (wv) {
  wv.addEventListener('message', e => {
    const m = e.data;
    if (m.type === 'dlHash') { hashes[m.id] = m.error ? 'Ошибка: ' + m.error : 'SHA-256: ' + m.sha256; render(); return; }
    if (m.type !== 'dlList') return;
    items = m.items;
    $('folder').textContent = 'Папка: ' + m.folder;
    render();
    clearTimeout(timer);
    timer = setTimeout(() => send({ type: 'dl:list' }), items.some(i => i.state === 'InProgress') ? 700 : 3000);
  });
  send({ type: 'dl:list' });
}
