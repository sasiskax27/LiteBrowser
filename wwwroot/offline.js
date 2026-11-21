if (window.top !== window) document.documentElement.innerHTML = '';

const $ = id => document.getElementById(id);
const wv = window.chrome && window.chrome.webview;
const send = m => wv && wv.postMessage(m);
const size = b => b < 1048576 ? Math.max(1, Math.round(b / 1024)) + ' КБ' : (b / 1048576).toFixed(1) + ' МБ';
let items = [];

function render() {
  const q = $('q').value.trim().toLowerCase();
  const out = $('out');
  out.innerHTML = '';
  const shown = items.filter(i => !q || (i.title + ' ' + i.url).toLowerCase().includes(q));
  if (!shown.length) { out.innerHTML = `<div class="empty">${q ? 'Ничего не найдено' : 'Сохранённых страниц пока нет'}</div>`; return; }
  const list = document.createElement('div');
  list.className = 'list';
  for (const it of shown) {
    const row = document.createElement('div');
    row.className = 'item';
    row.style.cursor = 'pointer';
    row.innerHTML = '<div class="fi icon">&#xE8A5;</div><div class="txt"><div class="name"></div><div class="meta"></div></div><div class="acts"></div>';
    row.querySelector('.name').textContent = it.title;
    let host = ''; try { host = new URL(it.url).hostname; } catch (e) { }
    const d = new Date(it.time);
    row.querySelector('.meta').textContent = `${host} · ${d.toLocaleDateString('ru-RU', { day: 'numeric', month: 'long' })} ${d.toLocaleTimeString('ru-RU', { hour: '2-digit', minute: '2-digit' })} · ${size(it.size)}`;
    const acts = row.querySelector('.acts');
    const btn = (g, title, fn) => { const b = document.createElement('button'); b.className = 'act icon'; b.innerHTML = g; b.title = title; b.onclick = e => { e.stopPropagation(); fn(); }; acts.appendChild(b); };
    btn('&#xE774;', 'Открыть оригинал в интернете', () => { location.href = it.url; });
    btn('&#xE74D;', 'Удалить', () => send({ type: 'off:remove', id: it.id }));
    row.onclick = () => send({ type: 'off:open', id: it.id });
    list.appendChild(row);
  }
  out.appendChild(list);
}

$('q').oninput = render;
if (wv) {
  wv.addEventListener('message', e => {
    if (e.data.type !== 'offList') return;
    items = e.data.items;
    if (items.length) $('sub').textContent = `${items.length} стр. · ${size(e.data.total)} · открываются без интернета`;
    render();
  });
  send({ type: 'off:list' });
}
