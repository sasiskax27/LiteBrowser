if (window.top !== window) document.documentElement.innerHTML = '';

const $ = id => document.getElementById(id);
const wv = window.chrome && window.chrome.webview;
const send = m => wv && wv.postMessage(m);
const fmt = n => Number(n || 0).toLocaleString('ru-RU');

const PERM = {
  Camera: 'Камера', Microphone: 'Микрофон', Geolocation: 'Местоположение', Notifications: 'Уведомления',
  ClipboardRead: 'Буфер обмена', MultipleAutomaticDownloads: 'Несколько загрузок', OtherSensors: 'Датчики',
  FileReadWrite: 'Файлы', Autoplay: 'Автовоспроизведение', LocalFonts: 'Шрифты', MidiSystemExclusiveMessages: 'MIDI', WindowManagement: 'Окна',
};
const GLYPH = { ok: '&#xE73E;', warn: '!', bad: '&#xE711;', info: 'i' };

function el(tag, cls, text) { const e = document.createElement(tag); if (cls) e.className = cls; if (text != null) e.textContent = text; return e; }

function bars(host, rows, empty) {
  host.innerHTML = '';
  if (!rows.length) { host.appendChild(el('div', 'empty2', empty)); return; }
  const max = Math.max(...rows.map(r => r.n), 1);
  for (const r of rows) {
    const row = el('div', 'barrow');
    const name = el('div', 'name', r.name);
    name.title = r.name;
    if (r.owner) name.appendChild(el('span', 'owner', r.owner));
    const track = el('div', 'track'); const fill = el('div'); fill.style.width = Math.max(3, r.n / max * 100) + '%'; track.appendChild(fill);
    row.append(name, track, el('div', 'n', fmt(r.n)));
    host.appendChild(row);
  }
}

function render(m) {
  $('busy').textContent = '';
  const s = m.score;
  $('score').textContent = s;
  const arc = $('arc');
  arc.style.strokeDashoffset = 326.7 * (1 - s / 100);
  arc.style.stroke = s >= 85 ? 'var(--ok)' : s >= 60 ? 'var(--warn)' : 'var(--bad)';
  const bad = m.items.filter(i => i.level === 'bad').length, warn = m.items.filter(i => i.level === 'warn').length;
  $('verdict').textContent = bad ? 'Есть серьёзные проблемы' : warn ? 'Хорошо, но можно лучше' : 'Отличная защита';
  $('verdictText').textContent = bad + warn
    ? `Важных проблем: ${bad}, советов: ${warn}. Каждую можно исправить одной кнопкой.`
    : 'Все основные функции защиты включены.';

  $('since').textContent = 'с ' + m.since;
  const st = m.stats, stats = $('stats');
  stats.innerHTML = '';
  for (const [n, label] of [
    [st.blocked, 'рекламы и трекеров'], [st.trackers, 'разных адресов слежки'], [st.popups, 'всплывающих окон'],
    [st.https, 'переходов на HTTPS'], [st.dangerous, 'опасных сайтов'], [st.denied, 'запросов разрешений отклонено'],
    ...(st.parental ? [[st.parental, 'заблокировано родительским контролем']] : []), [st.total, 'заблокировано за всё время'],
  ]) {
    const c = el('div', 'stat'); c.append(el('b', null, fmt(n)), el('span', null, label)); stats.appendChild(c);
  }

  const audit = $('audit');
  audit.innerHTML = '';
  for (const it of m.items) {
    const row = el('div', 'ai ' + it.level);
    const dot = el('div', 'dot icon'); dot.innerHTML = GLYPH[it.level] || 'i';
    if (it.level === 'warn' || it.level === 'info') dot.style.fontFamily = 'inherit', dot.style.fontWeight = '700';
    const t = el('div', 't'); t.append(el('b', null, it.title), el('div', null, it.text));
    row.append(dot, t);
    if (it.action) {
      const b = el('button', 'btn' + (it.level === 'bad' ? ' primary' : ''), it.label || 'Исправить');
      b.onclick = () => {
        if (it.action.startsWith('open:')) { location.href = it.action.slice(5); return; }
        b.disabled = true;
        send({ type: 'sec:fix', action: it.action });
      };
      row.appendChild(b);
    }
    audit.appendChild(row);
  }

  bars($('owners'), m.owners.map(o => ({ name: o.owner, n: o.count })), 'Пока никто — откройте пару сайтов');
  bars($('hosts'), m.topHosts.map(h => ({ name: h.host, n: h.count, owner: h.owner })), 'Пока ничего не заблокировано');
  bars($('tabs'), m.tabs.map(t => ({ name: t.title || t.host, n: t.blocked, owner: t.host })), 'Нет открытых сайтов');

  const perms = $('perms');
  perms.innerHTML = '';
  if (!m.permissions.length) perms.appendChild(el('div', 'empty2', 'Вы пока не давали и не запрещали разрешений'));
  for (const p of m.permissions) {
    const row = el('div', 'perm');
    row.append(el('div', 'grow', p.site), el('span', null, PERM[p.kind] || p.kind),
      el('span', 'tag ' + (p.value === 'allow' ? 'allow' : 'deny'), p.value === 'allow' ? 'разрешено' : 'запрещено'));
    const b = el('button', 'btn', 'Забыть');
    b.title = 'Сайт спросит снова';
    b.onclick = () => send({ type: 'sec:revoke', site: p.site, kind: p.kind });
    row.appendChild(b);
    perms.appendChild(row);
  }
}

$('refresh').onclick = () => send({ type: 'sec:report' });
$('reset').onclick = () => send({ type: 'sec:resetStats' });

if (wv) {
  wv.addEventListener('message', e => {
    const m = e.data;
    if (m.type === 'secReport') render(m);
    else if (m.type === 'secBusy') $('busy').textContent = m.text;
  });
  send({ type: 'sec:report' });
  setInterval(() => { if (!document.hidden) send({ type: 'sec:report' }); }, 5000);
}
