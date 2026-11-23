using System.Net;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using SmartStylist.Modules.Catalog.Newsletters;

namespace SmartStylist.Modules.Catalog.Collection;

/// <summary>
/// Sends a shop's plain HTTP requests out through another place on the internet (Catalog:Egresses), such as a US
/// proxy or a small US VPS, for shops that block or redirect visitors from Georgia. Only shops that name an egress
/// use it, each egress is rate-limited across all its shops, and nothing else changes: same requests, same headers.
/// </summary>
public sealed class EgressRouter : IDisposable
{
    private readonly CatalogOptions _options;
    private readonly IReadOnlyList<(ShopDomains Domains, string Egress)> _routes;
    private readonly Func<string, EgressOptions, HttpMessageHandler> _handlerFactory;
    private readonly Dictionary<string, (HttpClient Client, SemaphoreSlim Gate)> _clients = [];
    private readonly Dictionary<string, DateTimeOffset> _lastRequest = [];
    private readonly Lock _lock = new();

    public EgressRouter(IOptions<CatalogOptions> options)
        : this(options, DefaultHandler)
    {
    }

    /// <param name="handlerFactory">Builds the handler for an egress; tests swap in a fake web.</param>
    internal EgressRouter(IOptions<CatalogOptions> options, Func<string, EgressOptions, HttpMessageHandler> handlerFactory)
    {
        _options = options.Value;
        _handlerFactory = handlerFactory;
        _routes = _options.Shops
            .Where(s => !string.IsNullOrWhiteSpace(s.Egress))
            .Select(s => (new ShopDomains(s.Key, s.BaseUrl, []), s.Egress!.Trim()))
            .ToList();
    }

    /// <summary>The egress this page goes through, or null for straight from this machine.</summary>
    public string? EgressFor(Uri url) => _routes.FirstOrDefault(r => r.Domains.IsShopPage(url.Host)).Egress;

    /// <summary>The egress to retry a geo-redirected page through, when one is set up.</summary>
    public string? GeoRetryEgress =>
        _options.GeoRetryEgress is { Length: > 0 } name && IsConfigured(name) ? name : null;

    public bool IsConfigured(string egress) =>
        _options.Egresses.TryGetValue(egress, out var options) && !string.IsNullOrWhiteSpace(options.ProxyUrl);

    /// <summary>Sends a GET through the egress, after waiting out its rate limit.</summary>
    public async Task<HttpResponseMessage> GetAsync(string egress, Uri url, CancellationToken cancellationToken)
    {
        if (!_options.Egresses.TryGetValue(egress, out var options) || string.IsNullOrWhiteSpace(options.ProxyUrl))
        {
            throw new EgressNotConfiguredException(egress, url);
        }

        var (client, gate) = Client(egress, options);
        await gate.WaitAsync(cancellationToken);
        try
        {
            if (_lastRequest.TryGetValue(egress, out var last))
            {
                var wait = last.AddMilliseconds(options.MinIntervalMs) - DateTimeOffset.UtcNow;
                if (wait > TimeSpan.Zero)
                {
                    await Task.Delay(wait, cancellationToken);
                }
            }

            _lastRequest[egress] = DateTimeOffset.UtcNow;
            return await client.GetAsync(url, cancellationToken);
        }
        finally
        {
            gate.Release();
        }
    }

    private (HttpClient Client, SemaphoreSlim Gate) Client(string egress, EgressOptions options)
    {
        lock (_lock)
        {
            if (!_clients.TryGetValue(egress, out var entry))
            {
                var client = new HttpClient(_handlerFactory(egress, options)) { Timeout = TimeSpan.FromSeconds(45) };
                client.DefaultRequestHeaders.UserAgent.ParseAdd(_options.UserAgent);
                client.DefaultRequestHeaders.Accept.ParseAdd("text/html,application/xhtml+xml,application/xml;q=0.9,application/json;q=0.8,*/*;q=0.7");
                client.DefaultRequestHeaders.AcceptLanguage.ParseAdd("en-US,en;q=0.9");
                entry = (client, new SemaphoreSlim(1, 1));
                _clients[egress] = entry;
            }

            return entry;
        }
    }

