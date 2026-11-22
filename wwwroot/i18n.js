// Перевод своих страниц браузера. Подключается сервером только при английском интерфейсе (i18n.js?lang=en).
(() => {
  const lang = new URL(document.currentScript.src).searchParams.get('lang');
  if (lang !== 'en') return;
  let dict = {};
  try {
    const x = new XMLHttpRequest();
    x.open('GET', 'i18n-en.json', false); // синхронно: переводим до первой отрисовки
    x.send();
    dict = JSON.parse(x.responseText);
  } catch (e) { return; }
  document.documentElement.lang = 'en';
  const CYR = /[А-Яа-яЁё]/;
  // Ключи-префиксы («Пароли для », «Открыть ») — для строк с подставленным текстом
  const prefixes = Object.keys(dict).filter(k => /[ :]$/.test(k)).sort((a, b) => b.length - a.length);

  function lookup(s) {
    if (dict[s] !== undefined) return dict[s];
    // Числа внутри строки: «Найдено 5» ↔ ключ «Найдено {n}»
    if (/\d/.test(s)) {
      const nums = [];
      const k = s.replace(/\d+(?:[.,\s]\d+)*/g, m => { nums.push(m); return '{n}'; });
      if (dict[k] !== undefined) { let i = 0; return dict[k].replace(/\{n\}/g, () => nums[i++] ?? ''); }
    }
    for (const p of prefixes) if (s.startsWith(p)) return dict[p] + s.slice(p.length);
    return null;
  }

  function text(node) {
    const v = node.nodeValue;
    if (!v || !CYR.test(v)) return;
    const t = v.trim();
    const tr = lookup(t);
    if (tr !== null) node.nodeValue = v.replace(t, tr);
  }

  function attrs(el) {
    for (const a of ['placeholder', 'title', 'aria-label']) {
      const v = el.getAttribute && el.getAttribute(a);
      if (v && CYR.test(v)) { const tr = lookup(v.trim()); if (tr !== null) el.setAttribute(a, tr); }
    }
  }

  function walk(root) {
    if (root.nodeType === 3) { text(root); return; }
    if (root.nodeType !== 1 || root.tagName === 'SCRIPT' || root.tagName === 'STYLE') return;
    attrs(root);
    const w = document.createTreeWalker(root, NodeFilter.SHOW_TEXT | NodeFilter.SHOW_ELEMENT, {
      acceptNode: n => n.nodeType === 1 && (n.tagName === 'SCRIPT' || n.tagName === 'STYLE') ? NodeFilter.FILTER_REJECT : NodeFilter.FILTER_ACCEPT,
    });
    let n;
    while ((n = w.nextNode())) { if (n.nodeType === 3) text(n); else attrs(n); }
  }

  const tr = lookup(document.title.trim());
  if (tr !== null) document.title = tr;
  const start = () => {
    walk(document.body);
    // Страницы дорисовывают содержимое скриптами — переводим и новое
    new MutationObserver(list => {
      for (const m of list) {
        if (m.type === 'characterData') text(m.target);
        else if (m.type === 'attributes') attrs(m.target);
        else m.addedNodes.forEach(walk);
      }
    }).observe(document.body, { childList: true, subtree: true, characterData: true, attributes: true, attributeFilter: ['placeholder', 'title', 'aria-label'] });
  };
  if (document.body) start(); else document.addEventListener('DOMContentLoaded', start);
})();
