if (window.top !== window) document.documentElement.innerHTML = '';

const $ = id => document.getElementById(id);
const wv = window.chrome && window.chrome.webview;
let state = null, info = null, toastTimer = 0, saveTimer = 0;

function toast(text) {
  $('toastText').textContent = text;
  $('toast').classList.add('show');
  clearTimeout(toastTimer);
  toastTimer = setTimeout(() => $('toast').classList.remove('show'), 1800);
}

function save() { clearTimeout(saveTimer); wv.postMessage({ type: 'save', settings: state }); }
function saveLater() { clearTimeout(saveTimer); saveTimer = setTimeout(save, 600); }

function renderEngines() {
  const box = $('engines');
  box.innerHTML = '';
  for (const en of info.engines) {
    const el = document.createElement('button');
    el.className = 'engine' + (en.id === state.engine ? ' on' : '');
    el.innerHTML = '<div class="en-name"></div><div class="en-desc"></div>' +
      `<span class="tag ${en.private ? 'ok' : 'warn'}">${en.private ? 'Приватный' : 'Собирает данные'}</span>` +
      '<span class="check icon">&#xE73E;</span>';
    el.querySelector('.en-name').textContent = en.name;
    el.querySelector('.en-desc').textContent = en.desc;
    el.onclick = () => { if (state.engine === en.id) return; state.engine = en.id; renderEngines(); save(); };
    box.appendChild(el);
  }
}

function renderProxy() {
  const on = state.proxyMode !== 'off';
  $('proxyExtra').classList.toggle('hidden', !on);
  $('sitesBox').classList.toggle('hidden', state.proxyMode !== 'list');
  $('restartBanner').classList.toggle('show', !!info.restartNeeded);
  $('proxyDot').classList.toggle('on', info.proxyRunning && !info.restartNeeded);
  $('proxyState').textContent = info.restartNeeded ? 'Нужен перезапуск' : info.proxyRunning ? 'Работает' : 'Выключено';
}

function render() {
  renderEngines();
  document.querySelectorAll('input[data-key]').forEach(i => i.checked = !!state[i.dataset.key]);
  document.querySelectorAll('.seg[data-seg]').forEach(seg => {
    const v = String(state[seg.dataset.seg]);
    seg.querySelectorAll('button').forEach(b => b.classList.toggle('on', b.dataset.v === v));
  });
  if (document.activeElement !== $('pHost')) $('pHost').value = state.proxyHost;
  if (document.activeElement !== $('pPort')) $('pPort').value = state.proxyPort;
  if (document.activeElement !== $('sites')) $('sites').value = state.proxySites.join('\n');
  renderProxy();

  renderAdblock();
  renderSites();
  const bl = info.blocklist;
  $('blStatus').textContent = bl.updating ? 'Загружается список…'
    : bl.count > 0 ? `Загружено ${bl.count.toLocaleString('ru-RU')} доменов, обновлено ${bl.updated}` : 'Список ещё не загружен';

  $('about').innerHTML = '';
  for (const line of [
    'Версия LiteBrowser: ' + info.version,
    'Движок: Chromium (Microsoft WebView2) ' + info.browserVersion + '. Обновляется автоматически вместе с Windows',
    'Данные браузера хранятся только на этом компьютере',
  ]) { const d = document.createElement('div'); d.textContent = line; $('about').appendChild(d); }
}

