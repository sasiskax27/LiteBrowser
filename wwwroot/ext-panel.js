if (window.top !== window) document.documentElement.innerHTML = '';

const $ = id => document.getElementById(id);
const wv = window.chrome && window.chrome.webview;
const send = m => wv && wv.postMessage(m);
const el = (tag, cls, text) => { const e = document.createElement(tag); if (cls) e.className = cls; if (text != null) e.textContent = text; return e; };

function hideMenu() { $('menu').classList.remove('show'); }
document.addEventListener('click', e => { if (!e.target.closest('.menu') && !e.target.closest('.more')) hideMenu(); });

function showMenu(btn, it) {
  const m = $('menu');
  m.innerHTML = '';
  const add = (icon, text, fn, cls) => {
    const b = el('button', cls || '');
    b.innerHTML = `<span class="icon">${icon}</span>`;
    b.appendChild(document.createTextNode(text));
    b.onclick = () => { hideMenu(); fn(); };
    m.appendChild(b);
  };
  if (it.popup) add('&#xE8A7;', 'Открыть окно расширения во вкладке', () => send({ type: 'ext:open', id: it.id, page: 'popup' }));
  if (it.options) add('&#xE713;', 'Настройки расширения', () => send({ type: 'ext:open', id: it.id, page: 'options' }));
  add(it.enabled ? '&#xE769;' : '&#xE768;', it.enabled ? 'Выключить' : 'Включить', () => send({ type: 'ext:toggle', id: it.id, enabled: !it.enabled, from: 'panel' }));
  add('&#xE74D;', 'Удалить', () => { if (confirm('Удалить расширение «' + it.name + '»?')) send({ type: 'ext:remove', id: it.id }); }, 'danger');
  m.classList.add('show');
  const r = btn.getBoundingClientRect();
  const w = m.offsetWidth, h = m.offsetHeight;
  m.style.left = Math.max(6, Math.min(innerWidth - w - 6, r.right - w)) + 'px';
  m.style.top = (r.bottom + h + 6 > innerHeight ? Math.max(6, r.top - h - 4) : r.bottom + 4) + 'px';
}

function render(items) {
  const list = $('list');
  list.innerHTML = '';
  if (!items.length) {
    list.innerHTML = '<div class="empty">Расширений пока нет.<br>Откройте страницу расширения в магазине — там появится кнопка «＋ Установить расширение».</div>';
    return;
  }
  for (const it of items) {
    const row = el('div', 'ext' + (it.enabled ? '' : ' off') + (it.popup && it.enabled ? ' can' : ''));
    const ico = el('div', 'ico');
    if (it.icon) { const img = el('img'); img.src = it.icon; img.alt = ''; ico.appendChild(img); }
    else ico.innerHTML = '<span class="icon">&#xEA86;</span>';
    const t = el('div', 't');
    t.append(el('b', null, it.name), el('span', null, it.enabled ? (it.popup ? 'Нажмите, чтобы открыть' : 'Работает на страницах') : 'Выключено'));
    if (it.popup && it.enabled) { t.title = 'Открыть окно расширения'; t.onclick = () => send({ type: 'ext:popup', id: it.id }); ico.style.cursor = 'pointer'; ico.onclick = t.onclick; }
    const sw = el('label', 'switch sm');
    sw.title = it.enabled ? 'Выключить' : 'Включить';
    const cb = el('input'); cb.type = 'checkbox'; cb.checked = it.enabled;
    cb.onchange = () => send({ type: 'ext:toggle', id: it.id, enabled: cb.checked });
    sw.append(cb, el('span', 'slider'));
    const more = el('button', 'ib more');
    more.innerHTML = '&#xE712;';
    more.title = 'Ещё';
    more.onclick = e => { e.stopPropagation(); showMenu(more, it); };
    row.append(ico, t, sw, more);
    list.appendChild(row);
  }
  const hint = el('div', 'hint', 'Значки расширений на панели WebView2 не показывает — поэтому они собраны здесь.');
  list.appendChild(hint);
}

$('manage').onclick = () => send({ type: 'ext:manage' });
$('store').onclick = () => send({ type: 'ext:store', store: 'chrome' });

if (wv) {
  wv.addEventListener('message', e => {
    const m = e.data;
    if (m.type === 'extList') render(m.items);
  });
  send({ type: 'ext:list' });
}
