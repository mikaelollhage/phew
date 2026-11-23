using System.Globalization;
using System.Text.RegularExpressions;
using Phew.Core.Models;
using Phew.Core.Services;

namespace Phew.Infrastructure.Services;

public enum ShoppingImportQuantityMode
{
    QuantityFree = 0,
    WithQuantity = 1
}

public sealed class ShoppingImportSourceEntry
{
    public required string PreviewItemKey { get; init; }
    public required ShoppingListItem Item { get; init; }
    public string? SourceDaysSummary { get; init; }
    public string? AlreadyAddedSummary { get; init; }
    public bool IsAlreadyAdded { get; init; }
    public bool WasRemovedFromShoppingList { get; init; }
    public string? ShortStatus { get; init; }
    public string? ExistingInUncheckedText { get; init; }
    public string? ExistingInCheckedText { get; init; }
    public string? ExistingOverlapSummary { get; init; }
    public string? ConflictWarning { get; init; }

    public bool HasSourceQuantityOrUnit =>
        Item.Quantity.HasValue || !string.IsNullOrWhiteSpace(Item.Unit);
}

public sealed class ShoppingImportDisplayRow
{
    public required string RowKey { get; init; }
    public required string CanonicalKey { get; init; }
    public required string SortKey { get; init; }
    public required string DisplayName { get; init; }
    public decimal? Quantity { get; init; }
    public decimal? QuantityMax { get; init; }
    public string? Unit { get; init; }
    public string? Comment { get; init; }
    public required bool IncludeQuantity { get; init; }
    public required bool HasSourceQuantityOrUnit { get; init; }
    public required IReadOnlyList<ShoppingImportSourceEntry> Sources { get; init; }
    public string? SourceDaysSummary { get; init; }
    public string? AlreadyAddedSummary { get; init; }
    public bool IsAlreadyAdded { get; init; }
    public bool WasRemovedFromShoppingList { get; init; }
    public string? ShortStatus { get; init; }
    public string? ExistingInUncheckedText { get; init; }
    public string? ExistingInCheckedText { get; init; }
    public string? ExistingOverlapSummary { get; init; }
    public string? ConflictWarning { get; init; }

    public bool IsGrouped => Sources.Count > 1;
}

public static class ShoppingImportPreviewHelper
{
    private static readonly CultureInfo SwedishCulture = CultureInfo.GetCultureInfo("sv-SE");
    private static readonly StringComparer SwedishComparer = StringComparer.Create(SwedishCulture, ignoreCase: true);
    private static readonly Regex AfterAddSuffixRegex = new(@"\s*·\s*Efter tillägg:[^·]*", RegexOptions.Compiled);

