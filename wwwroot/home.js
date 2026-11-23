if (window.top !== window) document.documentElement.innerHTML = '';

const $ = id => document.getElementById(id);
const pad = n => String(n).padStart(2, '0');

function tick() {
  const d = new Date();
  $('time').textContent = pad(d.getHours()) + ':' + pad(d.getMinutes());
  const s = d.toLocaleDateString('ru-RU', { weekday: 'long', day: 'numeric', month: 'long' });
  $('date').textContent = s[0].toUpperCase() + s.slice(1);
  const h = d.getHours();
  $('hello').textContent = h < 5 ? 'Доброй ночи' : h < 12 ? 'Доброе утро' : h < 18 ? 'Добрый день' : 'Добрый вечер';
}
tick();
setInterval(tick, 1000);
if (new URLSearchParams(location.search).has('private')) $('private').classList.add('show');
if (new URLSearchParams(location.search).has('guest')) {
  // В гостевом окне ничего не сохраняется — прячем правку плиток и заметку
  $('guest').classList.add('show');
  $('editTiles').style.display = 'none';
  $('wgNote').closest('.wg').style.display = 'none';
}

const item = (name, on) => `<span class="${on ? '' : 'off'}">${name}: <b>${on ? 'вкл' : 'выкл'}</b></span>`;

const wv = window.chrome && window.chrome.webview;
let tiles = [], editing = false;
const colors = ['#ff5a52', '#7d8597', '#4ba3ff', '#4fc3f7', '#4a4f5c', '#ff7a6b', '#5fd38d', '#b07cff', '#f5c84c', '#ff6fb5'];
const colorOf = s => { let h = 0; for (const c of s) h = (h * 31 + c.charCodeAt(0)) >>> 0; return colors[h % colors.length]; };

function renderTiles() {
  const box = $('tiles');
  box.innerHTML = '';
  box.parentElement.classList.toggle('editing', editing);
  document.body.classList.toggle('editing', editing);
  tiles.forEach((t, i) => {
    const a = document.createElement('a');
    a.className = 'tile';
    a.href = t.url;
    a.innerHTML = '<span class="badge"></span><span class="nm"></span><button class="rm icon" title="Удалить">&#xE711;</button>';
    const b = a.querySelector('.badge');
    b.textContent = (t.name.match(/[\p{L}\p{N}]/u) || ['?'])[0].toUpperCase();
    const c = colorOf(t.name);
    b.style.background = `linear-gradient(135deg, ${c}, ${c}cc)`;
    a.querySelector('.nm').textContent = t.name;
    a.onclick = e => {
      if (!editing) return;
      e.preventDefault();
      const name = prompt('Название', t.name); if (name === null) return;
      const url = prompt('Адрес сайта', t.url); if (url === null) return;
      if (name.trim() && url.trim()) { tiles[i] = { name: name.trim(), url: url.trim() }; save(); }
    };
    a.querySelector('.rm').onclick = e => { e.preventDefault(); e.stopPropagation(); tiles.splice(i, 1); save(); };
    box.appendChild(a);
  });
  if (editing && tiles.length < 24) {
    const add = document.createElement('a');
    add.className = 'tile add';
    add.href = '#';
    add.innerHTML = '<span class="badge">+</span>Добавить';
    add.onclick = e => {
      e.preventDefault();
      const url = prompt('Адрес сайта (например, ozon.ru)'); if (!url || !url.trim()) return;
      let host = url.trim(); try { host = new URL(url.includes('://') ? url : 'https://' + url).hostname.replace(/^www\./, ''); } catch (x) {}
      const name = prompt('Название', host.split('.')[0].replace(/^./, c => c.toUpperCase())); if (!name || !name.trim()) return;
      tiles.push({ name: name.trim(), url: url.trim() }); save();
    };
    box.appendChild(add);
  }
  $('editTiles').innerHTML = editing ? '<span class="icon">&#xE73E;</span> Готово' : '<span class="icon">&#xE70F;</span> Изменить';
}
// ---- Виджеты: погода, курс валют, заметка ----
function cityForm(box, value) {
  box.innerHTML = '<div>Погода</div><input id="cityIn" placeholder="Ваш город, например Москва" maxlength="60">';
  const inp = box.querySelector('input');
  inp.value = value || '';
  inp.onkeydown = e => { if (e.key === 'Enter' && inp.value.trim()) { box.innerHTML = '<div class="dim">Загружаю…</div>'; wv.postMessage({ type: 'home:city', city: inp.value.trim() }); } };
  setTimeout(() => inp.focus(), 0);
}
function renderWidgets(m) {
  $('widgets').classList.toggle('show', !!m.enabled);
  if (!m.enabled) return;
  const w = $('wgWeather'), wd = m.weather;
  if (!m.city) {
    w.innerHTML = '<div class="big">🌤</div><div class="dim">Погода в вашем городе</div><a id="setCity">Указать город</a>';
    w.querySelector('#setCity').onclick = () => cityForm(w, '');
  } else if (!wd || wd.error) {
    w.innerHTML = '<div class="dim"></div><a id="setCity">Изменить город</a>';
    w.querySelector('.dim').textContent = (wd && wd.error) || 'Нет данных о погоде';
    w.querySelector('#setCity').onclick = () => cityForm(w, m.city);
  } else {
    w.innerHTML = `<div class="big">${wd.icon} ${wd.temp > 0 ? '+' : ''}${wd.temp}°</div><div class="dim"></div><div class="dim"></div><a class="edit icon" title="Изменить город">&#xE70F;</a>`;
    const dims = w.querySelectorAll('.dim');
    dims[0].textContent = wd.city + ' · ' + wd.text;
    dims[1].textContent = `ощущается ${wd.feels}° · ${wd.min}…${wd.max}° · ветер ${wd.wind} м/с`;
    w.querySelector('.edit').onclick = () => cityForm(w, m.city);
  }
  const c = $('wgMoney');
  if (m.currency && m.currency.rates) {
    c.innerHTML = '<div class="dim" style="margin:0 0 4px">Курс ЦБ РФ</div>' + m.currency.rates.map(r =>
      `<div class="row2"><span>${r.code === 'USD' ? '$' : r.code === 'EUR' ? '€' : '¥'} ${r.code}</span><span>${r.value.toFixed(2)} ₽ <span class="${r.change > 0 ? 'up' : 'down'}">${r.change > 0 ? '▲' : r.change < 0 ? '▼' : ''}${Math.abs(r.change).toFixed(2)}</span></span></div>`).join('');
  } else c.innerHTML = '<div class="dim">Курс валют недоступен</div>';
  const n = $('wgNote');
  if (document.activeElement !== n) n.value = m.note || '';
}
let noteTimer = 0;
$('wgNote').addEventListener('input', () => { clearTimeout(noteTimer); noteTimer = setTimeout(() => wv && wv.postMessage({ type: 'home:note', note: $('wgNote').value }), 600); });