// Блокировка рекламы
let abPoll = 0;
const listKey = { easylist: 'listEasyList', easyprivacy: 'listEasyPrivacy', ruadlist: 'listRuAdList', cookies: 'listCookies' };
function renderAdblock() {
  const ab = info.adblock;
  $('abTotal').textContent = `Всего заблокировано: ${ab.total.toLocaleString('ru-RU')}. Активных правил: ${ab.rules.toLocaleString('ru-RU')}.`;
  const box = $('abLists');
  box.innerHTML = '';
  for (const l of ab.lists) {
    const key = listKey[l.id];
    const row = document.createElement('div');
    row.className = 'row';
    const status = ab.updating ? 'Обновляется…'
      : l.error ? 'Ошибка загрузки: ' + l.error
      : state[key] && l.rules ? `${l.rules.toLocaleString('ru-RU')} правил · обновлён ${l.updated}`
      : state[key] ? 'Загружается…' : 'Выключен';
    row.innerHTML = '<div><div class="row-title"></div><div class="row-sub"></div></div><label class="switch"><input type="checkbox"><span class="slider"></span></label>';
    row.querySelector('.row-title').textContent = l.name;
    row.querySelector('.row-sub').textContent = l.desc + ' — ' + status;
    if (l.error) row.querySelector('.row-sub').style.color = 'var(--bad)';
    const cb = row.querySelector('input');
    cb.checked = !!state[key];
    cb.onchange = () => { state[key] = cb.checked; save(); };
    box.appendChild(row);
  }
  // Пока списки скачиваются или собираются — обновляем страницу сами
  const loading = ab.updating || ab.lists.some(l => state[listKey[l.id]] && !l.rules && !l.error);
  clearTimeout(abPoll);
  if (loading) abPoll = setTimeout(() => wv.postMessage({ type: 'get' }), 1500);
  const allow = $('abAllow');
  allow.innerHTML = '';
  $('abAllowEmpty').textContent = state.adblockAllow.length ? 'Здесь реклама не блокируется' : 'Список пуст. Добавить сайт можно кнопкой-щитом в адресной строке';
  for (const h of state.adblockAllow) {
    const c = document.createElement('button');
    c.className = 'chip';
    c.textContent = h + '  ✕';
    c.title = 'Снова блокировать рекламу на этом сайте';
    c.onclick = () => { state.adblockAllow = state.adblockAllow.filter(x => x !== h); save(); render(); };
    allow.appendChild(c);
  }
}
$('abUpdate').onclick = () => { toast('Обновляю списки…'); wv.postMessage({ type: 'updateBlocklist' }); };

// Сайты: доверие и разрешения
const permNames = { Camera: 'Камера', Microphone: 'Микрофон', Geolocation: 'Местоположение', ClipboardRead: 'Буфер обмена',
                    MultipleAutomaticDownloads: 'Несколько загрузок', Notifications: 'Уведомления' };
function chipList(boxId, key) {
  const box = $(boxId);
  box.innerHTML = '';
  if (!state[key].length) { box.innerHTML = '<span class="row-sub">Нет</span>'; return; }
  for (const h of state[key]) {
    const c = document.createElement('button');
    c.className = 'chip';
    c.textContent = h + '  ✕';
    c.title = 'Сделать обычным';
    c.onclick = () => { state[key] = state[key].filter(x => x !== h); save(); render(); };
    box.appendChild(c);
  }
}
function renderSites() {
  chipList('trustedList', 'trustedSites');
  chipList('untrustedList', 'untrustedSites');
  const box = $('permList');
  box.innerHTML = '';
  const sites = Object.keys(state.sitePermissions || {});
  if (!sites.length) { box.textContent = 'Сайты ещё ничего не запрашивали'; return; }
  for (const s of sites) {
    for (const [k, v] of Object.entries(state.sitePermissions[s])) {
      const c = document.createElement('button');
      c.className = 'chip';
      c.style.margin = '0 6px 6px 0';
      c.textContent = `${s} · ${permNames[k] || k}: ${v === 'allow' ? 'разрешено' : 'запрещено'}  ✕`;
      c.title = 'Забыть решение — сайт спросит снова';
      c.onclick = () => { delete state.sitePermissions[s][k]; if (!Object.keys(state.sitePermissions[s]).length) delete state.sitePermissions[s]; save(); render(); };
      box.appendChild(c);
    }
  }
}

// Переключатели
document.querySelectorAll('input[data-key]').forEach(i => i.onchange = () => { state[i.dataset.key] = i.checked; save(); });
document.querySelectorAll('.seg[data-seg]').forEach(seg => seg.querySelectorAll('button').forEach(b => b.onclick = () => {
  state[seg.dataset.seg] = seg.dataset.num ? Number(b.dataset.v) : b.dataset.v;
  render(); save();
}));

