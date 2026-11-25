using SmartStylist.Modules.Catalog;
using SmartStylist.Modules.Catalog.Catalogue;
using SmartStylist.Modules.Wardrobe.Style;

namespace SmartStylist.Web;

/// <summary>Hands the quality catalogue's brands to the style quiz (modules don't reference each other).</summary>
public static class BrandCards
{
    public static IReadOnlyList<BrandCard> From(QualityCatalogue catalogue) =>
        catalogue.Brands
            .Select(b => new BrandCard(b.Name, b.Styles, b.Categories, b.For.Contains(Audience.Men), b.For.Contains(Audience.Women)))
            .ToList();
}
