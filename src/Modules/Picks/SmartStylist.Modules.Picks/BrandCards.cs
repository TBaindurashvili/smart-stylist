using SmartStylist.Modules.Catalog;
using SmartStylist.Modules.Catalog.Catalogue;
using SmartStylist.Modules.Wardrobe.Style;

namespace SmartStylist.Modules.Picks;

/// <summary>Hands the quality catalogue's brands to the style quiz (Catalog and Wardrobe don't reference each other).</summary>
public static class BrandCards
{
    public static IReadOnlyList<BrandCard> From(QualityCatalogue catalogue) => catalogue.Brands.Select(Of).ToList();

    public static BrandCard Of(BrandOptions brand) =>
        new(brand.Name, brand.Styles, brand.Categories, brand.For.Contains(Audience.Men), brand.For.Contains(Audience.Women));
}
