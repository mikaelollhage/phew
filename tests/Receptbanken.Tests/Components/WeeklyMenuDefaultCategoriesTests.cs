using Bunit;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.FluentUI.AspNetCore.Components;
using Receptbanken.Core.Interfaces;
using Receptbanken.Core.Models;
using Receptbanken.Core.Services;
using Receptbanken.Infrastructure.Data;
using Receptbanken.Infrastructure.Services;
using Receptbanken.Tests.Infrastructure;
using Receptbanken.Web.Components.Pages;
using Receptbanken.Web.Services;

namespace Receptbanken.Tests.Components;

public class WeeklyMenuDefaultCategoriesTests : TestContext
{
    private sealed class FixedLocalDateProvider : ILocalDateProvider
    {
        private DateOnly _today;
        public FixedLocalDateProvider(DateOnly today) => _today = today;
        public DateOnly GetLocalToday() => _today;
        public void SetClientLocalDate(DateOnly clientDate) => _today = clientDate;
    }

    private sealed class TestBundle : IDisposable
    {
        public required TestDbContextFactory DbFactory { get; init; }
        public required RecipeService RecipeService { get; init; }
        public required CategoryService CategoryService { get; init; }
        public required WeeklyMenuService WeeklyMenuService { get; init; }

        public void Dispose() => DbFactory.Dispose();
    }

    private TestBundle SetupServices(DateOnly? localToday = null, bool seedDefaults = true)
    {
        JSInterop.Mode = JSRuntimeMode.Loose;

        var dbFactory = new TestDbContextFactory();
        if (seedDefaults)
        {
            using var db = dbFactory.CreateDbContext();
            DbInitializer.InitializeAsync(db, NullLogger.Instance).GetAwaiter().GetResult();
        }

        var ratingNotifier = new RecipeRatingNotifier();
        var memberNotifier = new FamilyMemberNotifier();
        var imageStorage = new MockImageStorageService();
        var recipeService = new RecipeService(dbFactory, imageStorage, NullLogger<RecipeService>.Instance);
        var categoryService = new CategoryService(dbFactory, NullLogger<CategoryService>.Instance);
        var servingCalculator = new ServingCalculator();
        var shoppingService = new ShoppingListService(
            dbFactory,
            new ShoppingListAggregator(servingCalculator),
            new SwedishIngredientParser(),
            new ShoppingListNotifier());
        var memberService = new FamilyMemberService(dbFactory, memberNotifier, recipeRatingNotifier: ratingNotifier);
        var ratingService = new RecipeRatingService(dbFactory, ratingNotifier, logger: NullLogger<RecipeRatingService>.Instance);
        var pantryMatcher = new PantryIngredientMatcher();
        var generator = new WeeklyMenuGenerator(new SeededRandomProvider(42), pantryMatcher);
        var weeklyMenuService = new WeeklyMenuService(dbFactory, generator, pantryMatcher, NullLogger<WeeklyMenuService>.Instance);
        var dateProvider = new FixedLocalDateProvider(localToday ?? new DateOnly(2026, 11, 2));

        Services.AddFluentUIComponents();
        Services.AddScoped<AppNotificationService>();
        Services.AddSingleton<IDbContextFactory<AppDbContext>>(dbFactory);
        Services.AddSingleton<IImageStorageService>(imageStorage);
        Services.AddSingleton<ILocalDateProvider>(dateProvider);
        Services.AddSingleton(servingCalculator);
        Services.AddSingleton(ratingNotifier);
        Services.AddSingleton(memberNotifier);
        Services.AddSingleton(recipeService);
        Services.AddSingleton(categoryService);
        Services.AddSingleton(shoppingService);
        Services.AddSingleton(memberService);
        Services.AddSingleton(ratingService);
        Services.AddSingleton(weeklyMenuService);

        return new TestBundle
        {
            DbFactory = dbFactory,
            RecipeService = recipeService,
            CategoryService = categoryService,
            WeeklyMenuService = weeklyMenuService
        };
    }

