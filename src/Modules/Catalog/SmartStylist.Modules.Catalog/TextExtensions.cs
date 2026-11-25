namespace SmartStylist.Modules.Catalog;

internal static class TextExtensions
{
    /// <summary>Cuts a value to a column's maximum length.</summary>
    public static string Truncate(this string value, int max) => value.Length <= max ? value : value[..max];
}
