using System.Text.Json;
using System.Text.RegularExpressions;

namespace LiteBrowser;

/// <summary>Сравнение цен: находим товар на странице и ищем его в других магазинах.</summary>
sealed partial class MainForm
{
    record Product(string Name, string Price, string Currency);

    /// <summary>Название и цена товара: из разметки schema.org (JSON-LD), мета-тегов, иначе — заголовок страницы.</summary>
    internal const string ProductJs = """
(() => {
  const out = { name: '', price: '', currency: '' };
  const visit = x => {
    if (!x || typeof x !== 'object' || out.name) return;
    if (Array.isArray(x)) { x.forEach(visit); return; }
    if (x['@graph']) visit(x['@graph']);
    const t = [].concat(x['@type'] || []);
    if (t.includes('Product') || t.includes('ProductGroup')) {
      out.name = String(x.name || '');
      const o = [].concat(x.offers || [])[0] || {};
      out.price = String(o.price || o.lowPrice || (o.priceSpecification || {}).price || '');
      out.currency = String(o.priceCurrency || '');
    }
  };
  for (const s of document.querySelectorAll('script[type="application/ld+json"]')) { try { visit(JSON.parse(s.textContent)); } catch (e) { } }
  const meta = q => { const m = document.querySelector(q); return m ? (m.getAttribute('content') || m.textContent || '').trim() : ''; };
  if (!out.price) { out.price = meta('meta[property="product:price:amount"], meta[itemprop="price"], [itemprop="price"]'); out.currency = out.currency || meta('meta[property="product:price:currency"], [itemprop="priceCurrency"]'); }
  if (!out.name) out.name = meta('meta[property="og:title"]') || ((document.querySelector('h1') || {}).innerText || '').trim() || document.title;
  return JSON.stringify(out);
})()
""";

    static readonly (string name, string url)[] ShopsRu =
    {
        ("Яндекс Маркет", "https://market.yandex.ru/search?text="),
        ("Ozon", "https://www.ozon.ru/search/?text="),
        ("Wildberries", "https://www.wildberries.ru/catalog/0/search.aspx?search="),
        ("Мегамаркет", "https://megamarket.ru/catalog/?q="),
        ("Avito", "https://www.avito.ru/rossiya?q="),
        ("AliExpress", "https://aliexpress.ru/wholesale?SearchText="),
        ("Google Покупки", "https://www.google.com/search?tbm=shop&q="),
    };

    static readonly (string name, string url)[] ShopsEn =
    {
        ("Amazon", "https://www.amazon.com/s?k="),
        ("eBay", "https://www.ebay.com/sch/i.html?_nkw="),
        ("AliExpress", "https://www.aliexpress.com/wholesale?SearchText="),
        ("Walmart", "https://www.walmart.com/search?q="),
        ("Google Shopping", "https://www.google.com/search?tbm=shop&q="),
    };

    async Task<Product?> ReadProduct()
    {
        var core = active?.Core;
        if (core == null || !core.Source.StartsWith("http")) return null;
        try
        {
            var raw = await core.ExecuteScriptAsync(ProductJs);
            var json = JsonSerializer.Deserialize<string>(raw);
            if (json == null) return null;
            using var d = JsonDocument.Parse(json);
            var r = d.RootElement;
            var name = CleanProductName(r.GetProperty("name").GetString() ?? "");
            return name.Length == 0 ? null : new Product(name, r.GetProperty("price").GetString() ?? "", r.GetProperty("currency").GetString() ?? "");
        }
        catch { return null; }
    }

    /// <summary>Убираем из названия «купить», название магазина после « | » и прочий хвост — для поиска нужна суть.</summary>
    internal static string CleanProductName(string s)
    {
        s = Regex.Replace(s, @"\s+", " ").Trim();
        s = Regex.Split(s, @"\s[|–—]\s|\s-\s(?=[^-]*$)")[0];
        s = Regex.Replace(s, @"^(купить|buy)\s+", "", RegexOptions.IgnoreCase);
        s = Regex.Replace(s, @"\s+(купить|по выгодной цене|в интернет-магазине|с доставкой|цена|отзывы).*$", "", RegexOptions.IgnoreCase);
        return s.Length > 90 ? s[..90].TrimEnd() : s.Trim();
    }

    /// <summary>Меню «Сравнить цены»: магазины, «открыть все», разбор товара ИИ.</summary>
    async Task ComparePrices()
    {
        var p = await ReadProduct();
        if (p == null) { infoBar.ShowBar("prices", "", L.T("На этой странице не нашлось товара")); return; }
        var menu = Theme.NewMenu();
        var head = p.Name + (p.Price.Length > 0 ? $" — {p.Price} {p.Currency}".TrimEnd() : "");
        menu.Items.Add(new ToolStripLabel(head.Length > 70 ? head[..70] + "…" : head) { ForeColor = Theme.TextDim, Margin = new Padding(8, 4, 8, 4) });
        menu.Items.Add(new ToolStripSeparator());
        var shops = L.En ? ShopsEn : ShopsRu;
        var host = new Uri(active!.Core!.Source).Host;
        var list = shops.Where(s => !host.EndsWith(new Uri(s.url).Host.Replace("www.", ""))).ToList(); // свой магазин не предлагаем
        foreach (var (name, url) in list)
        {
            var u = url + Uri.EscapeDataString(p.Name);
            menu.Items.Add(Theme.Item(L.T("Искать в ") + name, () => CreateTab(u, true), null, ""));
        }
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(Theme.Item(L.T("Открыть все магазины во вкладках"), () =>
        {
            foreach (var (_, url) in list) CreateTab(url + Uri.EscapeDataString(p.Name), false);
        }, null, ""));
        if (Cfg.AiEnabled)
            menu.Items.Add(Theme.Item(L.T("✦ Разобрать товар с ИИ: плюсы, минусы, аналоги"), () => OpenAi("product"), null, ""));
        menu.Items.Add(Theme.Item(L.T("Скопировать название"), () => Clipboard.SetText(p.Name), null, ""));
        var r = RectangleToClient(address.RectangleToScreen(address.ClientRectangle));
        menu.Show(this, new Point(r.Right - menu.PreferredSize.Width, r.Bottom + Theme.S(6)));
    }
}