    public static IReadOnlyList<ShoppingImportDisplayRow> BuildDisplayRows(
        IEnumerable<ShoppingImportSourceEntry> entries,
        IReadOnlySet<string> quantityAwareKeys,
        ShoppingListAggregator aggregator)
    {
        ArgumentNullException.ThrowIfNull(entries);
        ArgumentNullException.ThrowIfNull(quantityAwareKeys);
        ArgumentNullException.ThrowIfNull(aggregator);

        var rows = new List<ShoppingImportDisplayRow>();
        var quantityFreeGroups = new Dictionary<string, List<ShoppingImportSourceEntry>>(StringComparer.OrdinalIgnoreCase);

        foreach (var entry in entries)
        {
            var canonicalKey = aggregator.GetCanonicalIngredientKey(entry.Item.Name, entry.Item.Comment);
            var effectiveKey = string.IsNullOrWhiteSpace(canonicalKey) ? entry.PreviewItemKey : canonicalKey;

            if (quantityAwareKeys.Contains(entry.PreviewItemKey))
            {
                rows.Add(new ShoppingImportDisplayRow
                {
                    RowKey = entry.PreviewItemKey,
                    CanonicalKey = effectiveKey,
                    SortKey = effectiveKey,
                    DisplayName = entry.Item.Name,
                    Quantity = entry.Item.Quantity,
                    QuantityMax = entry.Item.QuantityMax,
                    Unit = entry.Item.Unit,
                    Comment = entry.Item.Comment,
                    IncludeQuantity = true,
                    HasSourceQuantityOrUnit = entry.HasSourceQuantityOrUnit,
                    Sources = [entry],
                    SourceDaysSummary = entry.SourceDaysSummary,
                    AlreadyAddedSummary = entry.AlreadyAddedSummary,
                    IsAlreadyAdded = entry.IsAlreadyAdded,
                    WasRemovedFromShoppingList = entry.WasRemovedFromShoppingList,
                    ShortStatus = entry.ShortStatus,
                    ExistingInUncheckedText = entry.ExistingInUncheckedText,
                    ExistingInCheckedText = entry.ExistingInCheckedText,
                    ExistingOverlapSummary = entry.ExistingOverlapSummary,
                    ConflictWarning = entry.ConflictWarning
                });
            }
            else
            {
                if (!quantityFreeGroups.TryGetValue(effectiveKey, out var group))
                {
                    group = new List<ShoppingImportSourceEntry>();
                    quantityFreeGroups[effectiveKey] = group;
                }
                group.Add(entry);
            }
        }

        foreach (var (canonicalKey, group) in quantityFreeGroups)
        {
            if (group.Count == 1)
            {
                var entry = group[0];
                rows.Add(new ShoppingImportDisplayRow
                {
                    RowKey = entry.PreviewItemKey,
                    CanonicalKey = canonicalKey,
                    SortKey = canonicalKey,
                    DisplayName = aggregator.FormatQuantityFreeName(entry.Item.Name),
                    Quantity = null,
                    QuantityMax = null,
                    Unit = null,
                    Comment = entry.Item.Comment,
                    IncludeQuantity = false,
                    HasSourceQuantityOrUnit = entry.HasSourceQuantityOrUnit,
                    Sources = [entry],
                    SourceDaysSummary = entry.SourceDaysSummary,
                    AlreadyAddedSummary = entry.AlreadyAddedSummary,
                    IsAlreadyAdded = entry.IsAlreadyAdded,
                    WasRemovedFromShoppingList = entry.WasRemovedFromShoppingList,
                    ShortStatus = entry.ShortStatus,
                    ExistingInUncheckedText = entry.ExistingInUncheckedText,
                    ExistingInCheckedText = entry.ExistingInCheckedText,
                    ExistingOverlapSummary = BuildQuantityFreeOverlapSummary(entry.ExistingOverlapSummary),
                    ConflictWarning = null
                });
            }
            else
            {
                var orderedSources = group
                    .OrderBy(s => s.Item.Name, SwedishComparer)
                    .ThenBy(s => s.Item.Unit ?? string.Empty, SwedishComparer)
                    .ThenBy(s => s.Item.Quantity ?? 0m)
                    .ThenBy(s => s.PreviewItemKey, StringComparer.Ordinal)
                    .ToList();

                var displayName = SelectGroupedQuantityFreeDisplayName(orderedSources, canonicalKey, aggregator);
                var existingUnchecked = orderedSources
                    .Select(s => s.ExistingInUncheckedText)
                    .FirstOrDefault(t => !string.IsNullOrWhiteSpace(t));
                var existingChecked = orderedSources
                    .Select(s => s.ExistingInCheckedText)
                    .FirstOrDefault(t => !string.IsNullOrWhiteSpace(t));
                var overlapSummary = orderedSources
                    .Select(s => BuildQuantityFreeOverlapSummary(s.ExistingOverlapSummary))
                    .FirstOrDefault(s => !string.IsNullOrWhiteSpace(s));

                rows.Add(new ShoppingImportDisplayRow
                {
                    RowKey = $"GROUP:{canonicalKey.ToLower(SwedishCulture)}",
                    CanonicalKey = canonicalKey,
                    SortKey = canonicalKey,
                    DisplayName = displayName,
                    Quantity = null,
                    QuantityMax = null,
                    Unit = null,
                    Comment = CombineDistinctComments(orderedSources.Select(s => s.Item.Comment)),
                    IncludeQuantity = false,
                    HasSourceQuantityOrUnit = orderedSources.Any(s => s.HasSourceQuantityOrUnit),
                    Sources = orderedSources,
                    SourceDaysSummary = CombineDistinctSummaries(orderedSources.Select(s => s.SourceDaysSummary)),
                    AlreadyAddedSummary = CombineDistinctSummaries(orderedSources.Select(s => s.AlreadyAddedSummary)),
                    IsAlreadyAdded = orderedSources.All(s => s.IsAlreadyAdded),
                    WasRemovedFromShoppingList = orderedSources.Any(s => s.WasRemovedFromShoppingList),
                    ShortStatus = orderedSources.Select(s => s.ShortStatus).FirstOrDefault(s => !string.IsNullOrWhiteSpace(s)),
                    ExistingInUncheckedText = existingUnchecked,
                    ExistingInCheckedText = existingChecked,
                    ExistingOverlapSummary = overlapSummary,
                    ConflictWarning = null
                });
            }
        }

        return rows
            .OrderBy(r => r.SortKey, SwedishComparer)
            .ThenBy(r => r.DisplayName, SwedishComparer)
            .ThenBy(r => r.IncludeQuantity)
            .ThenBy(r => r.Unit ?? string.Empty, SwedishComparer)
            .ThenBy(r => r.Quantity ?? 0m)
            .ThenBy(r => r.RowKey, StringComparer.Ordinal)
            .ToList();
    }

