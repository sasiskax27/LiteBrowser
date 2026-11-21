if (window.top !== window) document.documentElement.innerHTML = '';

const $ = id => document.getElementById(id);
const wv = window.chrome && window.chrome.webview;
const send = m => wv && wv.postMessage(m);

const size = b => !b ? '—' : b < 1024 ? b + ' Б' : b < 1048576 ? (b / 1024).toFixed(1) + ' КБ' : (b / 1048576).toFixed(2) + ' МБ';
const ms = v => !v ? '—' : v < 1000 ? Math.round(v) + ' мс' : (v / 1000).toFixed(2) + ' с';
const el = (tag, cls, text) => { const e = document.createElement(tag); if (cls) e.className = cls; if (text != null) e.textContent = text; return e; };

let since = 0, gen = -1, target = null, preserve = false, sel = null;
const net = new Map();      // id → запрос
let logs = [];              // записи консоли
let typeFilter = 'all', levelFilter = 'all';
const hist = []; let histPos = 0;

const TYPES = [
  ['all', 'Все'], ['Fetch', 'Fetch/XHR'], ['Script', 'JS'], ['Stylesheet', 'CSS'], ['Image', 'Картинки'],
  ['Media', 'Медиа'], ['Font', 'Шрифты'], ['Document', 'Документы'], ['blocked', 'Заблокировано'], ['third', 'Сторонние'], ['err', 'Ошибки'],
];
const LEVELS = [['all', 'Все'], ['error', 'Ошибки'], ['warn', 'Предупреждения'], ['log', 'Сообщения'], ['info', 'Инфо']];

function chips(host, list, get, set) {
  host.innerHTML = '';
  for (const [id, label] of list) {
    const b = el('button', 'chip' + (get() === id ? ' on' : ''), label);
    b.onclick = () => { set(id); chips(host, list, get, set); };
    host.appendChild(b);
  }
}
chips($('chips'), TYPES, () => typeFilter, v => { typeFilter = v; renderNet(); });
chips($('lchips'), LEVELS, () => levelFilter, v => { levelFilter = v; renderLog(); });

document.querySelectorAll('.tab').forEach(t => t.onclick = () => {
  document.querySelectorAll('.tab').forEach(x => x.classList.toggle('on', x === t));
  document.querySelectorAll('.pane').forEach(p => p.classList.toggle('on', p.id === t.dataset.p));
  if (t.dataset.p === 'perf') send({ type: 'dev:perf' });
  if (t.dataset.p === 'con') { $('expr').focus(); const l = $('log'); l.scrollTop = l.scrollHeight; }
});

function matchType(r) {
  switch (typeFilter) {
    case 'all': return true;
    case 'Fetch': return r.rtype === 'Fetch' || r.rtype === 'XHR';
    case 'blocked': return r.blocked;
    case 'third': return r.third;
    case 'err': return !r.blocked && (r.error || r.status >= 400);
    default: return r.rtype === typeFilter;
  }
}

function shortName(url) {
  try {
    const u = new URL(url);
    const p = u.pathname.split('/').filter(Boolean).pop() || u.hostname;
    return decodeURIComponent(p) + (u.search ? u.search.slice(0, 40) : '');
  } catch (e) { return url.slice(0, 80); }
}
const hostOf = url => { try { return new URL(url).hostname; } catch (e) { return ''; } };

function statusCell(r) {
  const s = el('div', 'st');
  if (r.blocked) { s.textContent = '✕'; s.classList.add('e'); s.title = 'Заблокировано'; }
  else if (r.error) { s.textContent = 'ERR'; s.classList.add('e'); s.title = r.error; }
  else if (!r.status) { s.textContent = '…'; s.classList.add('p'); }
  else { s.textContent = r.status; if (r.status >= 400) s.classList.add('e'); else if (r.status >= 300) s.classList.add('w'); }
  return s;
}

