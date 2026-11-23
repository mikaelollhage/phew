using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Phew.Core.Models;
using Phew.Core.Services;
using Phew.Infrastructure.Data;
using Phew.Infrastructure.Services;

namespace Phew.Tests.Infrastructure;

public class ShoppingImportQuantityFreeServiceTests : IDisposable
{
    private readonly TestDbContextFactory _dbFactory = new();
    private readonly ShoppingListService _shopping;
    private readonly RecipeService _recipes;
    private readonly CategoryService _categories;
    private readonly WeeklyMenuService _menus;

    public ShoppingImportQuantityFreeServiceTests()
    {
        using (var db = _dbFactory.CreateDbContext())
        {
            DbInitializer.InitializeAsync(db, NullLogger.Instance).GetAwaiter().GetResult();
        }

        var aggregator = new ShoppingListAggregator(new ServingCalculator());
        _shopping = new ShoppingListService(_dbFactory, aggregator, new SwedishIngredientParser(), new ShoppingListNotifier());
        _recipes = new RecipeService(_dbFactory, new MockImageStorageService(), NullLogger<RecipeService>.Instance);
        _categories = new CategoryService(_dbFactory, NullLogger<CategoryService>.Instance);
        var matcher = new PantryIngredientMatcher();
        _menus = new WeeklyMenuService(_dbFactory, new WeeklyMenuGenerator(new SeededRandomProvider(42), matcher), matcher, NullLogger<WeeklyMenuService>.Instance);
    }

    public void Dispose() => _dbFactory.Dispose();

    private async Task<Recipe> CreateRecipeAsync(string title, int servings, params (decimal? qty, string? unit, string name)[] ingredients)
    {
        var catId = (await _categories.GetCategoriesAsync()).First(c => c.Name == "Vardagsmat").Id;
        var list = ingredients.Select((ing, idx) => new Ingredient
        {
            OrderIndex = idx + 1,
            Quantity = ing.qty,
            Unit = ing.unit,
            Name = ing.name,
            RawText = $"{ing.qty} {ing.unit} {ing.name}".Trim()
        }).ToList();

        return await _recipes.CreateRecipeAsync(new Recipe
        {
            Title = title,
            Servings = servings,
            ServingsUnit = "portioner",
            Ingredients = list
        }, [catId]);
    }

    [Fact]
    public async Task RecipeConfirm_QuantityFreeDuplicates_PersistsSingleQuantitylessRow_AndPreservesOriginalRecipe()
    {
        var recipe = await CreateRecipeAsync("Smet", 4,
            (10m, "g", "mjölk"),
            (1m, "paket", "mjölk"),
            (1m, "dl", "mjölk"));

        var preview = await _shopping.BuildRecipeShoppingPreviewAsync(recipe.Id, 4);
        preview.Should().NotBeNull();
        preview!.Items.Should().HaveCount(3);

        var allKeys = preview.Items.Select(i => i.PreviewItemKey).ToList();
        var confirmResult = await _shopping.ConfirmRecipeShoppingAdditionsAsync(
            recipe.Id, 4, preview.PreviewStamp, allKeys, quantityFreePreviewItemKeys: allKeys);

        confirmResult.IsSuccess.Should().BeTrue();
        confirmResult.AddedItemsCount.Should().Be(1);

        var shoppingItems = await _shopping.GetItemsAsync();
        shoppingItems.Should().ContainSingle();
        var item = shoppingItems.Single();
        item.Name.Should().Be("Mjölk");
        item.Quantity.Should().BeNull();
        item.QuantityMax.Should().BeNull();
        item.Unit.Should().BeNull();
        item.RawText.Should().Be("Mjölk");

        // Original recipe ingredients must remain completely unchanged
        await using var db = _dbFactory.CreateDbContext();
        var savedRecipe = await db.Recipes.Include(r => r.Ingredients).SingleAsync(r => r.Id == recipe.Id);
        savedRecipe.Ingredients.Should().HaveCount(3);
        savedRecipe.Ingredients.Select(i => (i.Quantity, i.Unit, i.Name)).Should().BeEquivalentTo(new[]
        {
            (10m, "g", "mjölk"),
            (1m, "paket", "mjölk"),
            (1m, "dl", "mjölk")
        });
    }

