using SmartStylist.Modules.Catalog;
using SmartStylist.Modules.Catalog.Catalogue;
using SmartStylist.Modules.Catalog.Reporting;

namespace SmartStylist.Worker;

internal static class ReportPrinter
{
    public static void Print(IReadOnlyList<ShopQuality> rows, TextWriter output)
    {
        output.WriteLine();
        output.WriteLine($"{"Shop",-20} {"Grade",-9} {"Strategy",-14} {"Products",8} {"Pages ok",8} {"Sizes",6} {"Stock",6} {"On sale",7} {"Days",4} {"Moved",5}");
        output.WriteLine(new string('-', 96));
        foreach (var row in rows.OrderByDescending(r => r.Enabled).ThenByDescending(r => r.Grade).ThenBy(r => r.ShopKey))
        {
            output.WriteLine(
                $"{row.ShopKey + (row.Enabled ? "" : " (off)"),-20} {row.Grade,-9} {row.Strategy?.ToString() ?? "-",-14} {row.ProductsTracked,8} " +
                $"{Percent(row.PageSuccessRate),8} {Percent(row.SizeCoverage),6} {Percent(row.StockCoverage),6} " +
                $"{Percent(row.OnSaleShare),7} {row.DaysOfHistory,4} {row.ProductsWithPriceChange,5}");
        }

        var failing = rows.Where(r => r.Enabled && r.Grade is DataGrade.Failing or DataGrade.Poor && r.LastError is not null).ToList();
        if (failing.Count > 0)
        {
            output.WriteLine();
            output.WriteLine("Why shops failed:");
            foreach (var row in failing)
            {
                output.WriteLine($"  {row.ShopKey}: {row.LastError}");
            }
        }

        output.WriteLine();
        output.WriteLine("Pages ok = product pages that gave a price. Sizes/Stock = share of prices with a size/stock flag.");
        output.WriteLine("On sale = share with a crossed-out price. Moved = products whose price changed across days.");
    }

    /// <summary>The quality catalogue: one line per brand with its best working price source, then what would add one.</summary>
    public static void Print(IReadOnlyList<BrandCoverage> brands, TextWriter output)
    {
        output.WriteLine();
        output.WriteLine($"{"Brand",-20} {"For",-4} {"Best working source",-32} Products priced, last {CatalogueReport.Days} days");
        output.WriteLine(new string('-', 100));
        foreach (var row in brands.OrderBy(b => b.Best?.Route ?? (PriceRoute)99).ThenBy(b => b.Brand.Name))
        {
            var forWhom = string.Concat(row.Brand.For.Select(a => a.ToString()[0]));
            var best = row.Best is null ? "none yet" : $"{(int)row.Best.Route} {row.Best.Route} via {row.Best.Shop}";
            var priced = row.ProductsPriced.Count == 0 ? "-" : string.Join(", ", row.ProductsPriced.Select(p => $"{p.Key} {p.Value}"));
            output.WriteLine($"{row.Brand.Name,-20} {forWhom,-4} {best,-32} {priced}");
        }

        output.WriteLine();
        output.WriteLine("Sources, best first: 1 Feed (Awin, Shopify, Uniqlo API), 2 PageData (crawl or Chrome watch), 3 SaleEmail, 4 Extension.");
        var waiting = brands.SelectMany(b => b.Routes.Where(r => r.State == RouteState.Pending && (b.Best is null || r.Route < b.Best.Route))
                .Select(r => $"  {b.Brand.Name}: {r.Shop}, {r.Detail}"))
            .ToList();
        if (waiting.Count > 0)
        {
            output.WriteLine("Waiting, and better than what works today:");
            waiting.ForEach(output.WriteLine);
        }

        var untried = brands.Where(b => b.Best is null || b.Best.Route > PriceRoute.SaleEmail)
            .SelectMany(b => b.Routes.Where(r => r.State is RouteState.NotTried or RouteState.NotSetUp).Select(r => r.Shop))
            .Distinct()
            .Order()
            .ToList();
        if (untried.Count > 0)
        {
            output.WriteLine($"Shops to try (detect, probe, sign up to the newsletter or save a page with the extension): {string.Join(", ", untried)}");
        }
    }

    private static string Percent(double share) => $"{share:P0}";
}
