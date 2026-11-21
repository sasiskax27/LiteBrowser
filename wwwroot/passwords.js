if (window.top !== window) document.documentElement.innerHTML = '';

const $ = id => document.getElementById(id);
const wv = window.chrome && window.chrome.webview;
const send = m => wv && wv.postMessage(m);
let state = null, items = [], filter = 'all', editing = null, revealed = {}, toastTimer = 0, importedFile = '', listLoaded = false;

function toast(text) {
  $('toastText').textContent = text;
  $('toast').classList.add('show');
  clearTimeout(toastTimer);
  toastTimer = setTimeout(() => $('toast').classList.remove('show'), 2000);
}
const show = (id, on) => $(id).classList.toggle('hidden', !on);

// ---------- Оценка надёжности пароля ----------
function strength(p) {
  if (!p) return { score: 0, label: '', color: 'transparent' };
  let pool = 0;
  if (/[a-zа-я]/.test(p)) pool += 26;
  if (/[A-ZА-Я]/.test(p)) pool += 26;
  if (/\d/.test(p)) pool += 10;
  if (/[^A-Za-zА-Яа-я0-9]/.test(p)) pool += 20;
  let bits = p.length * Math.log2(Math.max(pool, 2));
  if (/^(.)\1+$/.test(p) || /(password|qwerty|123456|пароль|admin)/i.test(p)) bits = Math.min(bits, 20);
  const levels = [
    [28, 'Очень слабый', 'var(--bad)'], [45, 'Слабый', 'var(--warn)'], [65, 'Средний', '#f5d76e'], [90, 'Надёжный', 'var(--ok)'], [Infinity, 'Очень надёжный', 'var(--ok)'],
  ];
  const i = levels.findIndex(l => bits < l[0]);
  return { score: Math.min(100, bits / 1.1), label: `${levels[i][1]} · ${Math.round(bits)} бит`, color: levels[i][2] };
}
function meter(input, fill, label) {
  const s = strength($(input).value);
  $(fill).style.width = s.score + '%';
  $(fill).style.background = s.color;
  $(label).innerHTML = s.label || '&nbsp;';
}

// ---------- Экран блокировки ----------
function renderLock() {
  const exists = state.exists;
  show('lockScreen', !state.unlocked);
  show('vault', state.unlocked);
  if (state.unlocked) return;
  $('lockTitle').textContent = exists ? 'Хранилище заблокировано' : 'Менеджер паролей';
  $('lockSub').textContent = exists
    ? 'Введите мастер-пароль, чтобы открыть сохранённые пароли.'
    : 'Создайте хранилище, защищённое мастер-паролем. Запомните его: это единственный ключ к вашим паролям.';
  show('unlockForm', exists);
  show('createForm', !exists);
  setTimeout(() => (exists ? $('unlockPass') : $('newPass')).focus(), 50);
}

$('unlockForm').onsubmit = e => {
  e.preventDefault();
  if (!$('unlockPass').value) return;
  $('unlockBtn').disabled = true; $('unlockBtn').textContent = 'Открываю…'; $('lockError').textContent = '';
  send({ type: 'pw:unlock', master: $('unlockPass').value });
};
$('newPass').oninput = () => meter('newPass', 'newMeter', 'newMeterLabel');
$('createForm').onsubmit = e => {
  e.preventDefault();
  const a = $('newPass').value, b = $('newPass2').value;
  if (a.length < 8) { $('lockError').textContent = 'Мастер-пароль должен быть не короче 8 символов'; return; }
  if (a !== b) { $('lockError').textContent = 'Пароли не совпадают'; return; }
  if (!$('agree').checked) { $('lockError').textContent = 'Отметьте, что вы понимаете: мастер-пароль нельзя восстановить'; return; }
  $('createBtn').disabled = true; $('createBtn').textContent = 'Создаю…'; $('lockError').textContent = '';
  send({ type: 'pw:create', master: a });
};

// ---------- Список ----------
const colors = ['#ff6b6b', '#f7a14a', '#f5c84c', '#5fd38d', '#3fc1c9', '#5b8def', '#8c6cf0', '#e46ccf'];
const colorOf = s => { let h = 0; for (const c of s) h = (h * 31 + c.charCodeAt(0)) >>> 0; return colors[h % colors.length]; };

function renderStats() {
  const total = items.length, weak = items.filter(i => i.weak).length, reused = items.filter(i => i.reused).length;
  const good = items.filter(i => !i.weak && !i.reused).length;
  $('statTotal').textContent = total; $('statGood').textContent = good; $('statWeak').textContent = weak; $('statReused').textContent = reused;
  const score = total ? Math.round(good / total * 100) : 100;
  $('scoreNum').textContent = total ? score : '—';
  $('scoreRing').setAttribute('stroke-dasharray', `${total ? score : 0} 100`);
  $('scoreRing').style.stroke = score >= 80 ? 'var(--ok)' : score >= 50 ? 'var(--warn)' : 'var(--bad)';
}

