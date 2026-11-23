using Bunit;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.FluentUI.AspNetCore.Components;
using Phew.Core.Models;
using Phew.Core.Services;
using Phew.Infrastructure.Data;
using Phew.Infrastructure.Services;
using Phew.Tests.Infrastructure;
using Phew.Web.Components.Recipes;
using WeeklyMenuPreviewComponent = Phew.Web.Components.WeeklyMenu.WeeklyMenuShoppingPreview;

namespace Phew.Tests.Components;

public class ShoppingImportQuantityModeTests : TestContext
{
    private readonly TestDbContextFactory _dbFactory;
    private readonly ShoppingListNotifier _notifier;
    private readonly RecipeService _recipeService;
    private readonly CategoryService _categoryService;
    private readonly WeeklyMenuService _weeklyMenuService;
    private readonly ShoppingListService _shoppingService;

    public ShoppingImportQuantityModeTests()
    {
        _dbFactory = new TestDbContextFactory();
        using (var db = _dbFactory.CreateDbContext())
        {
            DbInitializer.InitializeAsync(db, NullLogger.Instance).GetAwaiter().GetResult();
        }

        _notifier = new ShoppingListNotifier();
        var calc = new ServingCalculator();
        var aggregator = new ShoppingListAggregator(calc);
        var parser = new SwedishIngredientParser();
        var imageStorage = new MockImageStorageService();
        var pantryMatcher = new PantryIngredientMatcher();
        var generator = new WeeklyMenuGenerator(new SeededRandomProvider(42), pantryMatcher);

        _recipeService = new RecipeService(_dbFactory, imageStorage, NullLogger<RecipeService>.Instance);
        _categoryService = new CategoryService(_dbFactory, NullLogger<CategoryService>.Instance);
        _weeklyMenuService = new WeeklyMenuService(_dbFactory, generator, pantryMatcher, NullLogger<WeeklyMenuService>.Instance);
        _shoppingService = new ShoppingListService(_dbFactory, aggregator, parser, _notifier);

        Services.AddFluentUIComponents();
        Services.AddSingleton<IDbContextFactory<AppDbContext>>(_dbFactory);
        Services.AddSingleton(calc);
        Services.AddSingleton(aggregator);
        Services.AddSingleton(parser);
        Services.AddSingleton(_notifier);
        Services.AddSingleton(_recipeService);
        Services.AddSingleton(_categoryService);
        Services.AddSingleton(_weeklyMenuService);
        Services.AddSingleton(_shoppingService);
        Services.AddLogging();

        JSInterop.Mode = JSRuntimeMode.Loose;
    }

    private async Task<Recipe> CreateRecipeAsync(string title, int servings, params (decimal? qty, string? unit, string name)[] ingredients)
    {
        var catId = (await _categoryService.GetCategoriesAsync()).First(c => c.Name == "Vardagsmat").Id;
        var list = ingredients.Select((ing, idx) => new Ingredient
        {
            OrderIndex = idx + 1,
            Quantity = ing.qty,
            Unit = ing.unit,
            Name = ing.name,
            RawText = $"{ing.qty} {ing.unit} {ing.name}".Trim()
        }).ToList();

        return await _recipeService.CreateRecipeAsync(new Recipe
        {
            Title = title,
            Servings = servings,
            ServingsUnit = "portioner",
            Ingredients = list
        }, [catId]);
    }

    [Fact]
    public async Task RecipePreview_DefaultsToQuantityFree_SortsSwedishAlphabetically_AndGroupsDuplicates()
    {
        var recipe = await CreateRecipeAsync("Testrecept", 4,
            (10m, "g", "mjölk"),
            (1m, "paket", "mjölk"),
            (1m, "dl", "mjölk"),
            (2m, "st", "ägg"),
            (1m, "dl", "åkerbönor"),
            (1m, "tsk", "örtkrydda"));

        var cut = RenderComponent<RecipeShoppingPreviewModal>(p => p
            .Add(x => x.IsVisible, true)
            .Add(x => x.RecipeId, recipe.Id)
            .Add(x => x.Servings, 4));

        cut.WaitForAssertion(() =>
        {
            var rows = cut.FindAll("li.weekly-preview-item-row");
            rows.Should().HaveCount(4);

            var names = rows.Select(r => r.GetAttribute("data-ingredient-name")).ToList();
            names.Should().Equal("Mjölk", "Åkerbönor", "Ägg", "Örtkrydda");

            // Grouped row indicator and accurate resulting count
            rows.First().TextContent.Should().Contain("3 förekomster");
            cut.Find(".weekly-preview-summary-counts").TextContent.Should().Contain("4 varor att lägga till");
            cut.Find(".weekly-preview-confirm-btn").TextContent.Should().Contain("(4)");
        });
    }

    [Fact]
    public async Task RecipePreview_ExpandGroupedRow_AndKeepQuantityForOneEntry_SplitsConsistently()
    {
        var recipe = await CreateRecipeAsync("Pannkaka", 4,
            (10m, "g", "mjölk"),
            (1m, "paket", "mjölk"),
            (1m, "dl", "mjölk"));

        var cut = RenderComponent<RecipeShoppingPreviewModal>(p => p
            .Add(x => x.IsVisible, true)
            .Add(x => x.RecipeId, recipe.Id)
            .Add(x => x.Servings, 4));

        cut.WaitForAssertion(() => cut.FindAll(".weekly-preview-group-badge").Should().ContainSingle());

        // Expand group
        cut.Find(".weekly-preview-group-badge").Click();

        cut.WaitForAssertion(() =>
        {
            var sources = cut.FindAll(".weekly-preview-group-source-item");
            sources.Should().HaveCount(3);
        });

        // Keep quantity for "1 dl mjölk"
        var dlSource = cut.FindAll(".weekly-preview-group-source-item")
            .Single(s => s.TextContent.Contains("1 dl mjölk"));
        var dlBtn = dlSource.QuerySelector("button.weekly-preview-qty-icon-btn")!;
        dlBtn.QuerySelector("svg").Should().NotBeNull();
        dlBtn.Click();

        cut.WaitForAssertion(() =>
        {
            var rows = cut.FindAll("li.weekly-preview-item-row");
            rows.Should().HaveCount(2);

            // One grouped quantity-free row (2 occurrences) and one quantity-aware row (1 dl mjölk)
            rows.Should().ContainSingle(r => r.TextContent.Contains("Mjölk") && r.TextContent.Contains("2 förekomster"));
            rows.Should().ContainSingle(r => r.TextContent.Contains("1 dl mjölk"));
            cut.Find(".weekly-preview-summary-counts").TextContent.Should().Contain("2 varor att lägga till");
        });
    }

