using System;
using System.Collections.Generic;
using System.Linq;
using JetBrains.Annotations;
using Litium.Products;
using Litium.Products.StockStatusCalculator;
using Litium.Runtime.DependencyInjection;

namespace Litium.Accelerator.Bft.Decorators;

/// <summary>
/// Calculate quantity in stock for a bundle based on the stock of its bundled items.
/// </summary>
/// <param name="parent"></param>
/// <param name="variantService"></param>
[UsedImplicitly]
[ServiceDecorator(typeof(IStockStatusCalculator))]
public class StockStatusCalculatorDecorator(
    IStockStatusCalculator parent,
    VariantService variantService
) : IStockStatusCalculator
{
    private readonly List<IsBundleLookupItem> _isBundleLookup = [];

    public IDictionary<Guid, StockStatusCalculatorResult> GetStockStatuses(
        StockStatusCalculatorArgs calculatorArgs,
        params StockStatusCalculatorItemArgs[] calculatorItemArgs)
    {
        var result = new Dictionary<Guid, StockStatusCalculatorResult>();
        foreach (var calculatorItemArg in calculatorItemArgs)
        {
            var stockStatuses = parent.GetStockStatuses(calculatorArgs, calculatorItemArgs);
            foreach (var stockStatus in stockStatuses)
            {
                if (IsBundle(calculatorItemArg.VariantSystemId))
                {
                    stockStatus.Value.InStockQuantity = GetBundleInStockQuantity(calculatorArgs, calculatorItemArg);
                }

                result.Add(stockStatus.Key, stockStatus.Value);
            }
        }

        return result;
    }

    public ICollection<Inventory> GetInventories(StockStatusCalculatorArgs calculatorArgs)
    {
        return parent.GetInventories(calculatorArgs);
    }

    private decimal GetBundleInStockQuantity(StockStatusCalculatorArgs calculatorArgs, StockStatusCalculatorItemArgs calculatorItemArg)
    {
        var bundleVariant = variantService.Get(calculatorItemArg.VariantSystemId);

        // Create a list of all items that make up this bundle to look up their stock statuses
        var bundleItemArgs = bundleVariant.BundledVariants
            .Select(bv => new StockStatusCalculatorItemArgs
            {
                VariantSystemId = bv.BundledVariantSystemId,
                Quantity = 1
            }).ToArray();

        var bundledItemStockStatuses = parent.GetStockStatuses(calculatorArgs, bundleItemArgs);

        // Next, find lowest stock quantity among bundled items
        decimal? bundleInStockQuantity = null;
        foreach (var bundleItemStockStatus in bundledItemStockStatuses)
        {
            var bundleItemInStockQuantity = bundleItemStockStatus.Value?.InStockQuantity ?? 0;
            if (bundleItemInStockQuantity == 0)
                continue;

            // Next, check how many bundles that can be created for this item
            var quantityInBundle = bundleVariant.BundledVariants
                .Where(bv => bv.BundledVariantSystemId == bundleItemStockStatus.Key)
                .Select(bv => bv.Quantity)
                .FirstOrDefault();

            var bundlesPossibleForItem = Math.Floor(bundleItemInStockQuantity / quantityInBundle);

            // Adjust if first item or if fewer bundles possible than previous items
            if (bundleInStockQuantity == null || bundlesPossibleForItem < bundleInStockQuantity)
            {
                bundleInStockQuantity = bundlesPossibleForItem;
            }
        }

        return bundleInStockQuantity ?? 0;
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