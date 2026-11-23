namespace SmartStylist.Modules.Catalog.Domain;

/// <summary>The e-commerce platform a shop runs on. It decides which adapter can read its prices.</summary>
public enum ShopPlatform
{
    Unknown,
    Shopify,
    SalesforceCommerceCloud,
    Magento,
    WooCommerce,
    BigCommerce,
    PrestaShop,
    NextJs,
    Nuxt,
}