let netTimer = 0;
function renderNet() {
  clearTimeout(netTimer); netTimer = 0;
  const q = $('nq').value.trim().toLowerCase();
  const host = $('reqs');
  const atBottom = host.scrollTop + host.clientHeight >= host.scrollHeight - 30;
  host.innerHTML = '';
  let count = 0, bytes = 0, blocked = 0, third = 0, shown = 0;
  const frag = document.createDocumentFragment();
  for (const r of net.values()) {
    count++; bytes += r.size || 0; if (r.blocked) blocked++; if (r.third) third++;
    if (!matchType(r) || (q && !r.url.toLowerCase().includes(q))) continue;
    shown++;
    const row = el('div', 'req' + (r.blocked ? ' blocked' : '') + (sel === r.id ? ' sel' : ''));
    const nm = el('div', 'nm');
    const n1 = el('div', null, shortName(r.url));
    n1.title = r.url;
    if (r.cached) n1.appendChild(el('span', 'badge', 'кэш'));
    if (r.third && !r.blocked) n1.appendChild(el('span', 'badge t', '3rd'));
    nm.append(n1, el('div', 'h', hostOf(r.url) + ' · ' + (r.rtype || '?') + (r.method && r.method !== 'GET' ? ' · ' + r.method : '')));
    row.append(statusCell(r), nm, el('div', 'sz', size(r.size)), el('div', 'ms', r.done ? ms(r.ms) : '…'));
    row.onclick = () => { sel = r.id; renderNet(); showDetail(r); };
    frag.appendChild(row);
  }
  host.appendChild(frag);
  if (!shown) host.appendChild(el('div', 'empty', count ? 'Под фильтр ничего не подходит' : 'Обновите страницу, чтобы увидеть её запросы'));
  if (atBottom) host.scrollTop = host.scrollHeight;
  $('nfoot').textContent = '';
  for (const t of [`${shown} из ${count} запросов`, `${size(bytes)} передано`, `${blocked} заблокировано`, `${third} сторонних`])
    $('nfoot').appendChild(el('span', null, t));
}
const scheduleNet = () => { if (!netTimer) netTimer = setTimeout(renderNet, 150); };

function showDetail(r) {
  const d = $('detail');
  d.classList.add('on');
  d.innerHTML = '';
  d.appendChild(el('div', 'u', r.url));
  const kv = el('div', 'kv');
  const rows = [
    ['Метод', r.method], ['Статус', r.blocked ? 'заблокирован браузером' : r.error ? r.error : r.status || 'ожидание'], ['Тип', r.rtype],
    ['MIME', r.mime], ['Размер', size(r.size)], ['Время', ms(r.ms)], ['Протокол', r.proto], ['IP-адрес', r.ip],
    ['Источник', r.cached ? 'кэш' : 'сеть'], ['Сторонний', r.third ? 'да' : 'нет'],
  ];
  for (const [k, v] of rows) if (v) kv.append(el('span', null, k), el('span', null, String(v)));
  d.appendChild(kv);
  const btns = el('div', 'btns');
  const b1 = el('button', 'btn', 'Копировать адрес'); b1.onclick = () => navigator.clipboard.writeText(r.url);
  const b2 = el('button', 'btn', 'Копировать как curl');
  b2.onclick = () => navigator.clipboard.writeText(`curl${r.method && r.method !== 'GET' ? ' -X ' + r.method : ''} "${r.url.replace(/"/g, '\\"')}"`);
  const b3 = el('button', 'btn', 'Открыть во вкладке'); b3.onclick = () => send({ type: 'dev:open', url: r.url });
  const b4 = el('button', 'btn', 'Закрыть'); b4.onclick = () => { d.classList.remove('on'); sel = null; renderNet(); };
  btns.append(b1, b2);
  if (/^https?:/.test(r.url)) btns.append(b3);
  btns.append(b4);
  d.appendChild(btns);
}

function addLine(cls, text, src, time) {
  const ln = el('div', 'ln ' + cls);
  ln.appendChild(el('div', 'tx', text));
  if (src || time) { const s = el('div', 'src', src || time); s.title = (time || '') + (src ? ' ' + src : ''); ln.appendChild(s); }
  return ln;
}

function renderLog() {
  const host = $('log');
  host.innerHTML = '';
  const frag = document.createDocumentFragment();
  for (const l of logs) {
    if (levelFilter !== 'all' && l.level !== levelFilter && !(l.level === 'in' || l.level === 'result')) continue;
    frag.appendChild(addLine(l.level + (l.err ? ' err' : ''), l.text, l.src, l.time));
  }
  host.appendChild(frag);
  if (!logs.length) host.appendChild(el('div', 'empty', 'Здесь появятся сообщения и ошибки страницы'));
  host.scrollTop = host.scrollHeight;
  const errs = logs.filter(l => l.level === 'error').length;
  $('errCount').style.display = errs ? '' : 'none';
  $('errCount').textContent = errs;
}

function pushLog(l) {
  logs.push(l);
  if (logs.length > 1000) logs.shift();
}