    [Fact]
    public async Task NewWeek_ShowsAllSevenDefaultCategoriesInUiAndPickerBeforeGeneration()
    {
        var today = new DateOnly(2026, 11, 2); // Monday
        using var bundle = SetupServices(today);
        var cats = await bundle.CategoryService.GetCategoriesAsync();
        var vardag = cats.First(c => c.Name == "Vardagsmat");
        var fest = cats.First(c => c.Name == "Fest & Helg");

        var cut = RenderComponent<WeeklyMenuPage>();
        cut.WaitForAssertion(() => cut.FindAll(".weekly-day-card").Should().HaveCount(7));

        for (int i = 0; i < 7; i++)
        {
            var expectedName = i is 4 or 5 ? "Fest & Helg" : "Vardagsmat";
            var expectedId = i is 4 or 5 ? fest.Id : vardag.Id;

            var card = cut.Find($".weekly-day-card[data-day-index='{i}']");
            card.QuerySelector(".weekly-day-category-toggle")!.ClassList.Should().Contain("has-selected");
            card.QuerySelector(".weekly-day-category-count")!.TextContent.Should().Be("1");
            card.QuerySelector(".day-category-chip")!.TextContent.Should().Contain(expectedName);

            // Expand picker and verify preselection before generation without +/✓ symbols
            card.QuerySelector(".weekly-day-category-toggle")!.Click();
            var selectedOption = cut.Find($".weekly-day-card[data-day-index='{i}'] .day-category-option[data-option-category-id='{expectedId}']");
            selectedOption.ClassList.Should().Contain("selected");
            selectedOption.GetAttribute("aria-pressed").Should().Be("true");
            selectedOption.TextContent.Should().NotContain("✓").And.NotContain("+");
        }
    }

    [Fact]
    public async Task DefaultSelections_SurviveDraftSaveAndReload()
    {
        var today = new DateOnly(2026, 11, 2);
        using var bundle = SetupServices(today);

        var cut = RenderComponent<WeeklyMenuPage>();
        cut.WaitForAssertion(() => cut.FindAll(".weekly-day-card").Should().HaveCount(7));

        cut.Find("button.save-draft-btn").Click();
        cut.WaitForAssertion(() => cut.Find("button.clear-menu-btn").Should().NotBeNull());

        // Re-render page (simulating reload)
        var reloaded = RenderComponent<WeeklyMenuPage>();
        reloaded.WaitForAssertion(() => reloaded.FindAll(".weekly-day-card").Should().HaveCount(7));

        for (int i = 0; i < 7; i++)
        {
            var expectedName = i is 4 or 5 ? "Fest & Helg" : "Vardagsmat";
            reloaded.Find($".weekly-day-card[data-day-index='{i}'] .day-category-chip").TextContent.Should().Contain(expectedName);
            reloaded.Find($".weekly-day-card[data-day-index='{i}'] .weekly-day-category-count").TextContent.Should().Be("1");
        }
    }

    [Fact]
    public async Task UserSelectedAndClearedCategories_RemainUnchangedAfterSaveAndReload_AndExistingEmptyDraftIsNotRewritten()
    {
        var week1Date = new DateOnly(2026, 11, 2);
        var week2Date = week1Date.AddDays(7);
        using var bundle = SetupServices(week1Date);
        var cats = await bundle.CategoryService.GetCategoriesAsync();
        var pasta = cats.First(c => c.Name == "Pasta");

        // Persist week 2 as an empty draft with 0 categories (legacy/empty saved draft)
        var week2EmptyDraft = new WeeklyMenu
        {
            WeekStartDate = week2Date,
            IsoYear = CalendarWeek.FromDate(week2Date).IsoYear,
            IsoWeek = CalendarWeek.FromDate(week2Date).IsoWeek,
            Status = WeeklyMenuStatus.Draft
        };
        WeeklyMenuGenerator.EnsureSevenDaysInitialized(week2EmptyDraft);
        await bundle.WeeklyMenuService.SaveDraftAsync(week2EmptyDraft);

        var cut = RenderComponent<WeeklyMenuPage>();
        cut.WaitForAssertion(() => cut.FindAll(".weekly-day-card").Should().HaveCount(7));

        // On week 1: clear Monday's default category, and set Tuesday to Pasta only
        cut.Find(".weekly-day-card[data-day-index='0'] .day-category-remove-btn").Click();
        cut.Find(".weekly-day-card[data-day-index='1'] .day-category-remove-btn").Click();
        cut.Find(".weekly-day-card[data-day-index='1'] .weekly-day-category-toggle").Click();
        cut.Find($".weekly-day-card[data-day-index='1'] .day-category-option[data-option-category-id='{pasta.Id}']").Click();

        cut.Find("button.save-draft-btn").Click();
        cut.WaitForAssertion(() => cut.FindAll(".weekly-status-badge.status-unsaved").Should().BeEmpty());

        // Reload week 1 -> cleared Monday stays empty, Tuesday keeps Pasta
        var reloaded = RenderComponent<WeeklyMenuPage>();
        reloaded.WaitForAssertion(() => reloaded.FindAll(".weekly-day-card").Should().HaveCount(7));

        reloaded.FindAll(".weekly-day-card[data-day-index='0'] .day-category-chip").Should().BeEmpty();
        reloaded.FindAll(".weekly-day-card[data-day-index='0'] .weekly-day-category-count").Should().BeEmpty();
        reloaded.Find(".weekly-day-card[data-day-index='1'] .day-category-chip").TextContent.Should().Contain("Pasta");

        // Navigate to week 2 -> existing saved empty draft is NOT silently rewritten
        reloaded.Find("button.week-next-btn").Click();
        reloaded.WaitForAssertion(() => reloaded.Find(".page-title").TextContent.Should().Contain($"Vecka {CalendarWeek.FromDate(week2Date).IsoWeek}"));
        for (int i = 0; i < 7; i++)
        {
            reloaded.FindAll($".weekly-day-card[data-day-index='{i}'] .day-category-chip").Should().BeEmpty();
        }
    }

