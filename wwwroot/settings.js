if (window.top !== window) document.documentElement.innerHTML = '';

const $ = id => document.getElementById(id);
const wv = window.chrome && window.chrome.webview;
let state = null, engines = [], toastTimer = 0;

function toast(text) {
  $('toastText').textContent = text;
  $('toast').classList.add('show');
  clearTimeout(toastTimer);
  toastTimer = setTimeout(() => $('toast').classList.remove('show'), 1600);
}

function save() { wv.postMessage({ type: 'save', settings: state }); }

function render() {
  const box = $('engines');
  box.innerHTML = '';
  for (const en of engines) {
    const el = document.createElement('button');
    el.className = 'engine' + (en.id === state.engine ? ' on' : '');
    el.innerHTML = '<div class="en-name"></div><div class="en-desc"></div>' +
      `<span class="tag ${en.private ? 'ok' : 'warn'}">${en.private ? 'Приватный' : 'Собирает данные'}</span>` +
      '<span class="check icon">&#xE73E;</span>';
    el.querySelector('.en-name').textContent = en.name;
    el.querySelector('.en-desc').textContent = en.desc;
    el.onclick = () => { if (state.engine === en.id) return; state.engine = en.id; render(); save(); };
    box.appendChild(el);
  }
  $('https').checked = state.httpsOnly;
  $('ads').checked = state.blockAds;
  document.querySelectorAll('#theme button').forEach(b => b.classList.toggle('on', b.dataset.v === state.siteTheme));
}

$('https').onchange = e => { state.httpsOnly = e.target.checked; save(); };
$('ads').onchange = e => { state.blockAds = e.target.checked; save(); };
document.querySelectorAll('#theme button').forEach(b => b.onclick = () => { state.siteTheme = b.dataset.v; render(); save(); });
$('clear').onclick = () => {
  if (confirm('Удалить историю, cookies, кэш и данные сайтов?\nВы выйдете из всех аккаунтов.')) wv.postMessage({ type: 'clear' });
};

if (wv) {
  wv.addEventListener('message', e => {
    const m = e.data;
    if (m.type === 'settings') {
      state = m.settings; engines = m.engines; render();
      $('about').innerHTML = '';
      for (const line of ['Версия LiteBrowser: ' + m.version, 'Движок: Chromium (Microsoft WebView2) ' + m.browserVersion,
                          'Движок обновляется автоматически вместе с Windows']) {
        const d = document.createElement('div'); d.textContent = line; $('about').appendChild(d);
      }
    }
    else if (m.type === 'saved') toast('Сохранено');
    else if (m.type === 'cleared') toast('Данные удалены');
  });
  wv.postMessage({ type: 'get' });
}
