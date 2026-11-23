using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Playwright;

namespace SmartStylist.Modules.Catalog.Collection;

/// <summary>
/// Renders pages in a browser for shops that block plain HTTP clients.
/// By default a headless Chromium with a fresh context per shop. With Catalog:Browser:UserDataDir set it drives a
/// visible browser (Channel "chrome" = the installed Google Chrome) with a profile kept between runs, at a person's pace.
/// A bot-protection page stops the request: the app never solves a challenge or CAPTCHA and never logs in.
/// </summary>
internal sealed class BrowserPageFetcher(IOptions<CatalogOptions> options, ILogger<BrowserPageFetcher> logger)
    : IPageFetcher, IAsyncDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private IPlaywright? _playwright;
    private IBrowser? _browser;
    private IBrowserContext? _context;

    private bool Persistent => !string.IsNullOrWhiteSpace(options.Value.Browser.UserDataDir);

    public async Task<string> GetPageAsync(Uri url, CancellationToken cancellationToken)
    {
        var context = await GetContextAsync();
        var page = await context.NewPageAsync();
        try
        {
            var response = await page.GotoAsync(url.ToString(), new PageGotoOptions
            {
                WaitUntil = WaitUntilState.DOMContentLoaded,
                Timeout = options.Value.Browser.NavigationTimeoutMs,
            });

            // Give client-rendered shops a moment to inject their JSON-LD.
            await page.WaitForTimeoutAsync(Math.Max(options.Value.Browser.PageSettleMs, 0));
            cancellationToken.ThrowIfCancellationRequested();
            var html = await page.ContentAsync();

            var status = response?.Status ?? 200;
            var vendor = BotWall.Detect(status, response?.Headers ?? new Dictionary<string, string>(), html);
            if (vendor is not null)
            {
                logger.LogWarning("{Host}: blocked by {Vendor} (HTTP {Status}); leaving the page alone", url.Host, vendor, status);
                throw new BlockedByBotWallException(vendor, status, url);
            }

            if (response is { Ok: false })
            {
                throw new HttpRequestException($"Browser got HTTP {response.Status} for {url}");
            }

            return html;
        }
        finally
        {
            await page.CloseAsync();
        }
    }

    public async Task<byte[]> GetFileAsync(Uri url, CancellationToken cancellationToken)
    {
        var context = await GetContextAsync();
        var response = await context.APIRequest.GetAsync(url.ToString());
        if (!response.Ok)
        {
            throw new HttpRequestException($"Browser got HTTP {response.Status} for {url}");
        }

        return await response.BodyAsync();
    }

    /// <summary>Drops cookies and storage so the next shop starts clean. A kept profile is left as it is, like a person's browser.</summary>
    public async Task ResetAsync()
    {
        await _gate.WaitAsync();
        try
        {
            if (_context is not null && !Persistent)
            {
                await _context.CloseAsync();
                _context = null;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<IBrowserContext> GetContextAsync()
    {
        await _gate.WaitAsync();
        try
        {
            if (_context is not null)
            {
                return _context;
            }

            var browserOptions = options.Value.Browser;
            var channel = string.IsNullOrWhiteSpace(browserOptions.Channel) ? null : browserOptions.Channel;
            var executable = string.IsNullOrWhiteSpace(browserOptions.ExecutablePath) ? null : browserOptions.ExecutablePath;
            _playwright ??= await Playwright.CreateAsync();

            if (Persistent)
            {
                // A real browser window with its own lasting profile. The shop's own user agent and fingerprint are
                // left as the browser reports them; faking them is the "aggressive evasion" we stay away from.
                var profile = browserOptions.UserDataDir!.StartsWith('~')
                    ? Path.Join(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), browserOptions.UserDataDir[1..].TrimStart('/', '\\'))
                    : Path.GetFullPath(browserOptions.UserDataDir!);
                Directory.CreateDirectory(profile);
                logger.LogInformation("Starting {Browser} with profile {Profile} (headless: {Headless})", channel ?? "Chromium", profile, browserOptions.Headless);
                _context = await Launch(() => _playwright.Chromium.LaunchPersistentContextAsync(profile, new BrowserTypeLaunchPersistentContextOptions
                {
                    Headless = browserOptions.Headless,
                    Channel = channel,
                    ExecutablePath = executable,
                    Locale = "en-US",
                    ViewportSize = new ViewportSize { Width = 1366, Height = 900 },
                }));
                return _context;
            }

            if (_browser is null)
            {
                logger.LogInformation("Starting {Browser} for browser collection (headless: {Headless})", channel ?? "Chromium", browserOptions.Headless);
                _browser = await Launch(() => _playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
                {
                    Headless = browserOptions.Headless,
                    Channel = channel,
                    ExecutablePath = executable,
                }));
            }

            _context = await _browser.NewContextAsync(new BrowserNewContextOptions
            {
                UserAgent = options.Value.UserAgent,
                Locale = "en-US",
                ViewportSize = new ViewportSize { Width = 1366, Height = 900 },
            });
            return _context;
        }
        finally
        {
            _gate.Release();
        }
    }

    private static async Task<T> Launch<T>(Func<Task<T>> launch)
    {
        try
        {
            return await launch();
        }
        catch (PlaywrightException ex) when (ex.Message.Contains("Executable doesn't exist", StringComparison.Ordinal)
            || ex.Message.Contains("is not found at", StringComparison.Ordinal))
        {
            throw new CollectorUnusableException(
                "BrowserJsonLd: the browser is not installed. Run `dotnet run --project src/SmartStylist.Worker -- install-browser` once, " +
                "or install Google Chrome when Catalog:Browser:Channel is \"chrome\".", ex);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_context is not null)
        {
            await _context.CloseAsync();
        }

        if (_browser is not null)
        {
            await _browser.CloseAsync();
        }

        _playwright?.Dispose();
        _gate.Dispose();
    }
}