    public static ShoppingImportSourceEntry FromRecipeItem(RecipePreviewItemDto dto)
    {
        ArgumentNullException.ThrowIfNull(dto);
        return new ShoppingImportSourceEntry
        {
            PreviewItemKey = dto.PreviewItemKey,
            Item = dto.Item,
            ExistingInUncheckedText = dto.ExistingInUncheckedText,
            ExistingInCheckedText = dto.ExistingInCheckedText,
            ExistingOverlapSummary = dto.ExistingOverlapSummary,
            ConflictWarning = dto.ConflictWarning
        };
    }

    public static ShoppingImportSourceEntry FromWeeklyMenuItem(WeeklyMenuPreviewItemDto dto, string? shortStatus)
    {
        ArgumentNullException.ThrowIfNull(dto);
        return new ShoppingImportSourceEntry
        {
            PreviewItemKey = dto.PreviewItemKey,
            Item = dto.Item,
            SourceDaysSummary = dto.SourceDaysSummary,
            AlreadyAddedSummary = dto.AlreadyAddedSummary,
            IsAlreadyAdded = dto.IsAlreadyAdded,
            WasRemovedFromShoppingList = dto.WasRemovedFromShoppingList,
            ShortStatus = shortStatus,
            ExistingInUncheckedText = dto.ExistingInUncheckedText,
            ExistingInCheckedText = dto.ExistingInCheckedText,
            ExistingOverlapSummary = dto.ExistingOverlapSummary,
            ConflictWarning = dto.ConflictWarning
        };
    }

    private static string SelectGroupedQuantityFreeDisplayName(
        IReadOnlyList<ShoppingImportSourceEntry> sources,
        string canonicalKey,
        ShoppingListAggregator aggregator)
    {
        var firstTrimmed = sources[0].Item.Name.Trim();
        bool allSameIgnoreCase = sources.All(s =>
            string.Equals(s.Item.Name.Trim(), firstTrimmed, StringComparison.OrdinalIgnoreCase));

        if (allSameIgnoreCase)
        {
            return aggregator.FormatQuantityFreeName(firstTrimmed);
        }

        return aggregator.FormatQuantityFreeName(canonicalKey);
    }

    private static string? BuildQuantityFreeOverlapSummary(string? originalSummary)
    {
        if (string.IsNullOrWhiteSpace(originalSummary))
            return null;

        var stripped = AfterAddSuffixRegex.Replace(originalSummary, string.Empty).Trim();
        return string.IsNullOrWhiteSpace(stripped) ? null : stripped;
    }

    private static string? CombineDistinctComments(IEnumerable<string?> comments)
    {
        var list = comments
            .Where(c => !string.IsNullOrWhiteSpace(c))
            .Select(c => c!.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        return list.Count > 0 ? string.Join(", ", list) : null;
    }

    private static string? CombineDistinctSummaries(IEnumerable<string?> summaries)
    {
        var list = summaries
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .SelectMany(s => s!.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        return list.Count > 0 ? string.Join(", ", list) : null;
    }
}
