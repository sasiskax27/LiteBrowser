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
  if (document.activeElement !== $('focusSites')) $('focusSites').value = (state.focusSites || []).join('\n');
  renderProxy();

  renderAdblock();
  renderSites();
  renderLookAndSync();
  const bl = info.blocklist;
  $('blStatus').textContent = bl.updating ? 'Загружается список…'
    : bl.count > 0 ? `Загружено ${bl.count.toLocaleString('ru-RU')} доменов, обновлено ${bl.updated}` : 'Список ещё не загружен';

  $('darkFrom').value = state.darkFrom || '21:00'; $('darkTo').value = state.darkTo || '07:00';
  const fl = $('feedList'); fl.innerHTML = '';
  const feeds = state.feeds || [];
  if (!feeds.length) fl.innerHTML = '<span class="row-sub">Подписок пока нет</span>';
  for (const f of feeds) {
    let label = f; try { label = new URL(f).hostname.replace(/^www\./, ''); } catch (e) { }
    const b = document.createElement('button'); b.className = 'chip'; b.textContent = label + '  ✕'; b.title = f + ' — убрать';
    b.onclick = () => { state.feeds = feeds.filter(x => x !== f); save(); render(); };
    fl.appendChild(b);
  }
  // Сайты в своём окне: чипы с крестиком
  const wr = $('winRules'); wr.innerHTML = '';
  const rules = state.windowRules || [];
  if (!rules.length) wr.innerHTML = '<span class="row-sub">Пока нет</span>';
  for (const r of rules) {
    const b = document.createElement('button'); b.className = 'chip'; b.textContent = r + '  ✕'; b.title = 'Убрать';
    b.onclick = () => { state.windowRules = rules.filter(x => x !== r); save(); render(); };
    wr.appendChild(b);
  }

  $('about').innerHTML = '';
  for (const line of [
    'Версия LiteBrowser: ' + info.version,
    'Движок: Chromium (Microsoft WebView2) ' + info.browserVersion + '. Обновляется автоматически вместе с Windows',
    'Данные браузера хранятся только на этом компьютере',
    info.portable ? 'Портативный режим: все данные в папке ' + info.dataDir : 'Портативная версия: положите рядом с LiteBrowser.exe пустую папку «LiteBrowserData» (например, на флешке) — все данные будут храниться в ней',
    ...(info.startupMs > 0 ? ['Запуск до первой страницы: ' + (info.startupMs / 1000).toFixed(1) + ' с. Память по вкладкам — Shift+Esc'] : []),
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

// Родительский контроль и ИИ
function renderParental() {
  $('parState').textContent = state.parentalEnabled ? '✓ Включён' : 'Выключен';
  $('parSub').textContent = state.parentalEnabled ? 'Чтобы изменить или выключить — введите PIN-код' : 'Придумайте PIN-код из 4+ цифр и нажмите «Включить»';
  $('parAdult').checked = state.parentalAdult;
  if (document.activeElement !== $('parSites')) $('parSites').value = (state.parentalBlocked || []).join('\n');
  $('parOff').style.display = state.parentalEnabled ? '' : 'none';
}
function parentalSend(enabled) {
  wv.postMessage({ type: 'parental:set', enabled, pin: $('parPin').value, adult: $('parAdult').checked,
    blocked: $('parSites').value.split('\n').map(s => s.trim()).filter(Boolean) });
  $('parPin').value = '';
}
$('parOn').onclick = () => parentalSend(true);
$('parOff').onclick = () => parentalSend(false);
$('openAi').onclick = () => wv.postMessage({ type: 'openAi' });

// Оформление, синхронизация, обновления
function renderLookAndSync() {
  renderParental();
  const acc = $('accents');
  acc.innerHTML = '';
  for (const a of info.accents) {
    const b = document.createElement('button');
    b.style.background = a.color;
    b.title = a.name;
    // В теме «Неон» акцент по умолчанию — неоновый, хотя в настройках он записан как синий
    const cur = state.uiTheme === 'neon' && state.accent === 'blue' ? 'neon' : state.accent;
    b.className = cur === a.id ? 'on' : '';
    b.onclick = () => { state.accent = a.id; save(); render(); };
    acc.appendChild(b);
  }
  $('wallState').textContent = state.wallpaper ? (state.wallpaperVideo ? 'Установлено своё видео' : 'Установлена своя картинка') : 'Своя картинка вместо фона. Плитки сайтов меняются кнопкой «Изменить» на самой стартовой странице';
  $('removeWall').style.display = state.wallpaper ? '' : 'none';

  const sy = info.sync;
  $('syncFolder').textContent = state.syncFolder || 'Не выбрана';
  const clouds = $('syncClouds');
  clouds.innerHTML = '';
  for (const c of sy.clouds) {
    if (c.path === state.syncFolder) continue;
    const b = document.createElement('button');
    b.className = 'chip';
    b.textContent = '☁ ' + c.name;
    b.title = c.path;
    b.onclick = () => wv.postMessage({ type: 'sync:folder', path: c.path });
    clouds.appendChild(b);
  }
  $('syncPwState').textContent = sy.hasPassword ? '✓ Пароль задан и защищён Windows' : 'Задайте одинаковый пароль на всех компьютерах (от 8 символов)';
  $('syncStatus').textContent = sy.running ? 'Синхронизирую…' : sy.status || (state.syncLast ? 'Последняя синхронизация: ' + state.syncLast : 'При запуске и при закрытии браузера');
  $('updState').textContent = `Версия ${info.version}. При запуске браузер проверит GitHub и предложит обновиться в один клик`;
}
$('pickWall').onclick = () => wv.postMessage({ type: 'pickWallpaper' });
$('removeWall').onclick = () => wv.postMessage({ type: 'removeWallpaper' });
$('syncPick').onclick = () => wv.postMessage({ type: 'sync:pick' });
$('davSave').onclick = () => wv.postMessage({ type: 'sync:webdav', url: $('davUrl').value, user: $('davUser').value, pass: $('davPass').value });
$('syncPwSave').onclick = () => {
  const p = $('syncPw').value;
  if (p.length < 8) { toast('Пароль синхронизации — не короче 8 символов'); return; }
  wv.postMessage({ type: 'sync:password', password: p });
  $('syncPw').value = '';
};
$('syncNow').onclick = () => wv.postMessage({ type: 'sync:now' });
$('checkUpd').onclick = () => { toast('Проверяю…'); wv.postMessage({ type: 'checkUpdate' }); };

// Переключатели
document.querySelectorAll('input[data-key]').forEach(i => i.onchange = () => { state[i.dataset.key] = i.checked; save(); });
document.querySelectorAll('.seg[data-seg]').forEach(seg => seg.querySelectorAll('button').forEach(b => b.onclick = () => {
  const before = state[seg.dataset.seg];
  state[seg.dataset.seg] = seg.dataset.num ? Number(b.dataset.v) : b.dataset.v;
  render(); save();
  // Новый язык — перезагружаем страницу, чтобы она открылась на нём
  if (seg.dataset.seg === 'language' && before !== state.language) setTimeout(() => location.reload(), 500);
}));

// Прокси
$('pHost').oninput = () => { state.proxyHost = $('pHost').value.trim(); saveLater(); };
$('pPort').oninput = () => { const p = parseInt($('pPort').value, 10); if (p > 0 && p < 65536) { state.proxyPort = p; saveLater(); } };
$('sites').oninput = () => { state.proxySites = $('sites').value.split('\n').map(s => s.trim()).filter(Boolean); saveLater(); };
$('focusSites').oninput = () => { state.focusSites = $('focusSites').value.split('\n').map(s => s.trim().toLowerCase().replace(/^https?:\/\//, '').replace(/\/.*$/, '')).filter(Boolean); saveLater(); };
// ---- Пароль на браузер ----
function renderLock(on) {
  $('lockDot').classList.toggle('on', on);
  $('lockState').textContent = on ? 'Включён' : 'Выключен';
  $('lockOld').classList.toggle('hidden', !on);
  $('lockSet').textContent = on ? 'Сменить PIN' : 'Включить';
  $('lockClear').classList.toggle('hidden', !on);
  $('helloRow').classList.toggle('hidden', !on);
  $('helloBtn').textContent = state.lockHello ? 'Выключить' : 'Включить';
}
$('helloBtn').onclick = () => wv.postMessage({ type: 'lock:hello', on: !state.lockHello });
$('lockSet').onclick = () => wv.postMessage({ type: 'lock:set', pin: $('lockNew').value, old: $('lockOld').value });
$('lockClear').onclick = () => wv.postMessage({ type: 'lock:clear', old: $('lockOld').value });

// ---- Встроенный Tor ----
let torBridges = '', torTimer = 0;
function renderTor(t) {
  torBridges = t.bridges || torBridges;
  document.querySelectorAll('[data-tb]').forEach(b => b.style.borderColor = b.dataset.tb === torBridges ? 'var(--accent)' : '');
  $('torDot').classList.toggle('on', t.running && t.progress >= 100);
  $('torState').textContent = t.busy || (t.running && t.progress < 100) ? (t.status || 'Подключаюсь…')
    : t.running ? (t.restartNeeded ? 'Tor подключён — перезапустите браузер, чтобы пустить трафик через него' : 'Работает: весь трафик идёт через Tor')
    : t.enabled ? (t.status || 'Запускаю…') : (t.status && t.status !== 'Tor выключен' ? t.status : 'Выключен');
  $('torOn').classList.toggle('hidden', t.enabled);
  $('torOff').classList.toggle('hidden', !t.enabled);
  $('restartBanner').classList.toggle('show', !!t.restartNeeded);
  clearTimeout(torTimer);
  if (t.busy || (t.enabled && t.progress < 100)) torTimer = setTimeout(() => wv.postMessage({ type: 'tor:status' }), 1000);
}
document.querySelectorAll('[data-tb]').forEach(b => b.onclick = () => { torBridges = b.dataset.tb; renderTor({ bridges: torBridges }); });
$('torOn').onclick = () => { $('torState').textContent = 'Скачиваю и запускаю Tor…'; wv.postMessage({ type: 'tor:enable', bridges: torBridges }); };
$('torOff').onclick = () => wv.postMessage({ type: 'tor:disable' });
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
    if (m.type === 'settings') { state = m.settings; info = m; render(); renderLock(!!state.lockPinHash); }
    else if (m.type === 'lockResult') { toast(m.text); if (m.hello !== undefined) state.lockHello = m.hello; if (m.ok) { $('lockOld').value = ''; $('lockNew').value = ''; if (m.enabled === false) state.lockHello = false; renderLock(m.enabled); } }
    else if (m.type === 'torStatus') renderTor(m);
    else if (m.type === 'torError') { $('torState').textContent = 'Ошибка: ' + m.text; }
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
  wv.postMessage({ type: 'tor:status' });
  wv.postMessage({ type: 'importScan' });
}


// ===================== Боковое меню, разделы и поиск =====================
const NAV = [
  ['Основные', [
    ['search', '&#xE721;', 'Поиск', 'Поисковая система и подсказки'],
    ['look', '&#xE790;', 'Внешний вид', 'Тема, цвет, язык, обои и масштаб'],
    ['tabs', '&#xE737;', 'Вкладки', 'Восстановление вкладок и экономия памяти'],
    ['bookmarks', '&#xE734;', 'Закладки и история', 'Панель закладок, история и загрузки'],
  ]],
  ['Приватность и защита', [
    ['security', '&#xEA18;', 'Безопасность', 'HTTPS и защита от опасных сайтов'],
    ['adblock', '&#xE8F8;', 'Блокировка рекламы', 'Фильтры, YouTube и всплывающие окна'],
    ['passwords', '&#xE8D7;', 'Пароли', 'Менеджер паролей и автоблокировка'],
    ['sites', '&#xE72E;', 'Сайты и разрешения', 'Доверенные и ненадёжные сайты'],
    ['parental', '&#xE716;', 'Родительский контроль', 'Безопасный интернет для детей'],
    ['proxy', '&#xE774;', 'Обход блокировок', 'Прокси, VPN-клиенты и Tor'],
  ]],
  ['Возможности', [
    ['ai', '&#xE99A;', 'Lite AI', 'Встроенный ИИ-помощник'],
    ['tools', '&#xE90F;', 'Инструменты', 'Перевод, жесты мышью, экономия трафика'],
    ['@extensions.html', '&#xEA86;', 'Расширения', ''],
  ]],
  ['Система', [
    ['import', '&#xE8DE;', 'Перенос', 'Закладки и история из других браузеров'],
    ['sync', '&#xE895;', 'Синхронизация', 'Между компьютерами через облачный диск'],
    ['updates', '&#xE896;', 'Обновления', 'Проверка новых версий'],
    ['system', '&#xE7F8;', 'Система', 'Ярлыки и браузер по умолчанию'],
    ['data', '&#xE74D;', 'Данные', 'Очистка истории, cookies и кэша'],
    ['about-sec', '&#xE946;', 'О браузере', 'Версия и движок'],
  ]],
];
const NAV_FLAT = NAV.flatMap(g => g[1]).filter(i => !i[0].startsWith('@'));

function buildNav() {
  const list = $('navList');
  for (const [group, items] of NAV) {
    const g = document.createElement('div');
    g.className = 'navgroup';
    g.textContent = group;
    list.appendChild(g);
    for (const [id, icon, title] of items) {
      const a = document.createElement(id.startsWith('@') ? 'a' : 'button');
      a.className = 'navitem';
      a.innerHTML = `<span class="icon">${icon}</span><span></span>`;
      a.querySelector('span:last-child').textContent = title;
      if (id.startsWith('@')) { a.href = id.slice(1); a.insertAdjacentHTML('beforeend', '<span class="ext icon">&#xE8A7;</span>'); }
      else { a.dataset.sec = id; a.onclick = () => { $('settingsFind').value = ''; runSearch(); location.hash = id; }; }
      list.appendChild(a);
    }
  }
}

function showSection(id) {
  const item = NAV_FLAT.find(i => i[0] === id) || NAV_FLAT[0];
  document.querySelectorAll('main > section').forEach(s => s.classList.toggle('active', s.id === item[0]));
  document.querySelectorAll('.navitem[data-sec]').forEach(a => a.classList.toggle('on', a.dataset.sec === item[0]));
  $('secIcon').innerHTML = item[1];
  $('secTitle').textContent = item[2];
  $('secSub').textContent = item[3];
  const on = document.querySelector('.navitem.on');
  if (on) on.scrollIntoView({ block: 'nearest', inline: 'nearest' });
  window.scrollTo(0, 0);
}

// Подсветка найденного текста
function unmark(root) {
  root.querySelectorAll('mark').forEach(m => m.replaceWith(document.createTextNode(m.textContent)));
  root.normalize();
}
function markText(el, q) {
  const w = document.createTreeWalker(el, NodeFilter.SHOW_TEXT);
  const hits = [];
  let n;
  while ((n = w.nextNode())) { const i = n.nodeValue.toLowerCase().indexOf(q); if (i >= 0) hits.push([n, i]); }
  for (const [node, i] of hits) {
    const r = document.createRange();
    r.setStart(node, i); r.setEnd(node, i + q.length);
    const m = document.createElement('mark');
    try { r.surroundContents(m); } catch (e) { }
  }
}

function runSearch() {
  const q = $('settingsFind').value.trim().toLowerCase();
  const main = document.querySelector('main');
  unmark(main);
  document.body.classList.toggle('searching', q.length > 0);
  if (!q) { document.body.classList.remove('nothing'); showSection(location.hash.slice(1)); return; }
  let any = false;
  document.querySelectorAll('main > section').forEach(sec => {
    const head = (sec.querySelector('h2') || {}).textContent || '';
    const secHit = head.toLowerCase().includes(q);
    let rowsHit = 0;
    sec.querySelectorAll('.row').forEach(r => {
      const hit = secHit || r.textContent.toLowerCase().includes(q);
      r.classList.toggle('nomatch', !hit);
      if (hit) { rowsHit++; markText(r, q); }
    });
    const hit = secHit || rowsHit > 0 || sec.textContent.toLowerCase().includes(q);
    sec.classList.toggle('nomatch', !hit);
    if (hit) any = true;
  });
  document.body.classList.toggle('nothing', !any);
}

buildNav();
$('settingsFind').addEventListener('input', runSearch);
$('settingsFind').addEventListener('keydown', e => { if (e.key === 'Escape') { e.target.value = ''; runSearch(); } });
window.addEventListener('hashchange', () => { if (!document.body.classList.contains('searching')) showSection(location.hash.slice(1)); });
showSection(location.hash.slice(1));
$('darkFrom').onchange = () => { state.darkFrom = $('darkFrom').value; save(); };
$('darkTo').onchange = () => { state.darkTo = $('darkTo').value; save(); };
