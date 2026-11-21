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

const item = (name, on) => `<span class="${on ? '' : 'off'}">${name}: <b>${on ? 'вкл' : 'выкл'}</b></span>`;

const wv = window.chrome && window.chrome.webview;
if (wv) {
  wv.addEventListener('message', e => {
    const m = e.data;
    if (m.type !== 'settings') return;
    const en = m.engines.find(x => x.id === m.settings.engine);
    const name = en ? en.name : 'интернете';
    $('engineName').textContent = en ? en.name : '';
    $('q').placeholder = 'Поиск в ' + name + ' или адрес сайта';
    $('foot').innerHTML = item('Только HTTPS', m.settings.httpsOnly) +
      item('Блокировка рекламы и трекеров', m.settings.blockAds) + item('SmartScreen', true) +
      (m.proxyRunning ? item('Обход блокировок', true) : '');
  });
  wv.postMessage({ type: 'get' });
}
$('q').focus();
