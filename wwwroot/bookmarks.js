if (window.top !== window) document.documentElement.innerHTML = '';

const $ = id => document.getElementById(id);
const wv = window.chrome && window.chrome.webview;
const send = m => wv && wv.postMessage(m);
let items = [];

function render() {
  const q = $('q').value.trim().toLowerCase();
  const out = $('out');
  out.innerHTML = '';
  $('count').textContent = `Всего: ${items.length}`;
  const shown = items.filter(i => !q || i.title.toLowerCase().includes(q) || i.url.toLowerCase().includes(q));
  if (!shown.length) { out.innerHTML = `<div class="empty">${q ? 'Ничего не найдено' : 'Закладок пока нет — нажмите ☆ в адресной строке'}</div>`; return; }
  for (const it of shown) {
    const a = document.createElement('a');
    a.className = 'item';
    a.href = it.url;
    a.innerHTML = '<div class="fi icon">&#xE774;</div><div class="txt"><div class="name"></div><div class="meta"></div></div><div class="acts"></div>';
    if (it.icon) a.querySelector('.fi').innerHTML = `<img src="data:image/png;base64,${it.icon}" alt="">`;
    a.querySelector('.name').textContent = it.title;
    a.querySelector('.meta').textContent = it.url;
    const acts = a.querySelector('.acts');
    const btn = (g, title, fn) => { const b = document.createElement('button'); b.className = 'act icon'; b.innerHTML = g; b.title = title; b.onclick = e => { e.preventDefault(); e.stopPropagation(); fn(); }; acts.appendChild(b); };
    btn('&#xE70F;', 'Переименовать', () => { const t = prompt('Название закладки', it.title); if (t && t.trim()) { send({ type: 'bm:rename', url: it.url, title: t }); it.title = t; render(); } });
    btn('&#xE74D;', 'Удалить', () => send({ type: 'bm:remove', url: it.url }));
    out.appendChild(a);
  }
}

$('q').oninput = render;
$('add').onclick = () => {
  const url = prompt('Адрес сайта');
  if (!url) return;
  const title = prompt('Название (можно оставить пустым)') || '';
  send({ type: 'bm:add', url, title });
};

if (wv) {
  wv.addEventListener('message', e => { if (e.data.type === 'bmList') { items = e.data.items; render(); } });
  send({ type: 'bm:list' });
}