$('expr').addEventListener('keydown', e => {
  const t = e.target;
  if (e.key === 'Enter' && !e.shiftKey) {
    e.preventDefault();
    const v = t.value.trim();
    if (!v) return;
    hist.push(v); histPos = hist.length;
    pushLog({ level: 'in', text: v });
    renderLog();
    send({ type: 'dev:eval', expr: v });
    t.value = ''; t.style.height = '26px';
  } else if (e.key === 'ArrowUp' && !t.value.includes('\n') && histPos > 0) {
    e.preventDefault(); t.value = hist[--histPos];
  } else if (e.key === 'ArrowDown' && !t.value.includes('\n') && histPos < hist.length) {
    e.preventDefault(); histPos++; t.value = hist[histPos] || '';
  } else if (e.key === 'l' && e.ctrlKey) {
    e.preventDefault(); logs = []; renderLog();
  }
});
$('expr').addEventListener('input', e => { e.target.style.height = '26px'; e.target.style.height = Math.min(140, e.target.scrollHeight) + 'px'; });

$('nq').oninput = renderNet;
$('nclear').onclick = () => { net.clear(); logs = []; sel = null; $('detail').classList.remove('on'); send({ type: 'dev:clear' }); renderNet(); renderLog(); };
$('preserve').onclick = () => { preserve = !preserve; $('preserve').classList.toggle('on', preserve); };
$('native').onclick = () => send({ type: 'dev:native' });
$('perfRun').onclick = () => send({ type: 'dev:perf' });

function card(value, label, cls) { const c = el('div', 'card' + (cls ? ' ' + cls : '')); c.append(el('b', null, value), el('span', null, label)); return c; }
function grade(v, good, mid) { return !v ? '' : v <= good ? 'good' : v <= mid ? 'mid' : 'slow'; }

function renderPerf(m) {
  const host = $('cards');
  host.innerHTML = '';
  let t = {};
  try { t = JSON.parse(m.timing || '{}'); } catch (e) { }
  $('perfUrl').textContent = t.title || '';
  host.append(
    card(ms(t.ttfb), 'Ответ сервера', grade(t.ttfb, 200, 600)),
    card(ms(t.fcp), 'Первая отрисовка', grade(t.fcp, 1800, 3000)),
    card(ms(t.dom), 'DOM готов', grade(t.dom, 2000, 4000)),
    card(ms(t.load), 'Полная загрузка', grade(t.load, 3000, 6000)),
    card(String(t.resources ?? '—'), 'Ресурсов'),
    card(size((t.resBytes || 0) + (t.transfer || 0)), 'Передано'),
    card(size(m.heap), 'Память JS'),
    card(Math.round(m.nodes || t.dom_nodes || 0).toLocaleString('ru-RU'), 'Элементов DOM', grade(m.nodes, 1500, 3000)),
    card(Math.round(m.listeners || 0).toLocaleString('ru-RU'), 'Обработчиков событий'),
    card(String(Math.round(m.frames || 0)), 'Фреймов'),
    card(ms(m.scriptMs), 'Работа скриптов'),
    card(ms(m.layoutMs), 'Вёрстка (layout)'),
    card(String(t.cookies ?? '—'), 'Cookie страницы'),
    card(t.storage < 0 ? 'нет' : String(t.storage ?? '—'), 'Ключей localStorage'),
  );
}

if (wv) {
  wv.addEventListener('message', e => {
    const m = e.data;
    if (m.type === 'devData') {
      if (m.target !== target || m.gen !== gen) {
        if (m.target !== target || !preserve) { net.clear(); sel = null; $('detail').classList.remove('on'); }
        if (m.target !== target) { logs = []; since = 0; target = m.target; gen = m.gen; send({ type: 'dev:poll', since: 0, preserve }); return; }
        gen = m.gen;
      }
      $('target').textContent = m.title || m.url;
      $('target').title = m.url;
      let changed = false;
      for (const r of m.net) { net.set(r.id, r); since = Math.max(since, r.seq); changed = true; if (sel === r.id) showDetail(r); }
      if (net.size > 1500) { const drop = net.size - 1500; let i = 0; for (const k of net.keys()) { if (i++ >= drop) break; net.delete(k); } }
      if (changed) scheduleNet();
      if (m.console.length) {
        for (const c of m.console) { pushLog({ level: c.level, text: c.text, src: c.src, time: c.time }); since = Math.max(since, c.seq); }
        renderLog();
      }
    } else if (m.type === 'devEval') {
      pushLog({ level: 'result', text: m.text, err: m.error });
      renderLog();
    } else if (m.type === 'devPerf') renderPerf(m);
    else if (m.type === 'devNoTarget') $('target').textContent = 'Откройте сайт во вкладке';
  });
  const poll = () => { if (!document.hidden) send({ type: 'dev:poll', since, preserve }); };
  setInterval(poll, 700);
  poll();
  renderNet(); renderLog();
}
