if (window.top !== window) document.documentElement.innerHTML = '';

const $ = id => document.getElementById(id);
const wv = window.chrome && window.chrome.webview;
let timer = 0, toastTimer = 0;

function toast(text) {
  $('toastText').textContent = text;
  $('toast').classList.add('show');
  clearTimeout(toastTimer);
  toastTimer = setTimeout(() => $('toast').classList.remove('show'), 1600);
}

function dayName(d) {
  const today = new Date(); today.setHours(0, 0, 0, 0);
  const x = new Date(d); x.setHours(0, 0, 0, 0);
  const diff = Math.round((today - x) / 86400000);
  if (diff === 0) return 'Сегодня';
  if (diff === 1) return 'Вчера';
  return d.toLocaleDateString('ru-RU', { weekday: 'long', day: 'numeric', month: 'long', year: x.getFullYear() === today.getFullYear() ? undefined : 'numeric' });
}

function render(items) {
  const out = $('out');
  out.innerHTML = '';
  if (!items.length) {
    out.innerHTML = '<div class="empty"></div>';
    out.firstChild.textContent = $('q').value ? 'Ничего не найдено' : 'История пуста';
    return;
  }
  let list = null, lastDay = '';
  for (const it of items) {
    const d = new Date(it.time);
    const day = dayName(d);
    if (day !== lastDay) {
      const h = document.createElement('div'); h.className = 'day'; h.textContent = day; out.appendChild(h);
      list = document.createElement('div'); list.className = 'list'; out.appendChild(list);
      lastDay = day;
    }
    const a = document.createElement('a');
    a.className = 'item';
    a.href = it.url;
    a.innerHTML = '<span class="time"></span><span class="txt"><div class="title"></div><div class="url"></div></span><button class="del icon" title="Удалить">&#xE711;</button>';
    a.querySelector('.time').textContent = d.toLocaleTimeString('ru-RU', { hour: '2-digit', minute: '2-digit' });
    a.querySelector('.title').textContent = it.title || it.url;
    a.querySelector('.url').textContent = it.url;
    a.querySelector('.del').onclick = ev => {
      ev.preventDefault(); ev.stopPropagation();
      wv.postMessage({ type: 'historyDelete', url: it.url, time: it.time });
      a.remove();
    };
    list.appendChild(a);
  }
}

function load() { wv.postMessage({ type: 'historyGet', q: $('q').value }); }

$('q').oninput = () => { clearTimeout(timer); timer = setTimeout(load, 200); };
$('clear').onclick = () => { if (confirm('Удалить всю историю посещений?')) wv.postMessage({ type: 'historyClear' }); };

if (wv) {
  wv.addEventListener('message', e => {
    const m = e.data;
    if (m.type === 'history') { $('off').style.display = m.saving ? 'none' : 'block'; render(m.items); }
    else if (m.type === 'done') { toast(m.text); load(); }
  });
  load();
}
