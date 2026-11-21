if (window.top !== window) document.documentElement.innerHTML = '';

const $ = id => document.getElementById(id);
const wv = window.chrome && window.chrome.webview;
const send = m => wv && wv.postMessage(m);
let st = null, busy = false, current = null, pending = null, poll = 0;

// ---------- Безопасный Markdown: сначала экранируем HTML, потом размечаем ----------
function esc(s) { return s.replace(/[&<>"']/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c])); }
function md(src) {
  const blocks = [];
  let s = esc(src).replace(/```(\w*)\n?([\s\S]*?)```/g, (_, l, code) => { blocks.push(code); return `\u0000${blocks.length - 1}\u0000`; });
  s = s.replace(/`([^`\n]+)`/g, '<code>$1</code>')
       .replace(/\*\*([^*\n]+)\*\*/g, '<b>$1</b>')
       .replace(/(^|[^*])\*([^*\n]+)\*/g, '$1<i>$2</i>')
       .replace(/^#{1,6}\s+(.+)$/gm, '<b>$1</b>');
  const lines = s.split('\n'), out = [];
  let list = null;
  for (const line of lines) {
    const ul = line.match(/^\s*[-*•]\s+(.*)/), ol = line.match(/^\s*\d+[.)]\s+(.*)/);
    if (ul || ol) {
      const tag = ul ? 'ul' : 'ol';
      if (list !== tag) { if (list) out.push(`</${list}>`); out.push(`<${tag}>`); list = tag; }
      out.push(`<li>${(ul || ol)[1]}</li>`);
    } else {
      if (list) { out.push(`</${list}>`); list = null; }
      if (line.trim()) out.push(`<p>${line}</p>`);
    }
  }
  if (list) out.push(`</${list}>`);
  return out.join('').replace(/\u0000(\d+)\u0000/g, (_, i) => `<pre><code>${blocks[+i]}</code></pre>`);
}

function scrollDown() { const sc = $('scroll'); sc.scrollTop = sc.scrollHeight; }

function addUser(text, page) {
  const d = document.createElement('div');
  d.className = 'msg user';
  d.textContent = text;
  if (page) { const p = document.createElement('div'); p.className = 'pg'; p.textContent = '📄 ' + page; d.appendChild(p); }
  $('chat').appendChild(d);
}

function addBot() {
  const d = document.createElement('div');
  d.className = 'msg bot';
  d.innerHTML = '<div class="who">✦ Lite AI</div><div class="body cursor"></div>';
  $('chat').appendChild(d);
  return d.querySelector('.body');
}

function setBusy(b) {
  busy = b;
  $('send').classList.toggle('stop', b);
  $('send').innerHTML = b ? '&#xE71A;' : '&#xE724;';
  $('send').title = b ? 'Остановить' : 'Отправить (Enter)';
}

function ask(action, prompt) {
  if (busy) return;
  if (st && !st.ok && st.provider === 'local') { showSetup(); return; }
  const mode = $('ctx').value;
  if (!action && (mode === 'tabs' || mode === 'history')) action = mode;
  pending = { action: action || '', prompt: prompt || '', usePage: action ? true : mode === 'page' };
  send({ type: 'ai:ask', ...pending });
}

$('send').onclick = () => {
  if (busy) { send({ type: 'ai:stop' }); return; }
  const q = $('q').value.trim();
  if (!q) return;
  $('q').value = ''; autosize();
  ask('', q);
};
$('q').addEventListener('keydown', e => { if (e.key === 'Enter' && !e.shiftKey) { e.preventDefault(); $('send').onclick(); } });
function autosize() { const t = $('q'); t.style.height = 'auto'; t.style.height = Math.min(140, t.scrollHeight) + 'px'; }
$('q').addEventListener('input', autosize);
document.querySelectorAll('.chip').forEach(c => c.onclick = () => {
  if (c.dataset.mode) { setMode(c.dataset.mode); $('q').focus(); return; }
  ask(c.dataset.a);
});
const PH = { page: 'Спросите о странице или о чём угодно…', tabs: 'Вопрос по всем открытым вкладкам (например «какой товар дешевле?»)',
  history: 'Что искать в истории? Например «статья про видеокарты на прошлой неделе»', none: 'Спросите о чём угодно…' };
function setMode(m) { $('ctx').value = m; $('q').placeholder = PH[m]; try { localStorage.setItem('aiCtx', m === 'history' ? 'page' : m); } catch (e) { } }
$('ctx').onchange = () => setMode($('ctx').value);
try { const m = localStorage.getItem('aiCtx'); if (m && PH[m]) setMode(m); } catch (e) { }
$('newChat').onclick = () => { if (busy) send({ type: 'ai:stop' }); $('chat').innerHTML = ''; send({ type: 'ai:reset' }); };

// ---------- Настройки и модели ----------
function modelRow(m, inSetup) {
  const row = document.createElement('div');
  row.className = 'model' + (st.localModel === m.id ? ' on' : '');
  row.innerHTML = '<div class="m"><b></b><span></span></div>';
  row.querySelector('b').textContent = m.name;
  row.querySelector('span').textContent = `${m.desc} · ${(m.size / 1024).toFixed(1)} ГБ`;
  const btn = document.createElement('button');
  btn.className = 'btn sm' + (m.downloaded ? '' : ' primary');
  if (st.downloading === m.id) { btn.textContent = '…'; btn.disabled = true; }
  else if (m.downloaded) {
    btn.textContent = st.localModel === m.id ? 'Выбрана' : 'Выбрать';
    btn.disabled = st.localModel === m.id;
    btn.onclick = () => send({ type: 'ai:config', provider: 'local', localModel: m.id });
    if (!inSetup) {
      const del = document.createElement('button');
      del.className = 'ib icon'; del.innerHTML = '&#xE74D;'; del.title = 'Удалить модель с диска';
      del.onclick = () => { if (confirm(`Удалить «${m.name}» с диска?`)) send({ type: 'ai:deleteModel', id: m.id }); };
      row.appendChild(del);
    }
  } else {
    btn.textContent = 'Скачать';
    btn.disabled = !!st.downloading;
    btn.onclick = () => send({ type: 'ai:download', id: m.id });
  }
  row.insertBefore(btn, row.children[1] || null);
  return row;
}

function showSetup() { $('setup').classList.remove('hidden'); scrollDown(); }

function render() {
  const names = { local: 'встроенный', ollama: 'Ollama', openai: 'облачный API' };
  const cur = st.provider === 'local' ? (st.local.find(m => m.id === st.localModel) || {}).name : st.model;
  $('sub').textContent = `${names[st.provider]}${cur ? ' · ' + cur : ''}${st.ok ? '' : ' · не готов'}`;
  // Первая настройка встроенного ИИ
  const needSetup = st.provider === 'local' && !st.local.some(m => m.downloaded);
  $('setup').classList.toggle('hidden', !needSetup && !st.downloading);
  const sm = $('setupModels'); sm.innerHTML = '';
  if (!st.downloading) st.local.forEach(m => sm.appendChild(modelRow(m, true)));
  $('dl').classList.toggle('hidden', !st.downloading);
  $('dlBar').style.width = Math.round((st.progress || 0) * 100) + '%';
  $('dlText').textContent = st.localStatus || '';
  if (!st.downloading && st.localStatus && /Ошибка|остановлена/.test(st.localStatus)) $('setupText').textContent = st.localStatus;
  // Панель настроек
  document.querySelectorAll('#prov button').forEach(b => b.classList.toggle('on', b.dataset.v === st.provider));
  $('pLocal').classList.toggle('hidden', st.provider !== 'local');
  $('pOllama').classList.toggle('hidden', st.provider !== 'ollama');
  $('pOpenai').classList.toggle('hidden', st.provider !== 'openai');
  const lm = $('localModels'); lm.innerHTML = '';
  st.local.forEach(m => lm.appendChild(modelRow(m, false)));
  $('gpu').checked = st.gpu;
  $('backend').textContent = st.backend ? `Сейчас работает на: ${st.backend === 'vulkan' ? 'видеокарте' : 'процессоре'}` : '';
  $('ollamaUrl').value = st.ollamaUrl;
  $('baseUrl').value = st.baseUrl;
  $('keyState').textContent = st.hasKey ? '(сохранён)' : '';
  const om = $('ollamaModel'); om.innerHTML = '';
  for (const m of (st.provider === 'ollama' ? st.models : [])) { const o = document.createElement('option'); o.value = o.textContent = m; om.appendChild(o); }
  om.value = st.model;
  if (st.provider === 'openai') {
    $('openaiModel').value = st.model;
    const dl = $('openaiList'); dl.innerHTML = '';
    for (const m of st.models) { const o = document.createElement('option'); o.value = m; dl.appendChild(o); }
  }
  if (!st.ok && st.error && st.provider !== 'local' && !$('settings').classList.contains('hidden') === false) $('hint').textContent = st.error;
  else $('hint').textContent = 'Enter — отправить';
  clearTimeout(poll);
  if (st.downloading) poll = setTimeout(() => send({ type: 'ai:status' }), 800);
}

$('gear').onclick = () => { $('settings').classList.toggle('hidden'); send({ type: 'ai:status' }); };
$('closeCfg').onclick = () => $('settings').classList.add('hidden');
document.querySelectorAll('#prov button').forEach(b => b.onclick = () => send({ type: 'ai:config', provider: b.dataset.v }));
$('gpu').onchange = () => send({ type: 'ai:config', gpu: $('gpu').checked });
$('saveCfg').onclick = () => {
  const cfg = { type: 'ai:config' };
  if (st.provider === 'ollama') { cfg.ollamaUrl = $('ollamaUrl').value.trim(); cfg.model = $('ollamaModel').value; }
  if (st.provider === 'openai') { cfg.baseUrl = $('baseUrl').value.trim(); cfg.model = $('openaiModel').value.trim(); if ($('apiKey').value) cfg.key = $('apiKey').value; $('apiKey').value = ''; }
  send(cfg);
  $('settings').classList.add('hidden');
};
$('dlCancel').onclick = () => send({ type: 'ai:cancelDownload' });
$('consentYes').onclick = () => { $('consent').classList.add('hidden'); send({ type: 'ai:consent' }); if (pending) send({ type: 'ai:ask', ...pending }); };
$('consentNo').onclick = () => $('consent').classList.add('hidden');

if (wv) {
  wv.addEventListener('message', e => {
    const m = e.data;
    switch (m.type) {
      case 'aiStatus': st = m; render(); break;
      case 'aiStart': addUser(m.question, m.page); current = addBot(); setBusy(true); scrollDown(); break;
      case 'aiToken': if (current) { current.innerHTML = md(m.text); current.classList.add('cursor'); scrollDown(); } break;
      case 'aiDone': if (current) { current.innerHTML = md(m.text || '…'); current.classList.remove('cursor'); } current = null; setBusy(false); scrollDown(); break;
      case 'aiError': {
        if (current) { current.classList.remove('cursor'); current = null; }
        const d = document.createElement('div'); d.className = 'msg err'; d.textContent = '⚠ ' + m.text; $('chat').appendChild(d);
        setBusy(false); scrollDown(); send({ type: 'ai:status' }); break;
      }
      case 'aiNeedConsent': $('consent').classList.remove('hidden'); break;
      case 'aiRun': ask(m.action); break;
    }
  });
  send({ type: 'ai:status' });
  const a = new URLSearchParams(location.search).get('action');
  if (a) setTimeout(() => ask(a), 400);
}
autosize();
$('q').focus();
