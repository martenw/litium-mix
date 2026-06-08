using System.Text.Json;
using JetBrains.Annotations;
using Litium.Globalization;
using Litium.Products;
using Litium.Products.StockStatusCalculator;
using Litium.Runtime.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Litium.Accelerator.Decorators;

/// <summary>
///     Calculate quantity in stock for a bundle based on the stock of its bundled items.
/// </summary>
/// <param name="parent"></param>
/// <param name="variantService"></param>
[UsedImplicitly]
[ServiceDecorator(typeof(IStockStatusCalculator))]
public class StockStatusCalculatorDecorator(
    ILogger<StockStatusCalculatorDecorator> logger,
    IStockStatusCalculator parent,
    VariantService variantService,
    CountryService countryService
) : IStockStatusCalculator
{
    private readonly List<IsBundleLookupItem> _isBundleLookup = [];

    public IDictionary<Guid, StockStatusCalculatorResult> GetStockStatuses(
        StockStatusCalculatorArgs calculatorArgs,
        params StockStatusCalculatorItemArgs[] calculatorItemArgs)
    {
        // Default stock calculator to sweden if no country specified, to avoid issues with missing inventory system ids
        // when called from job/service without country context.
        //if (!calculatorArgs.CountrySystemId.HasValue || calculatorArgs.CountrySystemId == Guid.Empty)
        //{
        //    calculatorArgs.CountrySystemId = countryService.Get("SE")?.SystemId ?? Guid.Empty;
        //}

        // Get stock status from Litium for all items first
        var stockStatuses = parent.GetStockStatuses(calculatorArgs, calculatorItemArgs);

        if (stockStatuses.Count != calculatorItemArgs.Length)
        {
            logger.LogWarning("Mismatch between requested stock statuses and Litium returned stock statuses. Requested: {RequestedCount} ({Requested}), Returned: {ReturnedCount} ({Returned})",
                calculatorItemArgs.Length, JsonSerializer.Serialize(calculatorItemArgs), stockStatuses.Count, JsonSerializer.Serialize(stockStatuses));
        }

        foreach (var stockStatus in stockStatuses)
        {
            // For bundle items, adjust stock status to reflect the stock balance of the bundled items
            if (IsBundle(stockStatus.Key))
            {
                var calculatedBundleStock = GetBundleInStockQuantity(calculatorArgs, stockStatus.Key);
                if (calculatedBundleStock.Quantity.Equals(stockStatus.Value.InStockQuantity))
                {
                    continue;
                }

                logger.LogDebug("Bundle variant '{ArticleNumber}' ({VariantSystemId}) stock quantity adjusted from stored:'{OldQuantity}' to calculated:'{NewQuantity}' based on {BundledItemStock}",
                    calculatedBundleStock.Variant?.Id,
                    stockStatus.Key,
                    stockStatus.Value.InStockQuantity,
                    calculatedBundleStock.Quantity,
                    JsonSerializer.Serialize(calculatedBundleStock.BundledItemStock));
                stockStatus.Value.InStockQuantity = calculatedBundleStock.Quantity;
            }
        }

        return stockStatuses;
    }

    public ICollection<Inventory> GetInventories(StockStatusCalculatorArgs calculatorArgs)
    {
        return parent.GetInventories(calculatorArgs);
    }

    private CalculatedBundleStock GetBundleInStockQuantity(StockStatusCalculatorArgs calculatorArgs, Guid bundleVariantSystemId)
    {
        string? variantId = null;

        try
        {
            var bundleVariant = variantService.Get(bundleVariantSystemId);
            variantId = bundleVariant?.Id;
            var bundledItemStock = new List<CalculatedBundleStock.CalculatedBundleStockBundledItem>();

            // Create a list of all items that make up this bundle to look up their stock statuses
            var bundleItemArgs = bundleVariant.BundledVariants
                .Select(bv => new StockStatusCalculatorItemArgs
                {
                    VariantSystemId = bv.BundledVariantSystemId,
                    Quantity = 1
                }).ToArray();

            var bundledItemStockStatuses = parent.GetStockStatuses(calculatorArgs, bundleItemArgs);

            // Next, find the lowest stock quantity among bundled items
            decimal? bundleInStockQuantity = null;
            foreach (var bundleItemStockStatus in bundledItemStockStatuses)
            {
                var bundleItemInStockQuantity = bundleItemStockStatus.Value?.InStockQuantity ?? 0;
                if (bundleItemInStockQuantity == 0)
                {
                    // Item in bundle is out of stock, so the whole bundle is out of stock
                    bundleInStockQuantity = 0;
                }

                // Next, check how many bundles that can be created for this item
                var quantityInBundle = bundleVariant.BundledVariants
                    .Where(bv => bv.BundledVariantSystemId == bundleItemStockStatus.Key)
                    .Select(bv => bv.Quantity)
                    .FirstOrDefault();

                var bundlesPossibleForItem = Math.Floor(bundleItemInStockQuantity / quantityInBundle);

                bundledItemStock.Add(new CalculatedBundleStock.CalculatedBundleStockBundledItem
                {
                    Variant = bundleItemStockStatus.Key,
                    InStock = bundleItemStockStatus.Value?.InStockQuantity ?? 0,
                    InBundle = quantityInBundle,
                    PossibleBundles = bundlesPossibleForItem
                });

                // Adjust if first item or if fewer bundles possible than previous items
                if (bundleInStockQuantity == null || bundlesPossibleForItem < bundleInStockQuantity)
                    bundleInStockQuantity = bundlesPossibleForItem;
            }

            if (bundleInStockQuantity is null or <= 0)
                logger.LogDebug("Bundle variant '{ArticleNumber}' is out of stock (Quantity = '{Quantity}') based on {BundledItemsStock}",
                    bundleVariant.Id,
                    bundleInStockQuantity,
                    JsonSerializer.Serialize(bundledItemStockStatuses));

            return new CalculatedBundleStock
            {
                Variant = bundleVariant,
                Quantity = bundleInStockQuantity ?? 0,
                BundledItemStock = bundledItemStock
            };
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Error calculating stock quantity for bundle variant '{VariantId}' ({VariantSystemId})'",
                variantId,
                bundleVariantSystemId);

            return new CalculatedBundleStock
            {
                Variant = null,
                Quantity = 0
            };
        }
    }

    /// <summary>
    ///     Check if the variant is a bundle and cache the result for one hour.
    /// </summary>
    private bool IsBundle(Guid variantSystemId)
    {
        var lookupItem = _isBundleLookup.FirstOrDefault(i => i.VariantSystemId == variantSystemId);
        if (lookupItem != null && lookupItem.ValidTo > DateTime.Now)
            return lookupItem.IsBundle;

        var variant = variantService.Get(variantSystemId);
        if (variant == null)
            return false;

        var isBundle = variant.BundledVariants != null && variant.BundledVariants.Any();
        _isBundleLookup.Add(new IsBundleLookupItem
        {
            IsBundle = isBundle,
            ValidTo = DateTime.Now.AddHours(1),
            VariantSystemId = variantSystemId
        });

        return isBundle;
    }

    internal class IsBundleLookupItem
    {
        public Guid VariantSystemId { get; set; }
        public bool IsBundle { get; set; }
        public DateTime ValidTo { get; set; }
    }
}

internal class CalculatedBundleStock
{
    public Variant? Variant { get; set; }
    public decimal Quantity { get; set; }
    public List<CalculatedBundleStockBundledItem> BundledItemStock { get; set; } = [];

    internal class CalculatedBundleStockBundledItem
    {
        public Guid Variant { get; set; }
        public decimal InStock { get; set; }
        public decimal InBundle { get; set; }
        public decimal PossibleBundles { get; set; }
    }
}