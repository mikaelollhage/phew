using Phew.Core.Models;
using Phew.Core.Services;

namespace Phew.Web.Components.Recipes;

public enum IngredientEditorItemKind
{
    Section,
    Ingredient
}

/// <summary>
/// Represents either a visual section header or an ingredient row in the unified ingredient editor list.
/// </summary>
public sealed class IngredientEditorItem
{
    private IngredientEditorItem(
        int key,
        IngredientEditorItemKind kind,
        string sectionTitle,
        Ingredient? ingredient,
        Ingredient? originalIngredient = null,
        string? originalSectionTitle = null)
    {
        Key = key;
        Kind = kind;
        SectionTitle = sectionTitle;
        Ingredient = ingredient;
        OriginalIngredient = originalIngredient;
        OriginalSectionTitle = originalSectionTitle;
    }

    public int Key { get; }
    public IngredientEditorItemKind Kind { get; }
    public string SectionTitle { get; set; }
    public Ingredient? Ingredient { get; }
    internal Ingredient? OriginalIngredient { get; }
    internal string? OriginalSectionTitle { get; }

    public bool IsSection => Kind == IngredientEditorItemKind.Section;
    public bool IsIngredient => Kind == IngredientEditorItemKind.Ingredient;

    public static IngredientEditorItem CreateSection(
        int key,
        string title,
        Ingredient? originalIngredient = null,
        string? originalSectionTitle = null) =>
        new(key, IngredientEditorItemKind.Section, title, null, originalIngredient, originalSectionTitle);

    public static IngredientEditorItem CreateIngredient(int key, Ingredient ingredient) =>
        new(key, IngredientEditorItemKind.Ingredient, string.Empty, ingredient);
}

/// <summary>
/// Deterministic state and reordering helper for a unified list of ingredient section headers and ingredients.
/// </summary>
public sealed class IngredientSectionList
{
    private readonly List<IngredientEditorItem> _items = new();
    private readonly SwedishIngredientParser _ingredientParser;
    private int _nextKey = 1;

    public IngredientSectionList(SwedishIngredientParser? ingredientParser = null)
    {
        _ingredientParser = ingredientParser ?? new SwedishIngredientParser();
    }

    public IReadOnlyList<IngredientEditorItem> Items => _items;

    public int IngredientCount => _items.Count(i => i.IsIngredient);