    [Fact]
    public async Task RecipeConfirm_MixedQuantityAwareAndQuantityFree_PreservesExplicitQuantities()
    {
        var recipe = await CreateRecipeAsync("Blandat", 4,
            (500m, "g", "nötfärs"),
            (1m, "dl", "mjölk"),
            (2m, "st", "gula lökar"));

        var preview = await _shopping.BuildRecipeShoppingPreviewAsync(recipe.Id, 4);
        var allKeys = preview!.Items.Select(i => i.PreviewItemKey).ToList();
        var beefKey = preview.Items.Single(i => i.Item.Name == "nötfärs").PreviewItemKey;
        var quantityFreeKeys = allKeys.Where(k => k != beefKey).ToList();

        var result = await _shopping.ConfirmRecipeShoppingAdditionsAsync(
            recipe.Id, 4, preview.PreviewStamp, allKeys, quantityFreePreviewItemKeys: quantityFreeKeys);

        result.IsSuccess.Should().BeTrue();
        result.AddedItemsCount.Should().Be(3);

        var items = await _shopping.GetItemsAsync();
        items.Should().ContainSingle(i => i.Name == "nötfärs" && i.Quantity == 500m && i.Unit == "g");
        items.Should().ContainSingle(i => i.Name == "Mjölk" && i.Quantity == null && i.Unit == null);
        items.Should().ContainSingle(i => i.Name == "Gula lökar" && i.Quantity == null && i.Unit == null);
    }

    [Fact]
    public async Task QuantityFreeGrouping_DoesNotMergeDistinctProducts_LikeMilkVsOatMilk_OrYellowVsRedOnion()
    {
        var recipe = await CreateRecipeAsync("Varianter", 4,
            (1m, "dl", "mjölk"),
            (1m, "dl", "havremjölk"),
            (1m, "st", "gul lök"),
            (1m, "st", "rödlök"));

        var preview = await _shopping.BuildRecipeShoppingPreviewAsync(recipe.Id, 4);
        var aggregator = new ShoppingListAggregator(new ServingCalculator());
        var rows = ShoppingImportPreviewHelper.BuildDisplayRows(
            preview!.Items.Select(ShoppingImportPreviewHelper.FromRecipeItem),
            new HashSet<string>(),
            aggregator);

        rows.Should().HaveCount(4);
        rows.Select(r => r.DisplayName).Should().Equal("Gul lök", "Havremjölk", "Mjölk", "Rödlök");
    }

    [Fact]
    public async Task QuantityFreeImport_PreservesExistingShoppingListSafeguards()
    {
        var existing = (await _shopping.AddManualItemAsync("2 dl mjölk"))!;
        var recipe = await CreateRecipeAsync("Pannkaka", 4, (1m, "dl", "mjölk"));

        var preview = await _shopping.BuildRecipeShoppingPreviewAsync(recipe.Id, 4);
        var allKeys = preview!.Items.Select(i => i.PreviewItemKey).ToList();

        var result = await _shopping.ConfirmRecipeShoppingAdditionsAsync(
            recipe.Id, 4, preview.PreviewStamp, allKeys, quantityFreePreviewItemKeys: allKeys);

        result.IsSuccess.Should().BeTrue();

        var items = await _shopping.GetItemsAsync();
        items.Should().ContainSingle();
        var updated = items.Single();
        updated.Id.Should().Be(existing.Id);
        updated.Quantity.Should().Be(2m);
        updated.Unit.Should().Be("dl");
        updated.Comment.Should().Contain("mängd ej angiven");
    }

    [Fact]
    public async Task WeeklyMenuConfirm_QuantityFreeDuplicates_PersistsSingleRow_AndTracksAllContributions()
    {
        var weekDate = new DateOnly(2026, 11, 2);
        var r1 = await CreateRecipeAsync("Dag 1", 4, (10m, "g", "mjölk"));
        var r2 = await CreateRecipeAsync("Dag 2", 4, (1m, "paket", "mjölk"));

        var menu = await _menus.GetOrCreateWeekMenuAsync(weekDate);
        menu.Days[0].RecipeId = r1.Id;
        menu.Days[0].Servings = 4;
        menu.Days[1].RecipeId = r2.Id;
        menu.Days[1].Servings = 4;
        await _menus.ConfirmMenuAsync(menu);

        var preview = await _shopping.BuildWeeklyMenuPreviewAsync(weekDate);
        var allKeys = preview.Items.Select(i => i.PreviewItemKey).ToList();

        var confirmResult = await _shopping.ConfirmWeeklyMenuAdditionsAsync(
            weekDate, preview.PreviewStamp, allKeys, quantityFreePreviewItemKeys: allKeys);

        confirmResult.IsSuccess.Should().BeTrue();
        confirmResult.AddedItemsCount.Should().Be(1);

        var items = await _shopping.GetItemsAsync();
        items.Should().ContainSingle(i => i.Name == "Mjölk" && i.Quantity == null && i.Unit == null);

        // Both day contributions are still recorded
        confirmResult.UpdatedPreview.PlannedMeals.Should().AllSatisfy(m => m.IsFullyAdded.Should().BeTrue());
    }
}
