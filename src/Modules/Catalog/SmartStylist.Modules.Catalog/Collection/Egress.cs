using System.Collections.Concurrent;
using System.Net;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace SmartStylist.Modules.Catalog.Collection;

/// <summary>
/// A way out to the internet from another country, e.g. a small US VPS running an HTTP proxy. Used only for shops
/// whose ShopOptions.Egress names it, because some shops block or redirect visitors from Georgia (geo, not bots).
/// </summary>
public sealed class EgressOptions
{
    /// <summary>HTTP proxy address, e.g. http://203.0.113.10:3128. Keep it and the password in user-secrets.</summary>
    public string? ProxyUrl { get; set; }

    public string? Username { get; set; }

    public string? Password { get; set; }

    /// <summary>Slowest pace for shops going through this egress; the proxy is one IP, so be gentle.</summary>
    public int MinDelayMs { get; set; } = 6000;
}

/// <summary>Plain-HTTP fetchers that go through a configured egress proxy, one per egress name.</summary>
internal sealed class EgressFetchers(IOptions<CatalogOptions> options)
{
    private readonly ConcurrentDictionary<string, HttpPageFetcher> _fetchers = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Tests swap the proxy handler for a fake one.</summary>
    internal Func<EgressOptions, HttpMessageHandler>? HandlerFactory { get; set; }

    public EgressOptions? Options(string name) =>
        options.Value.Egress.TryGetValue(name, out var egress) && !string.IsNullOrWhiteSpace(egress.ProxyUrl) ? egress : null;

    /// <summary>The fetcher for this egress, or null when it is not configured.</summary>
    public HttpPageFetcher? Get(string name)
    {
        var egress = Options(name);
        if (egress is null)
        {
            return null;
        }

        return _fetchers.GetOrAdd(name, _ =>
        {
            var handler = HandlerFactory?.Invoke(egress) ?? new SocketsHttpHandler
            {
                AutomaticDecompression = DecompressionMethods.All,
                PooledConnectionLifetime = TimeSpan.FromMinutes(5),
                UseProxy = true,
                Proxy = new WebProxy(egress.ProxyUrl)
                {
                    Credentials = string.IsNullOrEmpty(egress.Username) ? null : new NetworkCredential(egress.Username, egress.Password),
                },
            };
            var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(45) };
            client.DefaultRequestHeaders.UserAgent.ParseAdd(options.Value.UserAgent);
            client.DefaultRequestHeaders.Accept.ParseAdd("text/html,application/xhtml+xml,application/xml;q=0.9,application/json;q=0.8,*/*;q=0.7");
            client.DefaultRequestHeaders.AcceptLanguage.ParseAdd("en-US,en;q=0.9");
            return new HttpPageFetcher(client);
        });
    }
}

/// <summary>
/// Tries the home connection first and switches to the shop's egress for the rest of the run when the shop
/// redirects to another country's site or blocks the page. Without an egress it reports what to set.
/// </summary>
internal sealed class EgressSwitchingFetcher(IPageFetcher direct, IPageFetcher? egress, string shopKey, string egressName, ILogger logger) : IPageFetcher
{
    private bool _useEgress;

    public bool SwitchedToEgress => _useEgress;

    public Task<string> GetPageAsync(Uri url, CancellationToken cancellationToken) =>
        SendAsync(f => f.GetPageAsync(url, cancellationToken), url);

    public Task<byte[]> GetFileAsync(Uri url, CancellationToken cancellationToken) =>
        SendAsync(f => f.GetFileAsync(url, cancellationToken), url);

    private async Task<T> SendAsync<T>(Func<IPageFetcher, Task<T>> send, Uri url)
    {
        if (_useEgress && egress is not null)
        {
            return await send(egress);
        }

        try
        {
            return await send(direct);
        }
        catch (HttpRequestException ex) when (ex is GeoRedirectException or BlockedByBotWallException)
        {
            if (egress is null)
            {
                throw new HttpRequestException(
                    $"{ex.Message} {shopKey} is set to use the '{egressName}' egress, but Catalog:Egress:{egressName}:ProxyUrl is empty.", ex);
            }

            logger.LogInformation("{Shop}: {Reason}; switching to the {Egress} egress for this run", shopKey, ex.Message, egressName);
            _useEgress = true;
            return await send(egress);
        }
    }
}

/// <summary>The shop sent us to another country's site (brooksbrothers.com → brooksbrothers.eu). Geo, not a bot check.</summary>
public sealed class GeoRedirectException(Uri requested, Uri landed)
    : HttpRequestException($"Geo redirect: {requested.Host} sent this visitor to {landed.Host}{landed.AbsolutePath}. Use an egress in the shop's country.")
{
    public Uri Requested { get; } = requested;

    public Uri Landed { get; } = landed;

    /// <summary>Same shop name on another country domain, or a changed two-letter country segment (/US/ → /GE/).</summary>
    public static bool IsGeoRedirect(Uri requested, Uri landed)
    {
        static string Bare(string host) => host.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ? host[4..] : host;
        static string Name(string host) => Bare(host).Split('.')[0];
        static string? Country(Uri uri)
        {
            var first = uri.AbsolutePath.Trim('/').Split('/')[0];
            return first.Length == 2 && first.All(char.IsLetter) ? first.ToUpperInvariant() : null;
        }

        var requestedHost = Bare(requested.Host);
        var landedHost = Bare(landed.Host);
        if (!string.Equals(requestedHost, landedHost, StringComparison.OrdinalIgnoreCase))
        {
            return string.Equals(Name(requestedHost), Name(landedHost), StringComparison.OrdinalIgnoreCase);
        }

        var from = Country(requested);
        var to = Country(landed);
        return from is not null && to is not null && from != to;
    }
}