function renderList() {
  renderStats();
  const q = $('q').value.trim().toLowerCase();
  const list = $('list');
  list.innerHTML = '';
  const shown = items.filter(i =>
    (filter === 'all' || (filter === 'weak' && i.weak) || (filter === 'reused' && i.reused)) &&
    (!q || i.host.includes(q) || i.username.toLowerCase().includes(q)));
  show('empty', items.length === 0);
  show('list', shown.length > 0);
  for (const it of shown) {
    const row = document.createElement('div');
    row.className = 'item';
    row.innerHTML = `
      <div class="avatar"></div>
      <div class="info">
        <div class="host"><a target="_blank"></a></div>
        <div class="user"></div>
        <div class="pw mono hidden"></div>
      </div>
      <div class="actions">
        <button class="act icon" data-a="user" title="Скопировать логин">&#xE77B;</button>
        <button class="act icon" data-a="pass" title="Скопировать пароль">&#xE8C8;</button>
        <button class="act icon" data-a="eye" title="Показать пароль">&#xE7B3;</button>
        <button class="act icon" data-a="edit" title="Изменить">&#xE70F;</button>
        <button class="act icon del" data-a="del" title="Удалить">&#xE74D;</button>
      </div>`;
    const av = row.querySelector('.avatar');
    av.textContent = it.host[0] || '?';
    av.style.background = `linear-gradient(135deg, ${colorOf(it.host)}, ${colorOf(it.host + 'x')})`;
    const a = row.querySelector('.host a');
    a.textContent = it.host; a.href = it.origin;
    const host = row.querySelector('.host');
    if (it.weak) host.insertAdjacentHTML('beforeend', '<span class="badge weak">Слабый</span>');
    if (it.reused) host.insertAdjacentHTML('beforeend', '<span class="badge reused">Повторяется</span>');
    row.querySelector('.user').textContent = (it.username || '(без логина)') + ' · изменён ' + it.updated;
    const pw = row.querySelector('.pw');
    if (revealed[it.id]) { pw.textContent = revealed[it.id]; pw.classList.remove('hidden'); }
    row.querySelector('.actions').onclick = e => {
      const b = e.target.closest('button'); if (!b) return;
      const act = b.dataset.a;
      if (act === 'user') send({ type: 'pw:copy', id: it.id, field: 'user' });
      else if (act === 'pass') send({ type: 'pw:copy', id: it.id, field: 'pass' });
      else if (act === 'eye') {
        if (revealed[it.id]) { delete revealed[it.id]; renderList(); }
        else send({ type: 'pw:reveal', id: it.id });
      }
      else if (act === 'edit') openEdit(it);
      else if (act === 'del' && confirm(`Удалить пароль для ${it.host}?`)) send({ type: 'pw:delete', id: it.id });
    };
    list.appendChild(row);
  }
}

$('q').oninput = renderList;
document.querySelectorAll('#filters button').forEach(b => b.onclick = () => {
  filter = b.dataset.f;
  document.querySelectorAll('#filters button').forEach(x => x.classList.toggle('on', x === b));
  renderList();
});

// ---------- Редактирование ----------
let pendingEdit = null;
function openEdit(it) {
  editing = it || null;
  $('editTitle').textContent = it ? 'Изменить пароль' : 'Новый пароль';
  $('eUrl').value = it ? it.origin : '';
  $('eUser').value = it ? it.username : '';
  $('ePass').value = '';
  $('ePass').type = 'password';
  show('eDelete', !!it);
  meter('ePass', 'eMeter', 'eMeterLabel');
  show('editModal', true);
  if (it) { pendingEdit = it.id; send({ type: 'pw:reveal', id: it.id }); }
  setTimeout(() => (it ? $('ePass') : $('eUrl')).focus(), 50);
}
$('addBtn').onclick = () => openEdit(null);
$('eCancel').onclick = () => show('editModal', false);
$('eEye').onclick = () => { $('ePass').type = $('ePass').type === 'password' ? 'text' : 'password'; };
$('eGen').onclick = () => { genTarget = 'edit'; send({ type: 'pw:generate', length: 20, symbols: true }); };
$('ePass').oninput = () => meter('ePass', 'eMeter', 'eMeterLabel');
$('eSave').onclick = () => send({ type: 'pw:save', id: editing ? editing.id : '', url: $('eUrl').value, username: $('eUser').value, password: $('ePass').value });
$('eDelete').onclick = () => { if (editing && confirm('Удалить этот пароль?')) { send({ type: 'pw:delete', id: editing.id }); show('editModal', false); } };

// ---------- Генератор ----------
let genTarget = 'gen';
function generate() { genTarget = 'gen'; send({ type: 'pw:generate', length: +$('gLen').value, symbols: $('gSym').checked }); }
$('genBtn').onclick = () => { show('genModal', true); generate(); };
$('gLen').oninput = () => { $('gLenVal').textContent = $('gLen').value; generate(); };
$('gSym').onchange = generate;
$('gAgain').onclick = generate;
$('gClose').onclick = () => show('genModal', false);
$('gCopy').onclick = async () => {
  try { await navigator.clipboard.writeText($('gOut').textContent); toast('Пароль скопирован'); }
  catch (e) { toast('Не удалось скопировать'); }
};

