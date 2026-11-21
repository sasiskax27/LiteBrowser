if (window.top !== window) document.documentElement.innerHTML = '';

const $ = id => document.getElementById(id);
const wv = window.chrome && window.chrome.webview;
const send = m => wv && wv.postMessage(m);
let toastTimer = 0;

function toast(text) {
  $('toastText').textContent = text;
  $('toast').classList.add('show');
  clearTimeout(toastTimer);
  toastTimer = setTimeout(() => $('toast').classList.remove('show'), 2000);
}

function status(text, ok) {
  $('status').className = 'status ' + (ok ? 'ok' : 'bad');
  $('status').textContent = text;
}

function install(url) {
  if (!url.trim()) return;
  status('Готовлю установку…', true);
  send({ type: 'ext:install', url: url.trim() });
}

$('go').onclick = () => install($('url').value);
$('url').addEventListener('keydown', e => { if (e.key === 'Enter') install($('url').value); });
document.querySelectorAll('.store').forEach(b => b.onclick = () => send({ type: 'ext:store', store: b.dataset.store }));

function renderList(items) {
  const box = $('list');
  box.innerHTML = '';
  if (!items.length) { box.innerHTML = '<div class="empty">Пока ничего не установлено</div>'; return; }
  for (const it of items) {
    const row = document.createElement('div');
    row.className = 'ext' + (it.enabled ? '' : ' off');
    row.innerHTML = `
      ${it.icon ? '<img alt="">' : '<div class="noicon icon">&#xEA86;</div>'}
      <div class="body">
        <div class="title"><span class="n"></span><small class="v"></small></div>
        <div class="desc"></div>
        <div class="rights"></div>
        <div class="acts">
          <button class="btn primary open">Открыть</button>
          <button class="btn opts">Настройки</button>
          <button class="btn danger rm">Удалить</button>
        </div>
      </div>
      <label class="switch" title="Включить/выключить"><input type="checkbox"><span class="slider"></span></label>`;
    if (it.icon) row.querySelector('img').src = it.icon;
    row.querySelector('.n').textContent = it.name;
    row.querySelector('.v').textContent = [it.version, it.store].filter(Boolean).join(' · ');
    row.querySelector('.desc').textContent = it.desc || '';
    const r = row.querySelector('.rights');
    if (it.rights.length) { r.innerHTML = 'Права: <b></b>'; r.querySelector('b').textContent = it.rights.join(', '); }
    const open = row.querySelector('.open'), opts = row.querySelector('.opts');
    if (!it.popup) open.style.display = 'none';
    if (!it.options) opts.style.display = 'none';
    open.onclick = () => send({ type: 'ext:open', id: it.id, page: 'popup' });
    opts.onclick = () => send({ type: 'ext:open', id: it.id, page: 'options' });
    row.querySelector('.rm').onclick = () => { if (confirm(`Удалить расширение «${it.name}»?`)) send({ type: 'ext:remove', id: it.id }); };
    const cb = row.querySelector('input');
    cb.checked = it.enabled;
    cb.onchange = () => send({ type: 'ext:toggle', id: it.id, enabled: cb.checked });
    box.appendChild(row);
  }
}

function renderRec(items) {
  const box = $('rec');
  box.innerHTML = '';
  for (const it of items) {
    const b = document.createElement('button');
    b.innerHTML = '<b></b><span></span>';
    b.querySelector('b').textContent = it.name;
    b.querySelector('span').textContent = it.desc;
    b.onclick = () => install(it.url);
    box.appendChild(b);
  }
}

if (wv) {
  wv.addEventListener('message', e => {
    const m = e.data;
    if (m.type === 'extList') { renderList(m.items); renderRec(m.recommended); }
    else if (m.type === 'extStatus') { status(m.text, m.ok); if (m.ok && /установлено|удалено/.test(m.text)) { toast(m.text); $('url').value = ''; } }
  });
  send({ type: 'ext:list' });
}
