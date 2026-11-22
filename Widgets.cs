using System.Globalization;
using System.Text.Json;

namespace LiteBrowser;

/// <summary>
/// Виджеты стартовой страницы: погода (open-meteo.com, без ключа и регистрации) и курс валют (ЦБ РФ).
/// Город пользователь указывает сам — по IP ничего не определяется. Данные кэшируются на 30 минут.
/// </summary>
static class Widgets
{
    static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(8) };
    static (string city, DateTime at, object? data) weatherCache;
    static (DateTime at, object? data) currencyCache;

    static string WeatherText(int code) => code switch
    {
        0 => "Ясно", 1 => "Малооблачно", 2 => "Переменная облачность", 3 => "Пасмурно",
        45 or 48 => "Туман", 51 or 53 or 55 => "Морось", 56 or 57 => "Ледяная морось",
        61 or 63 => "Дождь", 65 => "Ливень", 66 or 67 => "Ледяной дождь",
        71 or 73 => "Снег", 75 => "Сильный снег", 77 => "Снежная крупа",
        80 or 81 or 82 => "Ливни", 85 or 86 => "Снегопад", 95 => "Гроза", 96 or 99 => "Гроза с градом",
        _ => "",
    };

    static string WeatherIcon(int code, bool day) => code switch
    {
        0 => day ? "☀️" : "🌙", 1 or 2 => day ? "⛅" : "☁️", 3 => "☁️", 45 or 48 => "🌫️",
        >= 51 and <= 67 => "🌧️", >= 71 and <= 77 => "🌨️", >= 80 and <= 82 => "🌦️", 85 or 86 => "❄️", >= 95 => "⛈️", _ => "🌡️",
    };

    public static async Task<object?> WeatherAsync(string city)
    {
        if (string.IsNullOrWhiteSpace(city)) return null;
        if (weatherCache.city == city && DateTime.Now - weatherCache.at < TimeSpan.FromMinutes(30)) return weatherCache.data;
        var geo = await Http.GetStringAsync("https://geocoding-api.open-meteo.com/v1/search?count=1&language=ru&name=" + Uri.EscapeDataString(city));
        using var g = JsonDocument.Parse(geo);
        if (!g.RootElement.TryGetProperty("results", out var res) || res.GetArrayLength() == 0) return new { error = "Город не найден" };
        var place = res[0];
        double lat = place.GetProperty("latitude").GetDouble(), lon = place.GetProperty("longitude").GetDouble();
        string name = place.GetProperty("name").GetString() ?? city;
        var url = "https://api.open-meteo.com/v1/forecast?timezone=auto&forecast_days=1" +
                  $"&latitude={lat.ToString(CultureInfo.InvariantCulture)}&longitude={lon.ToString(CultureInfo.InvariantCulture)}" +
                  "&current=temperature_2m,apparent_temperature,weather_code,is_day,wind_speed_10m&daily=temperature_2m_max,temperature_2m_min";
        using var w = JsonDocument.Parse(await Http.GetStringAsync(url));
        var cur = w.RootElement.GetProperty("current");
        var daily = w.RootElement.GetProperty("daily");
        int code = cur.GetProperty("weather_code").GetInt32();
        bool day = cur.GetProperty("is_day").GetInt32() == 1;
        var data = new
        {
            city = name,
            temp = (int)Math.Round(cur.GetProperty("temperature_2m").GetDouble()),
            feels = (int)Math.Round(cur.GetProperty("apparent_temperature").GetDouble()),
            wind = (int)Math.Round(cur.GetProperty("wind_speed_10m").GetDouble() / 3.6),
            max = (int)Math.Round(daily.GetProperty("temperature_2m_max")[0].GetDouble()),
            min = (int)Math.Round(daily.GetProperty("temperature_2m_min")[0].GetDouble()),
            text = WeatherText(code), icon = WeatherIcon(code, day),
        };
        weatherCache = (city, DateTime.Now, data);
        return data;
    }

    public static async Task<object?> CurrencyAsync()
    {
        if (currencyCache.data != null && DateTime.Now - currencyCache.at < TimeSpan.FromMinutes(30)) return currencyCache.data;
        using var d = JsonDocument.Parse(await Http.GetStringAsync("https://www.cbr-xml-daily.ru/daily_json.js"));
        var v = d.RootElement.GetProperty("Valute");
        object Rate(string code)
        {
            var x = v.GetProperty(code);
            double value = x.GetProperty("Value").GetDouble() / x.GetProperty("Nominal").GetDouble();
            double prev = x.GetProperty("Previous").GetDouble() / x.GetProperty("Nominal").GetDouble();
            return new { code, value = Math.Round(value, 2), change = Math.Round(value - prev, 2) };
        }
        var data = new { rates = new[] { Rate("USD"), Rate("EUR"), Rate("CNY") } };
        currencyCache = (DateTime.Now, data);
        return data;
    }
}
