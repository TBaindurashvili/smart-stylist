using System.Net;

namespace SmartStylist.Modules.Catalog.Collection;

/// <summary>
/// Plain HTTP requests to shops. A shop with an Egress goes out through it (a US proxy); a page that redirects to the
/// shop's site for another country is retried once through Catalog:GeoRetryEgress, or reported as geo-blocked.
/// </summary>
internal sealed class HttpPageFetcher(HttpClient httpClient, EgressRouter egress) : IPageFetcher
{
    public const string ClientName = "catalog";

    /// <summary>Longest we wait when a shop answers 429 Too Many Requests before trying once more.</summary>
    private static readonly TimeSpan MaxRetryAfter = TimeSpan.FromSeconds(30);

    public async Task<string> GetPageAsync(Uri url, CancellationToken cancellationToken)
    {
        using var response = await SendAsync(url, cancellationToken);
        return await response.Content.ReadAsStringAsync(cancellationToken);
    }

    public async Task<byte[]> GetFileAsync(Uri url, CancellationToken cancellationToken)
    {
        using var response = await SendAsync(url, cancellationToken);
        return await response.Content.ReadAsByteArrayAsync(cancellationToken);
    }

    private async Task<HttpResponseMessage> SendAsync(Uri url, CancellationToken cancellationToken)
    {
        var route = egress.EgressFor(url);
        var response = await SendViaAsync(route, url, cancellationToken);
        if (response.IsSuccessStatusCode && GeoRedirect.Detect(url, response.RequestMessage?.RequestUri) is { } landed)
        {
            response.Dispose();
            if (route is not null || egress.GeoRetryEgress is not { } retry)
            {
                throw new GeoRedirectedException(url, landed);
            }

            route = retry;
            response = await SendViaAsync(route, url, cancellationToken);
            if (response.IsSuccessStatusCode && GeoRedirect.Detect(url, response.RequestMessage?.RequestUri) is { } again)
            {
                response.Dispose();
                throw new GeoRedirectedException(url, again);
            }
        }

        if (response.IsSuccessStatusCode)
        {
            return response;
        }

        using (response)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            var vendor = BotWall.Detect((int)response.StatusCode, Headers(response), body);
            if (vendor is not null)
            {
                throw new BlockedByBotWallException(vendor, (int)response.StatusCode, url);
            }

            response.EnsureSuccessStatusCode();
            return response;
        }
    }

    private async Task<HttpResponseMessage> SendViaAsync(string? route, Uri url, CancellationToken cancellationToken)
    {
        var response = await GetAsync(route, url, cancellationToken);
        if (response.StatusCode == HttpStatusCode.TooManyRequests)
        {
            // Honour the shop's rate limit once; a second 429 means it does not want us right now.
            var wait = RetryAfter(response) ?? TimeSpan.FromSeconds(10);
            response.Dispose();
            await Task.Delay(wait < MaxRetryAfter ? wait : MaxRetryAfter, cancellationToken);
            response = await GetAsync(route, url, cancellationToken);
        }

        return response;
    }

    private Task<HttpResponseMessage> GetAsync(string? route, Uri url, CancellationToken cancellationToken) =>
        route is null ? httpClient.GetAsync(url, cancellationToken) : egress.GetAsync(route, url, cancellationToken);

    private static Dictionary<string, string> Headers(HttpResponseMessage response) =>
        response.Headers.Concat(response.Content.Headers)
            .GroupBy(h => h.Key, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => string.Join(", ", g.First().Value), StringComparer.OrdinalIgnoreCase);

    private static TimeSpan? RetryAfter(HttpResponseMessage response) =>
        response.Headers.RetryAfter switch
        {
            { Delta: { } delta } => delta,
            { Date: { } date } => date - DateTimeOffset.UtcNow,
            _ => null,
        };
}