    public static string? NormalizeGroupHeader(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    /// <summary>
    /// Builds the unified editor list from an ordered ingredient sequence while preserving exact order,
    /// repeated group names, and ungrouped ingredients between grouped sections.
    /// </summary>
    public void LoadFromIngredients(IEnumerable<Ingredient> ingredients)
    {
        _items.Clear();
        string? currentGroup = null;

        foreach (var ing in ingredients)
        {
            var group = NormalizeGroupHeader(ing.GroupHeader);
            if (group != null && !string.Equals(group, currentGroup, StringComparison.Ordinal))
            {
                _items.Add(IngredientEditorItem.CreateSection(_nextKey++, group));
                currentGroup = group;
            }
            else if (group == null && currentGroup != null)
            {
                // Preserve exact boundary when ungrouped ingredients appear after a grouped section.
                _items.Add(IngredientEditorItem.CreateSection(_nextKey++, string.Empty));
                currentGroup = null;
            }

            _items.Add(IngredientEditorItem.CreateIngredient(_nextKey++, ing));
        }
    }

    /// <summary>
    /// Synchronizes the target ingredient collection in-place with the current order and section headers.
    /// Empty sections are not persisted as ingredients; unnamed sections yield null GroupHeader.
    /// </summary>
    public void SyncToIngredients(IList<Ingredient> targetIngredients)
    {
        targetIngredients.Clear();

        string? activeGroup = null;
        int orderIndex = 1;

        foreach (var item in _items)
        {
            if (item.IsSection)
            {
                activeGroup = NormalizeGroupHeader(item.SectionTitle);
                continue;
            }

            if (item.Ingredient != null)
            {
                item.Ingredient.GroupHeader = activeGroup;
                item.Ingredient.OrderIndex = orderIndex++;
                targetIngredients.Add(item.Ingredient);
            }
        }
    }

    public IngredientEditorItem AddIngredient(Ingredient? ingredient = null, IList<Ingredient>? targetIngredients = null)
    {
        var ing = ingredient ?? new Ingredient { Name = string.Empty };
        var item = IngredientEditorItem.CreateIngredient(_nextKey++, ing);
        _items.Add(item);
        if (targetIngredients != null)
        {
            SyncToIngredients(targetIngredients);
        }
        return item;
    }

    public IngredientEditorItem AddSection(string title = "", IList<Ingredient>? targetIngredients = null)
    {
        var item = IngredientEditorItem.CreateSection(_nextKey++, title);
        _items.Add(item);
        if (targetIngredients != null)
        {
            SyncToIngredients(targetIngredients);
        }
        return item;
    }

    public bool RenameSection(int sectionKey, string newTitle, IList<Ingredient>? targetIngredients = null)
    {
        var item = _items.FirstOrDefault(i => i.Key == sectionKey && i.IsSection);
        if (item == null)
        {
            return false;
        }

        item.SectionTitle = newTitle ?? string.Empty;
        if (targetIngredients != null)
        {
            SyncToIngredients(targetIngredients);
        }
        return true;
    }

    /// <summary>
    /// Removes a section header without deleting any of its ingredients.
    /// </summary>
    public bool RemoveSection(int sectionKey, IList<Ingredient>? targetIngredients = null)
    {
        var index = _items.FindIndex(i => i.Key == sectionKey && i.IsSection);
        if (index < 0)
        {
            return false;
        }

        _items.RemoveAt(index);
        if (targetIngredients != null)
        {
            SyncToIngredients(targetIngredients);
        }
        return true;
    }

    public bool RemoveIngredient(int ingredientKey, IList<Ingredient>? targetIngredients = null)
    {
        var index = _items.FindIndex(i => i.Key == ingredientKey && i.IsIngredient);
        if (index < 0)
        {
            return false;
        }

        _items.RemoveAt(index);
        if (targetIngredients != null)
        {
            SyncToIngredients(targetIngredients);
        }
        return true;
    }

    /// <summary>
    /// Converts an ingredient row into a section header at the exact same position and recalculates grouping.
    /// Temporarily retains the original ingredient in memory for lossless round-trip conversion.
    /// </summary>
    public IngredientEditorItem? ConvertIngredientToSection(int ingredientKey, IList<Ingredient>? targetIngredients = null)
    {
        var index = _items.FindIndex(i => i.Key == ingredientKey && i.IsIngredient);
        if (index < 0 || _items[index].Ingredient == null)
        {
            return null;
        }

        var original = CloneIngredient(_items[index].Ingredient!);
        var title = ExtractSectionTitleFromIngredient(original);
        var sectionItem = IngredientEditorItem.CreateSection(_nextKey++, title, original, title);
        _items[index] = sectionItem;

        if (targetIngredients != null)
        {
            SyncToIngredients(targetIngredients);
        }

        return sectionItem;
    }

    /// <summary>
    /// Converts a section header into an editable ingredient row at the exact same position and recalculates grouping.
    /// Restores the original ingredient if the section title was not changed; otherwise parses the title.
    /// </summary>
    public IngredientEditorItem? ConvertSectionToIngredient(int sectionKey, IList<Ingredient>? targetIngredients = null)
    {
        var index = _items.FindIndex(i => i.Key == sectionKey && i.IsSection);
        if (index < 0)
        {
            return null;
        }

        var sectionItem = _items[index];
        var title = sectionItem.SectionTitle?.Trim() ?? string.Empty;

        Ingredient ingredient;
        if (sectionItem.OriginalIngredient != null &&
            string.Equals(title, sectionItem.OriginalSectionTitle, StringComparison.Ordinal))
        {
            ingredient = CloneIngredient(sectionItem.OriginalIngredient);
        }
        else
        {
            ingredient = _ingredientParser.Parse(title);
        }

        var ingredientItem = IngredientEditorItem.CreateIngredient(_nextKey++, ingredient);
        _items[index] = ingredientItem;

        if (targetIngredients != null)
        {
            SyncToIngredients(targetIngredients);
        }

        return ingredientItem;
    }

    private static Ingredient CloneIngredient(Ingredient source) => new()
    {
        Id = source.Id,
        RecipeId = source.RecipeId,
        Recipe = source.Recipe,
        OrderIndex = source.OrderIndex,
        RawText = source.RawText,
        Quantity = source.Quantity,
        QuantityMax = source.QuantityMax,
        Unit = source.Unit,
        Name = source.Name,
        Comment = source.Comment,
        Preparation = source.Preparation,
        GroupHeader = source.GroupHeader
    };

    private static string ExtractSectionTitleFromIngredient(Ingredient ingredient)
    {
        var name = ingredient.Name?.Trim() ?? string.Empty;
        var raw = ingredient.RawText?.Trim() ?? string.Empty;

        var hasStructuredParts = ingredient.Quantity.HasValue
            || ingredient.QuantityMax.HasValue
            || !string.IsNullOrWhiteSpace(ingredient.Unit)
            || !string.IsNullOrWhiteSpace(ingredient.Comment)
            || !string.IsNullOrWhiteSpace(ingredient.Preparation);

        string candidate;
        if (hasStructuredParts && !string.IsNullOrWhiteSpace(raw) &&
            (string.IsNullOrWhiteSpace(name) || raw.Contains(name, StringComparison.OrdinalIgnoreCase)))
        {
            candidate = raw;
        }
        else if (!string.IsNullOrWhiteSpace(name))
        {
            candidate = name;
        }
        else
        {
            candidate = raw;
        }

        return candidate.Trim().TrimStart('#').Trim().TrimEnd(':').Trim();
    }

    public int GetSectionIngredientCount(int sectionKey)
    {
        var index = _items.FindIndex(i => i.Key == sectionKey && i.IsSection);
        if (index < 0)
        {
            return 0;
        }

        int count = 0;
        for (int i = index + 1; i < _items.Count && !_items[i].IsSection; i++)
        {
            if (_items[i].IsIngredient)
            {
                count++;
            }
        }
        return count;
    }

    public bool CanMoveUp(int itemKey)
    {
        var index = _items.FindIndex(i => i.Key == itemKey);
        return index > 0;
    }

    public bool CanMoveDown(int itemKey)
    {
        var index = _items.FindIndex(i => i.Key == itemKey);
        return index >= 0 && index < _items.Count - 1;
    }

    public (int IndicatorItemKey, bool IsAbove)? GetDropIndicatorTarget(int sourceKey, int hoveredKey)
    {
        if (sourceKey == hoveredKey)
        {
            return null;
        }

        var sourceIndex = _items.FindIndex(i => i.Key == sourceKey);
        var hoveredIndex = _items.FindIndex(i => i.Key == hoveredKey);
        if (sourceIndex < 0 || hoveredIndex < 0)
        {
            return null;
        }

        return (hoveredKey, sourceIndex > hoveredIndex);
    }

    /// <summary>
    /// Moves an ingredient or section header row onto the target item's position and updates GroupHeader and OrderIndex.
    /// </summary>
    public bool MoveItem(int sourceKey, int targetKey, IList<Ingredient>? targetIngredients = null)
    {
        if (sourceKey == targetKey)
        {
            return false;
        }

        var sourceIndex = _items.FindIndex(i => i.Key == sourceKey);
        var targetIndex = _items.FindIndex(i => i.Key == targetKey);
        if (sourceIndex < 0 || targetIndex < 0)
        {
            return false;
        }

        var moved = _items[sourceIndex];
        _items.RemoveAt(sourceIndex);
        _items.Insert(targetIndex, moved);
        if (targetIngredients != null)
        {
            SyncToIngredients(targetIngredients);
        }
        return true;
    }

    /// <summary>
    /// Moves an ingredient or section header row by one position up or down.
    /// </summary>
    public bool MoveByOffset(int sourceKey, int direction, IList<Ingredient>? targetIngredients = null)
    {
        if (direction == 0)
        {
            return false;
        }

        var sourceIndex = _items.FindIndex(i => i.Key == sourceKey);
        if (sourceIndex < 0)
        {
            return false;
        }

        int targetIndex = sourceIndex + (direction < 0 ? -1 : 1);
        if (targetIndex < 0 || targetIndex >= _items.Count)
        {
            return false;
        }

        var moved = _items[sourceIndex];
        _items.RemoveAt(sourceIndex);
        _items.Insert(targetIndex, moved);
        if (targetIngredients != null)
        {
            SyncToIngredients(targetIngredients);
        }
        return true;
    }
}