// ---------- Ещё ----------
function renderNever() {
  const box = $('neverList');
  box.innerHTML = '';
  if (!state.never.length) { box.innerHTML = '<span style="padding-right:12px">Список пуст</span>'; return; }
  for (const h of state.never) {
    const s = document.createElement('span');
    s.textContent = h;
    const x = document.createElement('button');
    x.className = 'icon'; x.innerHTML = '&#xE711;'; x.title = 'Убрать';
    x.onclick = () => send({ type: 'pw:neverRemove', host: h });
    s.appendChild(x);
    box.appendChild(s);
  }
}
$('moreBtn').onclick = () => {
  $('autoLockVal').textContent = state.autoLock ? `через ${state.autoLock} мин бездействия` : 'только при закрытии браузера';
  renderNever();
  show('moreModal', true);
};
$('moreClose').onclick = () => show('moreModal', false);
$('importCsv').onclick = () => send({ type: 'pw:importCsv' });
$('exportCsv').onclick = () => send({ type: 'pw:exportCsv' });
$('emptyImport').onclick = () => $('moreBtn').onclick();
$('cmBtn').onclick = () => {
  if ($('cmNew').value !== $('cmNew2').value) { toast('Новые пароли не совпадают'); return; }
  send({ type: 'pw:changeMaster', old: $('cmOld').value, new: $('cmNew').value });
  ['cmOld', 'cmNew', 'cmNew2'].forEach(id => $(id).value = '');
};
$('importKeep').onclick = () => show('importModal', false);
$('importDelete').onclick = () => { send({ type: 'pw:deleteFile', file: importedFile }); show('importModal', false); };
$('lockBtn').onclick = () => { revealed = {}; send({ type: 'pw:lock' }); };

document.addEventListener('keydown', e => {
  if (e.key === 'Escape') ['editModal', 'genModal', 'moreModal', 'importModal'].forEach(id => show(id, false));
});
document.querySelectorAll('.modal').forEach(m => m.addEventListener('mousedown', e => { if (e.target === m) show(m.id, false); }));

// ---------- Сообщения от браузера ----------
if (wv) {
  wv.addEventListener('message', e => {
    const m = e.data;
    switch (m.type) {
      case 'pwState':
        const wasUnlocked = state && state.unlocked;
        state = m;
        if (!m.unlocked) { items = []; revealed = {}; listLoaded = false; }
        $('unlockBtn').disabled = false; $('unlockBtn').textContent = 'Открыть';
        $('createBtn').disabled = false; $('createBtn').textContent = 'Создать хранилище';
        renderLock();
        if (m.unlocked && !listLoaded) send({ type: 'pw:list' });
        if (!$('moreModal').classList.contains('hidden')) renderNever();
        if (wasUnlocked && !m.unlocked) ['editModal', 'genModal', 'moreModal'].forEach(id => show(id, false));
        break;
      case 'pwList':
        items = m.items; listLoaded = true;
        $('unlockPass').value = ''; $('newPass').value = ''; $('newPass2').value = '';
        renderList();
        show('editModal', false);
        break;
      case 'pwReveal':
        if (pendingEdit === m.id) { $('ePass').value = m.password; meter('ePass', 'eMeter', 'eMeterLabel'); pendingEdit = null; }
        else { revealed[m.id] = m.password; renderList(); }
        break;
      case 'pwGenerated':
        if (genTarget === 'edit') { $('ePass').value = m.password; $('ePass').type = 'text'; meter('ePass', 'eMeter', 'eMeterLabel'); }
        else { $('gOut').textContent = m.password; $('gOut').dataset.v = m.password; const s = strength(m.password); $('gMeter').style.width = s.score + '%'; $('gMeter').style.background = s.color; $('gMeterLabel').textContent = s.label; }
        break;
      case 'pwImported':
        importedFile = m.file;
        $('importTitle').textContent = m.count ? `Перенесено паролей: ${m.count}` : 'Новых паролей не найдено';
        show('moreModal', false);
        show('importModal', true);
        break;
      case 'pwExported':
        importedFile = m.file;
        $('importTitle').textContent = `Экспортировано паролей: ${m.count}. Импортируйте файл в Bitwarden, а потом удалите его`;
        show('moreModal', false);
        show('importModal', true);
        break;
      case 'pwError':
        $('unlockBtn').disabled = false; $('unlockBtn').textContent = 'Открыть';
        $('createBtn').disabled = false; $('createBtn').textContent = 'Создать хранилище';
        if (state && !state.unlocked) { $('lockError').textContent = m.text; $('unlockPass').select(); }
        else toast(m.text);
        break;
      case 'done': toast(m.text); break;
      case 'error': toast('Ошибка: ' + m.text); $('createBtn').disabled = false; $('createBtn').textContent = 'Создать хранилище'; if (state && !state.unlocked) $('lockError').textContent = m.text; break;
    }
  });
  send({ type: 'pw:state' });
  // Хранилище может заблокироваться само (таймер, Win+L) — проверяем, не продлевая блокировку
  setInterval(() => send({ type: 'pw:state' }), 15000);
  document.addEventListener('visibilitychange', () => { if (!document.hidden) send({ type: 'pw:state' }); });
}