    private static SocketsHttpHandler DefaultHandler(string egress, EgressOptions options)
    {
        var proxyUrl = new Uri(options.ProxyUrl!);
        var proxy = new WebProxy(new UriBuilder(proxyUrl) { UserName = "", Password = "" }.Uri);
        if (!string.IsNullOrEmpty(proxyUrl.UserInfo))
        {
            var parts = proxyUrl.UserInfo.Split(':', 2);
            proxy.Credentials = new NetworkCredential(Uri.UnescapeDataString(parts[0]), parts.Length > 1 ? Uri.UnescapeDataString(parts[1]) : "");
        }

        return new SocketsHttpHandler
        {
            Proxy = proxy,
            UseProxy = true,
            AutomaticDecompression = DecompressionMethods.All,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        };
    }

    public void Dispose()
    {
        foreach (var (client, gate) in _clients.Values)
        {
            client.Dispose();
            gate.Dispose();
        }
    }
}

/// <summary>
/// Recognises a shop sending visitors to its site for another country: brooksbrothers.com to brooksbrothers.eu,
/// www.patagonia.com to eu.patagonia.com, or /us/... to a page without the US locale.
/// </summary>
public static partial class GeoRedirect
{
    /// <summary>Where the shop sent the request when that is a country site, or null.</summary>
    public static Uri? Detect(Uri requested, Uri? landed)
    {
        if (landed is null || !landed.IsAbsoluteUri)
        {
            return null;
        }

        var from = Strip(requested.Host);
        var to = Strip(landed.Host);
        if (!string.Equals(from, to, StringComparison.OrdinalIgnoreCase))
        {
            // Same brand on another host is a country site; another brand (a CDN, a login page) is not.
            var brand = Brand(from);
            return to.Split('.').Contains(brand, StringComparer.OrdinalIgnoreCase) ? landed : null;
        }

        var locale = UsLocale().Match(requested.AbsolutePath);
        return locale.Success && !landed.AbsolutePath.StartsWith(locale.Value, StringComparison.OrdinalIgnoreCase) ? landed : null;
    }

    private static string Strip(string host) => host.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ? host[4..] : host;

    /// <summary>"brooksbrothers" in brooksbrothers.com, "levi" in levi.com, "patagonia" in shop.patagonia.co.uk.</summary>
    private static string Brand(string host)
    {
        var labels = host.Split('.');
        if (labels.Length >= 3 && labels[^2] is "co" or "com" or "org" or "net" && labels[^1].Length == 2)
        {
            return labels[^3];
        }

        return labels.Length >= 2 ? labels[^2] : labels[0];
    }

    [GeneratedRegex(@"^/(us|en-us|en_us|us/en|us/en_us)(?=/|$)", RegexOptions.IgnoreCase)]
    private static partial Regex UsLocale();
}

/// <summary>The shop sent the request to its site for another country: blocked by geography, not by a bot check.</summary>
public sealed class GeoRedirectedException(Uri requested, Uri landed)
    : HttpRequestException(
        $"Redirected from {requested.Host} to {landed.Host}{landed.AbsolutePath} (a country site): blocked by geography, not a bot check. " +
        "Route this shop through a US egress (Catalog:Egresses and the shop's Egress, or Catalog:GeoRetryEgress).")
{
    public Uri Landed { get; } = landed;
}

/// <summary>A shop names an egress that has no proxy address yet.</summary>
public sealed class EgressNotConfiguredException(string egress, Uri url)
    : HttpRequestException(
        $"{url.Host} goes through the '{egress}' egress, which is not set up: set Catalog__Egresses__{egress}__ProxyUrl " +
        "(e.g. socks5://127.0.0.1:1080 from \"ssh -D 1080 my-us-vps\").")
{
    public string Egress { get; } = egress;
}
