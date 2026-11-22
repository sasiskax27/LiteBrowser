if (window.top !== window) document.documentElement.innerHTML = '';

const $ = id => document.getElementById(id);
const wv = window.chrome && window.chrome.webview;
const send = m => wv && wv.postMessage(m);
let items = [], filter = 'unread';

function ago(t) {
  const d = new Date(t), days = Math.floor((Date.now() - d) / 86400000);
  return days === 0 ? 'сегодня' : days === 1 ? 'вчера' : d.toLocaleDateString('ru-RU', { day: 'numeric', month: 'long' });
}

function render() {
  const q = $('q').value.trim().toLowerCase();
  const out = $('out');
  out.innerHTML = '';
  const shown = items.filter(i => (filter === 'all' || (filter === 'read') === i.read) && (!q || (i.title + ' ' + i.url).toLowerCase().includes(q)));
  const unread = items.filter(i => !i.read).length;
  $('sub').textContent = items.length ? `Непрочитанных: ${unread} из ${items.length}` : 'Добавить страницу: меню → «Эта страница» → «Прочитать позже» или Ctrl+Shift+L';
  if (!shown.length) { out.innerHTML = `<div class="empty">${q ? 'Ничего не найдено' : filter === 'read' ? 'Прочитанных пока нет' : 'Список пуст — всё прочитано'}</div>`; return; }
  const list = document.createElement('div');
  list.className = 'list';
  for (const it of shown) {
    const row = document.createElement('div');
    row.className = 'item' + (it.read ? ' read' : '');
    row.style.cursor = 'pointer';
    row.innerHTML = `<div class="fi icon${it.read ? ' read' : ''}">${it.read ? '&#xE73E;' : '&#xE7BC;'}</div><div class="txt"><div class="name"></div><div class="meta"></div></div><div class="acts"></div>`;
    row.querySelector('.name').textContent = it.title;
    let host = ''; try { host = new URL(it.url).hostname.replace(/^www\./, ''); } catch (e) { }
    row.querySelector('.meta').textContent = host + ' · ' + ago(it.added);
    const acts = row.querySelector('.acts');
    const btn = (g, title, fn) => { const b = document.createElement('button'); b.className = 'act icon'; b.innerHTML = g; b.title = title; b.onclick = e => { e.stopPropagation(); fn(); }; acts.appendChild(b); };
    btn(it.read ? '&#xE7BC;' : '&#xE73E;', it.read ? 'Вернуть в непрочитанные' : 'Отметить прочитанным', () => send({ type: 'rl:read', url: it.url, read: !it.read }));
    btn('&#xE74D;', 'Удалить', () => send({ type: 'rl:remove', url: it.url }));
    row.onclick = () => send({ type: 'rl:open', url: it.url });
    list.appendChild(row);
  }
  out.appendChild(list);
}

document.querySelectorAll('.tabs button').forEach(b => b.onclick = () => {
  filter = b.dataset.f;
  document.querySelectorAll('.tabs button').forEach(x => x.classList.toggle('on', x === b));
  render();
});
$('q').oninput = render;
if (wv) {
  wv.addEventListener('message', e => { if (e.data.type === 'rlList') { items = e.data.items; render(); } });
  send({ type: 'rl:list' });
}
