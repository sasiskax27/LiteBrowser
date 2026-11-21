using System.Text.Json;

namespace LiteBrowser;

/// <summary>Скрипты для страниц: находят формы входа, ловят отправку пароля и заполняют поля по команде пользователя.</summary>
static class PasswordScripts
{
    /// <summary>Внедряется в каждую страницу. Сам ничего не заполняет — только сообщает браузеру о форме и отправке.</summary>
    public const string Page = """
(() => {
  if (window.top !== window || !window.chrome || !window.chrome.webview) return;
  const post = m => { try { window.chrome.webview.postMessage(m); } catch (e) {} };
  const visible = el => !!(el.offsetWidth || el.offsetHeight || el.getClientRects().length);
  let formSent = false, last = '';
  const check = () => {
    if (!formSent && [...document.querySelectorAll('input[type=password]')].some(visible)) { formSent = true; post({ type: 'pw:form' }); }
  };
  const userBefore = P => {
    let u = null;
    for (const i of document.querySelectorAll('input')) {
      if (i === P) break;
      if (/^(text|email|tel)$/i.test(i.type) && i.value && visible(i)) u = i;
    }
    return u ? u.value : '';
  };
  const grab = () => {
    const pws = [...document.querySelectorAll('input[type=password]')].filter(i => i.value);
    if (!pws.length) return;
    const P = pws[0], user = userBefore(P);
    const key = location.host + '\n' + user + '\n' + P.value;
    if (key === last) return;
    last = key;
    post({ type: 'pw:capture', username: user, password: P.value });
  };
  document.addEventListener('submit', () => grab(), true);
  document.addEventListener('click', e => {
    const b = e.target && e.target.closest ? e.target.closest('button, input[type=submit]') : null;
    if (b && (b.type === 'submit' || (b.tagName === 'BUTTON' && !b.getAttribute('type') && b.form))) setTimeout(grab, 0);
  }, true);
  document.addEventListener('keydown', e => { if (e.key === 'Enter' && e.target && e.target.type === 'password') grab(); }, true);
  document.addEventListener('change', e => {
    const t = e.target;
    if (t && t.tagName === 'INPUT' && /^(email|text|tel)$/i.test(t.type) && t.value &&
        /user|login|email|mail|phone|identifier/i.test((t.name || '') + (t.id || '') + (t.autocomplete || '') + t.type))
      post({ type: 'pw:user', username: t.value });
  }, true);
  document.addEventListener('focusin', check, true);
  if (document.readyState === 'loading') document.addEventListener('DOMContentLoaded', check); else check();
  setTimeout(check, 1500);
})();
""";

    const string Helpers = """
  const visible = el => !!(el.offsetWidth || el.offsetHeight || el.getClientRects().length);
  const set = (el, v) => {
    el.focus();
    const d = Object.getOwnPropertyDescriptor(HTMLInputElement.prototype, 'value');
    d.set.call(el, v);
    el.dispatchEvent(new Event('input', { bubbles: true }));
    el.dispatchEvent(new Event('change', { bubbles: true }));
  };
""";

    /// <summary>Заполнить логин и пароль (запускается только по клику пользователя).</summary>
    public static string Fill(string user, string pass) => $$"""
((u, p) => {
{{Helpers}}
  const inputs = [...document.querySelectorAll('input')].filter(visible);
  const P = inputs.find(i => i.type === 'password');
  let U = null;
  for (const i of inputs) { if (i === P) break; if (/^(text|email|tel)$/i.test(i.type)) U = i; }
  if (!P && !U) return 'none';
  if (U && u) set(U, u);
  if (P) set(P, p);
  return P ? 'ok' : 'user';
})({{JsonSerializer.Serialize(user)}}, {{JsonSerializer.Serialize(pass)}})
""";

    /// <summary>Вставить сгенерированный пароль во все пустые поля пароля (регистрация: пароль + повтор).</summary>
    public static string FillGenerated(string pass) => $$"""
((p) => {
{{Helpers}}
  const fields = [...document.querySelectorAll('input[type=password]')].filter(i => visible(i) && !i.value);
  fields.forEach(f => set(f, p));
  return fields.length;
})({{JsonSerializer.Serialize(pass)}})
""";
}
