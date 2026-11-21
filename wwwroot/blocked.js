if (window.top !== window) document.documentElement.innerHTML = '';

const url = new URLSearchParams(location.search).get('url') || '';
let host = url;
try { host = new URL(url).hostname; } catch (e) { }
document.getElementById('host').textContent = host;
document.getElementById('open').href = 'https://lite.home/allow?url=' + encodeURIComponent(url);
document.getElementById('back').onclick = () => { if (history.length > 1) history.back(); else location.href = 'home.html'; };
