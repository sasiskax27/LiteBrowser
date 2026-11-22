if (window.top !== window) document.documentElement.innerHTML = '';

const url = new URLSearchParams(location.search).get('url') || '';
let host = url;
try { host = new URL(url).hostname; } catch (e) { }
document.getElementById('host').textContent = host;
const fake = new URLSearchParams(location.search).get('fake');
if (fake) {
  // Похоже на подделку известного сайта
  document.getElementById('title').textContent = 'Похоже на сайт-подделку';
  document.getElementById('reason').style.display = 'none';
  document.getElementById('fakeReason').style.display = '';
  document.getElementById('host2').textContent = host;
  document.getElementById('real').textContent = fake;
  const go = document.createElement('a');
  go.className = 'btn primary';
  go.style.textDecoration = 'none';
  go.textContent = 'Перейти на настоящий ' + fake;
  go.href = 'https://' + fake + '/';
  document.querySelector('.actions').prepend(go);
}
document.getElementById('open').href = 'https://lite.home/allow?url=' + encodeURIComponent(url);
document.getElementById('back').onclick = () => { if (history.length > 1) history.back(); else location.href = 'home.html'; };