    [Fact]
    public async Task GenerationAndReroll_RespectDefaultAndUserSelectedCategories()
    {
        var today = new DateOnly(2026, 11, 2);
        using var bundle = SetupServices(today);
        var cats = await bundle.CategoryService.GetCategoriesAsync();
        var vardag = cats.First(c => c.Name == "Vardagsmat");
        var fest = cats.First(c => c.Name == "Fest & Helg");
        var pasta = cats.First(c => c.Name == "Pasta");

        await bundle.RecipeService.CreateRecipeAsync(new Recipe { Title = "Vardagsköttbullar", Servings = 4 }, [vardag.Id]);
        await bundle.RecipeService.CreateRecipeAsync(new Recipe { Title = "Helgoxfilé", Servings = 4 }, [fest.Id]);
        var pastaRecipe = await bundle.RecipeService.CreateRecipeAsync(new Recipe { Title = "Specialpasta", Servings = 4 }, [pasta.Id]);

        var cut = RenderComponent<WeeklyMenuPage>();
        cut.WaitForAssertion(() => cut.FindAll(".weekly-day-card").Should().HaveCount(7));

        // Generate using defaults -> Friday (day 4) must pick Helgoxfilé, Monday (day 0) must pick Vardagsköttbullar
        cut.Find("button.generate-week-btn").Click();
        cut.WaitForAssertion(() =>
        {
            cut.Find(".weekly-day-card[data-day-index='0'] .weekly-day-recipe-title").TextContent.Should().Contain("Vardagsköttbullar");
            cut.Find(".weekly-day-card[data-day-index='4'] .weekly-day-recipe-title").TextContent.Should().Contain("Helgoxfilé");
        });

        // Change Monday to Pasta only and reroll -> must pick Specialpasta
        cut.Find(".weekly-day-card[data-day-index='0'] .day-category-remove-btn").Click();
        cut.Find(".weekly-day-card[data-day-index='0'] .weekly-day-category-toggle").Click();
        cut.Find($".weekly-day-card[data-day-index='0'] .day-category-option[data-option-category-id='{pasta.Id}']").Click();
        cut.Find(".weekly-day-card[data-day-index='0'] .weekly-day-reroll-btn").Click();

        cut.WaitForAssertion(() =>
        {
            cut.Find(".weekly-day-card[data-day-index='0'] .weekly-day-recipe-title").TextContent.Should().Contain("Specialpasta");
        });
    }

    [Fact]
    public async Task MissingDefaultCategories_HandledSafelyWithoutDuplicatesOrExceptions()
    {
        var today = new DateOnly(2026, 11, 2);
        using var bundle = SetupServices(today, seedDefaults: false);

        // Only create an unrelated category; neither "Vardagsmat" nor "Fest & Helg" exists
        await bundle.CategoryService.CreateCategoryAsync(new Category { Name = "Snabblagat", ColorHex = "#2a9d8f" });

        var cut = RenderComponent<WeeklyMenuPage>();
        cut.WaitForAssertion(() => cut.FindAll(".weekly-day-card").Should().HaveCount(7));

        for (int i = 0; i < 7; i++)
        {
            cut.FindAll($".weekly-day-card[data-day-index='{i}'] .day-category-chip").Should().BeEmpty();
        }

        // No duplicate or invented categories created
        var allCats = await bundle.CategoryService.GetCategoriesAsync();
        allCats.Should().ContainSingle(c => c.Name == "Snabblagat");
    }
}
