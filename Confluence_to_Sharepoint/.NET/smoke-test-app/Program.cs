using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Playwright;

public class AppConfig
{
    public string TargetSiteUrl { get; set; } = "";
    public string ApiCheckPath { get; set; } = "/_api/web/title";
    public int RemoteDebuggingPort { get; set; } = 9222;
    public int WaitTimeoutSeconds { get; set; } = 300;
    public string CookiesOutputPath { get; set; } = "cookies.json";
}

public class Program
{
    public static async Task<int> Main(string[] args)
    {
        Console.WriteLine("== UI-Assisted SharePoint Smoke Test ==");
        var config = LoadConfig();
        if (string.IsNullOrWhiteSpace(config.TargetSiteUrl))
        {
            Console.WriteLine("ERROR: Set TargetSiteUrl in appsettings.json or pass --site=https://tenant.sharepoint.com/sites/YourSite");
            return 1;
        }
        foreach (var arg in args)
        {
            if (arg.StartsWith("--site=")) config.TargetSiteUrl = arg.Substring("--site=".Length);
            if (arg.StartsWith("--port=") && int.TryParse(arg.Substring("--port=".Length), out var p)) config.RemoteDebuggingPort = p;
        }

        var cdpEndpoint = $"http://localhost:{config.RemoteDebuggingPort}";
        Console.WriteLine($"Connecting to system browser via CDP: {cdpEndpoint}");
        Console.WriteLine("Start Chrome/Edge with:  --remote-debugging-port=9222");
        Console.WriteLine($"Waiting up to {config.WaitTimeoutSeconds}s for login at: {config.TargetSiteUrl}");

        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.ConnectOverCDPAsync(cdpEndpoint);

        var context = browser.Contexts.FirstOrDefault();
        IPage? page = null;
        try
        {
            if (context != null && context.Pages.Count > 0)
            {
                page = context.Pages.First();
            }
            else
            {
                page = await browser.NewPageAsync();
            }
        }
        catch
        {
            Console.WriteLine("Cannot create a page via CDP; open a tab manually and navigate to the site.");
        }

        if (page != null)
        {
            try { await page.GotoAsync(config.TargetSiteUrl, new PageGotoOptions { WaitUntil = WaitUntilState.Load }); }
            catch { /* ignore */ }
        }

        var deadline = DateTime.UtcNow.AddSeconds(config.WaitTimeoutSeconds);
        var targetOrigin = new Uri(config.TargetSiteUrl).GetLeftPart(UriPartial.Authority);

        async Task<IReadOnlyList<Microsoft.Playwright.Cookie>> ReadCookiesAsync()
        {
            var ctx = browser.Contexts.FirstOrDefault();
            if (ctx == null) return Array.Empty<Microsoft.Playwright.Cookie>();
            try { return await ctx.CookiesAsync(new[] { targetOrigin }); } catch { return Array.Empty<Microsoft.Playwright.Cookie>(); }
        }

        while (DateTime.UtcNow < deadline)
        {
            var cookies = await ReadCookiesAsync();
            var names = cookies.Select(c => c.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var ok = names.Contains("FedAuth") && names.Contains("rtFa");
            if (ok)
            {
                Console.WriteLine("Login detected (FedAuth + rtFa).");
                await SaveCookiesAsync(cookies, config.CookiesOutputPath);
                Console.WriteLine($"Saved → {config.CookiesOutputPath}");
                await CallApiAsync(config, cookies);
                return 0;
            }
            Console.Write("Waiting for login...\r");
            await Task.Delay(1500);
        }

        Console.WriteLine("\nTimeout waiting for cookies.");
        return 2;
    }

    static AppConfig LoadConfig()
    {
        var cfg = new AppConfig();
        try
        {
            if (File.Exists("appsettings.json"))
            {
                var json = File.ReadAllText("appsettings.json");
                var fromFile = JsonSerializer.Deserialize<AppConfig>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                if (fromFile != null) cfg = fromFile;
            }
        }
        catch { }
        return cfg;
    }

    static async Task SaveCookiesAsync(IReadOnlyList<Microsoft.Playwright.Cookie> cookies, string path)
    {
        var mapped = cookies.Select(c => new { c.Name, c.Value, c.Domain, c.Path, c.SameSite, c.HttpOnly, c.Secure, c.Expires });
        var json = JsonSerializer.Serialize(mapped, new JsonSerializerOptions { WriteIndented = true });
        await File.WriteAllTextAsync(path, json);
    }

    static async Task CallApiAsync(AppConfig cfg, IReadOnlyList<Microsoft.Playwright.Cookie> cookies)
    {
        var target = new Uri(new Uri(cfg.TargetSiteUrl), cfg.ApiCheckPath);
        Console.WriteLine($"Calling API: {target}");

        var handler = new HttpClientHandler
        {
            CookieContainer = new CookieContainer(),
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate
        };

        var baseUri = new Uri(cfg.TargetSiteUrl);
        foreach (var c in cookies)
        {
            var domain = c.Domain.StartsWith(".") ? c.Domain.TrimStart('.') : c.Domain;
            handler.CookieContainer.Add(baseUri, new Cookie(c.Name, c.Value, c.Path, domain));
        }

        using var http = new HttpClient(handler);
        http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json;odata=nometadata"));

        var resp = await http.GetAsync(target);
        Console.WriteLine($"HTTP {(int)resp.StatusCode} {resp.ReasonPhrase}");
        var body = await resp.Content.ReadAsStringAsync();
        Console.WriteLine("Response:");
        Console.WriteLine(body);
    }
}