    [Fact]
    public async Task RecipePreview_PerRowOverride_And_DeselectDisablesQuantityToggle()
    {
        var recipe = await CreateRecipeAsync("Gryta", 4,
            (500m, "g", "nötfärs"),
            (2m, "st", "gula lökar"));

        var cut = RenderComponent<RecipeShoppingPreviewModal>(p => p
            .Add(x => x.IsVisible, true)
            .Add(x => x.RecipeId, recipe.Id)
            .Add(x => x.Servings, 4));

        cut.WaitForAssertion(() => cut.FindAll("li.weekly-preview-item-row").Should().HaveCount(2));

        // Toggle "Nötfärs" to include quantity via compact icon-only button
        var beefRow = cut.FindAll("li.weekly-preview-item-row")
            .Single(r => r.GetAttribute("data-ingredient-name") == "Nötfärs");
        var beefBtn = beefRow.QuerySelector("button.weekly-preview-qty-icon-btn")!;
        beefBtn.QuerySelector("svg").Should().NotBeNull();
        beefBtn.GetAttribute("aria-pressed").Should().Be("false");
        beefBtn.Click();

        cut.WaitForAssertion(() =>
        {
            var updatedBeef = cut.FindAll("li.weekly-preview-item-row")
                .Single(r => r.GetAttribute("data-ingredient-name") == "nötfärs");
            updatedBeef.TextContent.Should().Contain("500 g nötfärs");
            updatedBeef.QuerySelector("button.weekly-preview-qty-icon-btn")!.GetAttribute("aria-pressed").Should().Be("true");

            var onionRow = cut.FindAll("li.weekly-preview-item-row")
                .Single(r => r.GetAttribute("data-ingredient-name") == "Gula lökar");
            onionRow.TextContent.Should().NotContain("2 st");
        });

        // Deselect "Gula lökar" -> its quantity toggle button must be disabled
        var onionCheckbox = cut.FindAll("li.weekly-preview-item-row")
            .Single(r => r.GetAttribute("data-ingredient-name") == "Gula lökar")
            .QuerySelector("input.weekly-preview-checkbox")!;
        onionCheckbox.Change(false);

        cut.WaitForAssertion(() =>
        {
            var onionBtn = cut.FindAll("li.weekly-preview-item-row")
                .Single(r => r.GetAttribute("data-ingredient-name") == "Gula lökar")
                .QuerySelector("button.weekly-preview-qty-icon-btn")!;
            onionBtn.HasAttribute("disabled").Should().BeTrue();
            cut.Find(".weekly-preview-summary-counts").TextContent.Should().Contain("1 vara att lägga till");
        });
    }

    [Fact]
    public async Task WeeklyMenuPreview_HasParityWithRecipePreview_ForQuantityModeGroupingAndSwedishSorting()
    {
        var weekDate = new DateOnly(2026, 11, 2);
        var recipe1 = await CreateRecipeAsync("Måndagsrätt", 4,
            (10m, "g", "mjölk"),
            (2m, "st", "ägg"));
        var recipe2 = await CreateRecipeAsync("Tisdagsrätt", 4,
            (1m, "dl", "mjölk"),
            (1m, "dl", "åkerbönor"));

        var menu = await _weeklyMenuService.GetOrCreateWeekMenuAsync(weekDate);
        menu.Days[0].RecipeId = recipe1.Id;
        menu.Days[0].Servings = 4;
        menu.Days[1].RecipeId = recipe2.Id;
        menu.Days[1].Servings = 4;
        await _weeklyMenuService.ConfirmMenuAsync(menu);

        var cut = RenderComponent<WeeklyMenuPreviewComponent>(p => p
            .Add(x => x.IsVisible, true)
            .Add(x => x.InitialWeekDate, weekDate)
            .Add(x => x.ReferenceToday, weekDate));

        cut.WaitForAssertion(() =>
        {
            var rows = cut.FindAll("li.weekly-preview-item-row");
            rows.Should().HaveCount(3);
            rows.Select(r => r.GetAttribute("data-ingredient-name"))
                .Should().Equal("Mjölk", "Åkerbönor", "Ägg");
            rows.First().TextContent.Should().Contain("2 förekomster");
            cut.Find(".weekly-preview-summary-counts").TextContent.Should().Contain("3 varor att lägga till");
        });

        // Switch global mode to WithQuantity
        cut.Find(".weekly-preview-qty-mode-with").Click();

        cut.WaitForAssertion(() =>
        {
            var rows = cut.FindAll("li.weekly-preview-item-row");
            rows.Should().HaveCount(4);
            cut.Find(".weekly-preview-summary-counts").TextContent.Should().Contain("4 varor att lägga till");
        });
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _dbFactory.Dispose();
        }
        base.Dispose(disposing);
    }
}
