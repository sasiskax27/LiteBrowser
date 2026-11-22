namespace LiteBrowser;

/// <summary>
/// Cookie-баннеры: жмём «Отклонить» / «Только необходимые» (известные системы согласий и поиск по тексту кнопки).
/// Если отказаться нельзя (только «Принять» или «Понятно») — баннер просто скрываем, согласие не даём.
/// Работает в странице и во фреймах согласий; следит за страницей 20 секунд после загрузки.
/// </summary>
static class CookieBanners
{
    public const string Script = """
(() => {
  if (window.__liteCookie) return; window.__liteCookie = 1;
  if (location.protocol !== 'https:' && location.protocol !== 'http:') return;
  const REJECT = /^\s*(reject( all| cookies)?|decline( all)?|deny( all)?|refuse( all)?|disagree|do not consent|only (necessary|essential|required)|(necessary|essential|required) (cookies )?only|use necessary cookies only|continue without (accepting|agreeing)|отклонить( все)?|отказаться|запретить( все)?|не (соглашаюсь|принимать)|только (необходимые|обязательные|технические)( cookies?| файлы)?|продолжить без (согласия|принятия)|ablehnen|alle ablehnen|refuser|tout refuser|rechazar( todo)?|rifiuta( tutto)?)\s*[.!]?\s*$/i;
  const KNOWN = [
    '#onetrust-reject-all-handler', '.ot-pc-refuse-all-handler',
    '#CybotCookiebotDialogBodyButtonDecline', '#CybotCookiebotDialogBodyLevelButtonLevelOptinDeclineAll',
    '#didomi-notice-disagree-button', '.didomi-continue-without-agreeing',
    '.fc-cta-do-not-consent', '.qc-cmp2-summary-buttons button[mode="secondary"]',
    '#truste-consent-required', '.cmplz-deny', '.cky-btn-reject', '.osano-cm-denyAll', '.iubenda-cs-reject-btn',
    '.cm-btn-decline', '.cn-decline', '[data-tid="banner-decline"]', '[data-cookie-refuse]', '._brlbs-refuse-btn',
    'button[data-testid="uc-deny-all-button"]', '#cookiescript_reject', '.cc-deny', '.js-cookie-decline',
  ];
  const WORDS = /cookie|consent|gdpr|ccpa|privacy|cmp|куки|файл(ы|ов) cookie|согласи/i;
  const consentFrame = window.top !== window && /consent|cmp|privacy|cookie|sourcepoint|sp_message/i.test(location.href);
  const visible = el => { const r = el.getBoundingClientRect(); const s = getComputedStyle(el); return r.width > 0 && r.height > 0 && s.visibility !== 'hidden' && s.display !== 'none' && s.opacity !== '0'; };
  const label = el => (el.innerText || el.value || el.getAttribute('aria-label') || '').trim().replace(/\s+/g, ' ');

  // Поиск и в теневом DOM (Usercentrics и др.)
  const all = (sel, root = document) => {
    const out = [...root.querySelectorAll(sel)];
    for (const h of root.querySelectorAll('*')) if (h.shadowRoot) out.push(...all(sel, h.shadowRoot));
    return out;
  };
  // Баннер — это блок поверх страницы (fixed/sticky или диалог), в котором говорится о cookie
  const bannerOf = el => {
    for (let n = el; n && n !== document.body && n !== document.documentElement; n = n.parentElement || (n.getRootNode() && n.getRootNode().host)) {
      if (!(n instanceof Element)) break;
      const s = getComputedStyle(n);
      const over = s.position === 'fixed' || s.position === 'sticky' || n.getAttribute('role') === 'dialog' || n.getAttribute('aria-modal') === 'true';
      if (over && WORDS.test((n.id || '') + ' ' + (n.className && n.className.baseVal === undefined ? n.className : '') + ' ' + (n.innerText || '').slice(0, 600))) return n;
    }
    return null;
  };

  let rejected = false;
  const tryReject = () => {
    if (rejected) return;
    for (const sel of KNOWN) for (const b of all(sel)) if (visible(b)) { b.click(); rejected = true; return; }
    for (const b of all('button, a[role="button"], [role="button"], input[type="button"], input[type="submit"], a')) {
      const t = label(b);
      if (!t || t.length > 60 || !REJECT.test(t) || !visible(b)) continue;
      if (consentFrame || bannerOf(b)) { b.click(); rejected = true; return; }
    }
  };

  // Отказаться нельзя — прячем баннер (только небольшой: не закрываем полстраницы с содержимым)
  const tryHide = () => {
    if (window.top !== window) return;
    for (const n of document.querySelectorAll('body *')) {
      if (n.__liteHidden || n.closest('[data-lite-hidden]')) continue;
      const s = getComputedStyle(n);
      if (s.position !== 'fixed' && s.position !== 'sticky') continue;
      const r = n.getBoundingClientRect();
      if (r.height < 30 || r.height > Math.max(innerHeight, 600) * 0.45 || r.width < 200) continue;
      const txt = (n.innerText || '');
      if (txt.length > 500 || !/cookie|куки/i.test(txt)) continue;
      // Есть кнопка отказа — значит, её нажмёт tryReject; прячем только баннеры без выбора
      if ([...n.querySelectorAll('button, a, [role="button"]')].some(b => REJECT.test(label(b)))) continue;
      n.__liteHidden = true; n.setAttribute('data-lite-hidden', '');
      n.style.setProperty('display', 'none', 'important');
    }
  };

  const start = Date.now();
  let last = 0, timer = 0;
  // Не чаще двух раз в секунду: страница может меняться постоянно
  const tick = () => {
    clearTimeout(timer);
    const wait = 500 - (Date.now() - last);
    if (wait > 0) { timer = setTimeout(tick, wait); return; }
    last = Date.now();
    tryReject();
    if (Date.now() - start > 1500) tryHide();
  };
  const run = () => {
    tick();
    const mo = new MutationObserver(tick);
    mo.observe(document.documentElement, { childList: true, subtree: true });
    setTimeout(tick, 2000);
    setTimeout(() => { mo.disconnect(); clearTimeout(timer); }, 20000);
  };
  if (document.readyState === 'loading') document.addEventListener('DOMContentLoaded', () => setTimeout(run, 300)); else setTimeout(run, 300);
})();
""";
}
