static async Task PrintWhereWeAreAsync(HttpClient http, Uri baseUri)
{
    var webInfo = new Uri(baseUri, "/_api/web?$select=Title,Url,ServerRelativeUrl");
    using (var r = await http.GetAsync(webInfo))
    {
        Console.WriteLine($"[Web info] HTTP {(int)r.StatusCode} {r.ReasonPhrase}");
        var body = await r.Content.ReadAsStringAsync();
        Console.WriteLine(body.Length > 600 ? body[..600] + " …" : body);
    }

    var listsUrl = new Uri(baseUri,
        "/_api/web/lists?$select=Title,BaseTemplate,RootFolder/ServerRelativeUrl&$expand=RootFolder");
    using (var lr = await http.GetAsync(listsUrl))
    {
        Console.WriteLine($"[Lists summary] HTTP {(int)lr.StatusCode} {lr.ReasonPhrase}");
        var body = await lr.Content.ReadAsStringAsync();
        Console.WriteLine(body.Length > 600 ? body[..600] + " …" : body);
    }
}


Where to call:

await PrintWhereWeAreAsync(http, baseUri);           // ← add this
var pagePath = await ResolvePagePathAcrossLibrariesAsync(http, baseUri, "First-Test-Page.aspx");

2) Point the resolver at the correct site

Update your config so cfg.TargetSiteUrl is the site that holds the page, e.g.:

https://<tenant>.sharepoint.com/sites/<yourSite>


Then rebuild:

var baseUri = new Uri(cfg.TargetSiteUrl.TrimEnd('/')); // MUST be the page’s site, not the tenant root
await PrintWhereWeAreAsync(http, baseUri);
var pagePath = await ResolvePagePathAcrossLibrariesAsync(http, baseUri, "First-Test-Page.aspx");