function save() { renderTiles(); wv && wv.postMessage({ type: 'tiles:save', tiles }); }
$('editTiles').onclick = () => { editing = !editing; renderTiles(); };
if (wv) {
  wv.addEventListener('message', e => {
    const m = e.data;
    if (m.type === 'widgets') { renderWidgets(m); return; }
    if (m.type === 'news') { renderNews(m.items); return; }
    if (m.type !== 'settings') return;
    const en = m.engines.find(x => x.id === m.settings.engine);
    const name = en ? en.name : 'интернете';
    $('engineName').textContent = en ? en.name : '';
    $('q').placeholder = 'Поиск в ' + name + ' или адрес сайта';
    $('import').classList.toggle('show', !!m.importSuggest);
    if (!editing) { tiles = (m.settings.homeTiles || []).map(t => ({ name: t.name, url: t.url })); renderTiles(); }
    const img = m.settings.wallpaper && !m.settings.wallpaperVideo;
    if (img) { document.body.classList.add('wall'); document.body.style.backgroundImage = `url(wallpaper?v=${m.settings.wallpaperVersion})`; }
    else { document.body.classList.remove('wall'); document.body.style.backgroundImage = ''; }
    HomeBg.apply(m.settings);
    $('foot').innerHTML = item('Только HTTPS', m.settings.httpsOnly) +
      item('Блокировка рекламы и трекеров', m.settings.blockAds) + item('SmartScreen', true) +
      (m.proxyRunning ? item('Обход блокировок', true) : '') +
      (m.adblock && m.adblock.total ? `<span>🛡 Заблокировано: <b>${m.adblock.total.toLocaleString('ru-RU')}</b></span>` : '');
  });
  wv.postMessage({ type: 'get' });
  wv.postMessage({ type: 'home:widgets' });
  wv.postMessage({ type: 'home:news' });
  $('importClose').onclick = () => { $('import').classList.remove('show'); wv.postMessage({ type: 'dismissImport' }); };
}
$('q').focus();

// Новости из подписок (RSS): заголовок, источник, время
function renderNews(items) {
  const box = $('news'), list = $('newsList');
  box.classList.toggle('show', items.length > 0);
  list.innerHTML = '';
  for (const n of items) {
    const a = document.createElement('a'); a.href = n.link;
    const t = document.createElement('span'); t.className = 't'; t.textContent = n.title;
    const s = document.createElement('span'); s.className = 's'; s.textContent = n.source + (n.time ? ' · ' + n.time : '');
    a.append(t, s); list.appendChild(a);
  }
}