// Прокси
$('pHost').oninput = () => { state.proxyHost = $('pHost').value.trim(); saveLater(); };
$('pPort').oninput = () => { const p = parseInt($('pPort').value, 10); if (p > 0 && p < 65536) { state.proxyPort = p; saveLater(); } };
$('sites').oninput = () => { state.proxySites = $('sites').value.split('\n').map(s => s.trim()).filter(Boolean); saveLater(); };
$('resetSites').onclick = () => { state.proxySites = info.defaultSites.slice(); $('sites').value = state.proxySites.join('\n'); save(); };
document.querySelectorAll('.chip').forEach(c => c.onclick = () => {
  state.proxyHost = '127.0.0.1'; state.proxyPort = Number(c.dataset.port); state.proxyType = 'socks5';
  if (state.proxyMode === 'off') state.proxyMode = 'list';
  $('pHost').value = state.proxyHost; $('pPort').value = state.proxyPort;
  render(); save();
});
$('test').onclick = () => {
  $('testResult').className = 'test'; $('testResult').textContent = 'Проверяю…';
  wv.postMessage({ type: 'testProxy', host: $('pHost').value.trim(), port: parseInt($('pPort').value, 10) });
};
$('restart').onclick = () => wv.postMessage({ type: 'restart' });

// Перенос из других браузеров
function renderImport(items) {
  const box = $('importList');
  box.innerHTML = '';
  if (!items.length) {
    box.innerHTML = '<div class="row-sub">Другие браузеры не найдены. Можно загрузить закладки из HTML-файла.</div>';
    return;
  }
  for (const it of items) {
    const row = document.createElement('div');
    row.className = 'row';
    row.innerHTML = '<div><div class="row-title"></div><div class="row-sub"></div><div class="imp-result"></div></div>' +
      '<div class="actions"><label class="chk"><input type="checkbox" class="b" checked>Закладки</label>' +
      '<label class="chk"><input type="checkbox" class="h" checked>История</label>' +
      '<button class="btn primary go">Перенести</button></div>';
    row.querySelector('.row-title').textContent = it.browser;
    row.querySelector('.row-sub').textContent = 'Профиль: ' + it.profile;
    const b = row.querySelector('.b'), h = row.querySelector('.h');
    if (!it.bookmarks) { b.checked = false; b.disabled = true; }
    if (!it.history) { h.checked = false; h.disabled = true; }
    row.querySelector('.go').onclick = () => {
      if (!b.checked && !h.checked) return;
      const res = row.querySelector('.imp-result');
      res.className = 'imp-result'; res.textContent = 'Переношу…';
      currentImportRow = row;
      wv.postMessage({ type: 'importRun', id: it.id, bookmarks: b.checked, history: h.checked });
    };
    box.appendChild(row);
  }
}
let currentImportRow = null;
$('importHtml').onclick = () => { currentImportRow = null; wv.postMessage({ type: 'importHtml' }); };
$('exportHtml').onclick = () => wv.postMessage({ type: 'exportHtml' });

// Остальное
$('blUpdate').onclick = () => wv.postMessage({ type: 'updateBlocklist' });
$('shortcuts').onclick = () => wv.postMessage({ type: 'shortcuts' });
$('defaultBrowser').onclick = () => wv.postMessage({ type: 'defaultBrowser' });
$('clear').onclick = () => {
  if (confirm('Удалить историю, cookies, кэш и данные сайтов?\nВы выйдете из всех аккаунтов.')) wv.postMessage({ type: 'clear' });
};

if (wv) {
  wv.addEventListener('message', e => {
    const m = e.data;
    if (m.type === 'settings') { state = m.settings; info = m; render(); }
    else if (m.type === 'saved') { if (info) { info.restartNeeded = m.restartNeeded; renderProxy(); } toast('Сохранено'); }
    else if (m.type === 'proxyTest') {
      $('testResult').className = 'test ' + (m.ok ? 'ok' : 'bad');
      $('testResult').textContent = m.ok ? `✓ Прокси отвечает (${m.ms} мс)` : '✕ Нет ответа. Запущен ли VPN-клиент и верен ли порт?';
    }
    else if (m.type === 'importList') renderImport(m.items);
    else if (m.type === 'importResult') {
      const res = currentImportRow && currentImportRow.querySelector('.imp-result');
      if (res) { res.className = 'imp-result ' + (m.ok ? 'ok' : 'bad'); res.textContent = (m.ok ? '✓ ' : '✕ ') + m.text; }
      toast(m.text);
    }
    else if (m.type === 'done') toast(m.text);
    else if (m.type === 'error') toast('Ошибка: ' + m.text);
  });
  wv.postMessage({ type: 'get' });
  wv.postMessage({ type: 'importScan' });
}
