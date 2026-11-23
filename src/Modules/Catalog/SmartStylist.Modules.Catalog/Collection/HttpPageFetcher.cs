using System.Net;

namespace SmartStylist.Modules.Catalog.Collection;

internal sealed class HttpPageFetcher(HttpClient httpClient) : IPageFetcher
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
        var response = await httpClient.GetAsync(url, cancellationToken);
        if (response.StatusCode == HttpStatusCode.TooManyRequests)
        {
            // Honour the shop's rate limit once; a second 429 means it does not want us right now.
            var wait = RetryAfter(response) ?? TimeSpan.FromSeconds(10);
            response.Dispose();
            await Task.Delay(wait < MaxRetryAfter ? wait : MaxRetryAfter, cancellationToken);
            response = await httpClient.GetAsync(url, cancellationToken);
        }

        if (response.RequestMessage?.RequestUri is { } landed && GeoRedirectException.IsGeoRedirect(url, landed))
        {
            response.Dispose();
            throw new GeoRedirectException(url, landed);
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
