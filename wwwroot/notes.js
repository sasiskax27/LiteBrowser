if (window.top !== window) document.documentElement.innerHTML = '';

const $ = id => document.getElementById(id);
const wv = window.chrome && window.chrome.webview;
const send = m => wv && wv.postMessage(m);
let items = [];

function render() {
  const q = $('q').value.trim().toLowerCase();
  const out = $('out');
  out.innerHTML = '';
  const shown = items.filter(i => !q || (i.text + ' ' + i.note + ' ' + i.title + ' ' + i.url).toLowerCase().includes(q));
  if (!shown.length) { out.innerHTML = `<div class="empty">${q ? 'Ничего не найдено' : 'Заметок пока нет'}</div>`; return; }
  const pages = new Map();
  for (const it of shown) { if (!pages.has(it.url)) pages.set(it.url, []); pages.get(it.url).push(it); }
  for (const [url, list] of pages) {
    const sec = document.createElement('div');
    sec.className = 'page';
    sec.innerHTML = '<div class="page-title"><a></a><span></span></div><div class="list"></div>';
    const a = sec.querySelector('a');
    a.textContent = list[0].title || url;
    a.href = url;
    let host = ''; try { host = new URL(url).hostname; } catch (e) { }
    sec.querySelector('span').textContent = host;
    const box = sec.querySelector('.list');
    for (const it of list) {
      const row = document.createElement('div');
      row.className = 'item';
      row.innerHTML = '<div class="txt"></div><div class="acts"><button class="act icon" title="Удалить">&#xE74D;</button></div>';
      const t = row.querySelector('.txt');
      t.className = 'txt ' + (it.text ? 'hl' : 'note');
      t.textContent = it.text ? '«' + it.text + '»' : it.note;
      row.querySelector('button').onclick = () => send({ type: 'notes:remove', id: it.id });
      box.appendChild(row);
    }
    out.appendChild(sec);
  }
}

$('q').oninput = render;
if (wv) {
  wv.addEventListener('message', e => { if (e.data.type === 'notesList') { items = e.data.items; render(); } });
  send({ type: 'notes:list' });
}
