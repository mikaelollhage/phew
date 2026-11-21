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

public class WeeklyMenuUx22Tests : TestContext
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

    private TestBundle SetupServices(DateOnly? localToday = null)
    {
        JSInterop.Mode = JSRuntimeMode.Loose;

        var dbFactory = new TestDbContextFactory();
        using (var db = dbFactory.CreateDbContext())
        {
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
    public async Task ClearWeeklyMenu_ResetsRecipesAndDefaultCategories_PreservesPantry_AndDoesNotAffectOtherWeeks()
    {
        var week1 = new DateOnly(2026, 11, 2);
        var week2 = week1.AddDays(7);
        using var bundle = SetupServices(week1);
        var cats = await bundle.CategoryService.GetCategoriesAsync();
        var pasta = cats.First(c => c.Name == "Pasta");

        var recipe = await bundle.RecipeService.CreateRecipeAsync(
            new Recipe { Title = "Krämig pasta", Servings = 4 },
            [pasta.Id]);

        var menu1 = await bundle.WeeklyMenuService.GetOrCreateWeekMenuAsync(week1);
        menu1.PantryIngredients = ["Lök"];
        menu1.Days[0].RecipeId = recipe.Id;
        menu1.Days[0].Servings = 6;
        menu1.Days[0].SelectedCategories = [new WeeklyMenuDayCategory { CategoryId = pasta.Id }];
        menu1.Days[4].SelectedCategories.Clear();
        await bundle.WeeklyMenuService.ConfirmMenuAsync(menu1);

        var menu2 = await bundle.WeeklyMenuService.GetOrCreateWeekMenuAsync(week2);
        menu2.Days[0].RecipeId = recipe.Id;
        menu2.Days[0].SelectedCategories = [new WeeklyMenuDayCategory { CategoryId = pasta.Id }];
        await bundle.WeeklyMenuService.ConfirmMenuAsync(menu2);

        var cut = RenderComponent<WeeklyMenuPage>();
        cut.WaitForAssertion(() => cut.FindAll(".weekly-day-card.has-recipe").Should().HaveCount(1));

        cut.Find(".weekly-more-actions-toggle").Click();
        cut.Find("button.clear-menu-btn").Click();
        cut.WaitForAssertion(() => cut.Find(".modal-dialog-custom button.btn-danger-custom").Should().NotBeNull());
        cut.Find(".modal-dialog-custom button.btn-danger-custom").Click();

        cut.WaitForAssertion(() =>
        {
            cut.FindAll(".weekly-day-card.has-recipe").Should().BeEmpty();
            cut.Find(".weekly-status-badge").TextContent.Should().Contain("Utkast");
            cut.Find(".weekly-pantry-count-badge").TextContent.Should().Be("(1)");
            for (int i = 0; i < 7; i++)
            {
                var expectedName = i is 4 or 5 ? "Fest & Helg" : "Vardagsmat";
                cut.Find($".weekly-day-card[data-day-index='{i}'] .day-category-chip")
                    .TextContent.Should().Contain(expectedName);
            }
        });

        // Reload week 1 and verify persistence
        var reloaded = RenderComponent<WeeklyMenuPage>();
        reloaded.WaitForAssertion(() => reloaded.FindAll(".weekly-day-card").Should().HaveCount(7));
        reloaded.FindAll(".weekly-day-card.has-recipe").Should().BeEmpty();
        reloaded.Find(".weekly-day-card[data-day-index='0'] .day-category-chip").TextContent.Should().Contain("Vardagsmat");
        reloaded.Find(".weekly-day-card[data-day-index='4'] .day-category-chip").TextContent.Should().Contain("Fest & Helg");

        // Week 2 remains untouched
        var savedWeek2 = await bundle.WeeklyMenuService.GetSavedMenuForWeekAsync(week2);
        savedWeek2!.Status.Should().Be(WeeklyMenuStatus.Confirmed);
        savedWeek2.Days[0].RecipeId.Should().Be(recipe.Id);
        savedWeek2.Days[0].SelectedCategories.Should().ContainSingle(sc => sc.CategoryId == pasta.Id);
    }

    [Fact]
    public async Task RestoreDefaultCategories_PreservesRecipesPortionsAndPantry_MarksUnsaved_AndPersistsOnSave()
    {
        var today = new DateOnly(2026, 11, 2);
        using var bundle = SetupServices(today);
        var cats = await bundle.CategoryService.GetCategoriesAsync();
        var pasta = cats.First(c => c.Name == "Pasta");

        var recipe = await bundle.RecipeService.CreateRecipeAsync(
            new Recipe { Title = "Pasta Carbonara", Servings = 4 },
            [pasta.Id]);

        var menu = await bundle.WeeklyMenuService.GetOrCreateWeekMenuAsync(today);
        menu.PantryIngredients = ["Bacon", "Ägg"];
        menu.Days[0].RecipeId = recipe.Id;
        menu.Days[0].Servings = 6;
        menu.Days[0].SelectedCategories = [new WeeklyMenuDayCategory { CategoryId = pasta.Id }];
        menu.Days[5].SelectedCategories.Clear();
        await bundle.WeeklyMenuService.SaveDraftAsync(menu);

        var cut = RenderComponent<WeeklyMenuPage>();
        cut.WaitForAssertion(() => cut.FindAll(".weekly-day-card.has-recipe").Should().HaveCount(1));

        cut.Find(".weekly-more-actions-toggle").Click();
        cut.Find("button.reset-categories-btn").Click();

        cut.WaitForAssertion(() =>
        {
            var dialog = cut.Find(".modal-dialog-custom");
            dialog.TextContent.Should().Contain("Återställ standardkategorier?");
            dialog.TextContent.Should().Contain("ersätts med veckans standardkategorier");
        });

        cut.Find(".modal-dialog-custom button.btn-primary-custom").Click();

        cut.WaitForAssertion(() =>
        {
            cut.Find(".weekly-status-badge.status-unsaved").TextContent.Should().Contain("Osparade ändringar");
            // Recipe, servings, and pantry preserved
            cut.Find(".weekly-day-card[data-day-index='0'] .weekly-day-recipe-title").TextContent.Should().Contain("Pasta Carbonara");
            cut.Find(".weekly-day-card[data-day-index='0'] .servings-count").TextContent.Should().Contain("6 portioner");
            cut.Find(".weekly-pantry-count-badge").TextContent.Should().Be("(2)");

            for (int i = 0; i < 7; i++)
            {
                var expectedName = i is 4 or 5 ? "Fest & Helg" : "Vardagsmat";
                cut.Find($".weekly-day-card[data-day-index='{i}'] .day-category-chip")
                    .TextContent.Should().Contain(expectedName);
            }
        });

        // Save draft and reload -> restored categories persist
        cut.Find("button.save-draft-btn").Click();
        cut.WaitForAssertion(() => cut.FindAll(".weekly-status-badge.status-unsaved").Should().BeEmpty());

        var reloaded = RenderComponent<WeeklyMenuPage>();
        reloaded.WaitForAssertion(() => reloaded.FindAll(".weekly-day-card.has-recipe").Should().HaveCount(1));
        reloaded.Find(".weekly-day-card[data-day-index='0'] .day-category-chip").TextContent.Should().Contain("Vardagsmat");
        reloaded.Find(".weekly-day-card[data-day-index='5'] .day-category-chip").TextContent.Should().Contain("Fest & Helg");
    }

    [Fact]
    public async Task ExistingSavedEmptyDraft_CanBeRepairedWithRestoreDefaultCategories_WhileManualClearRemainsRespected()
    {
        var today = new DateOnly(2026, 11, 2);
        using var bundle = SetupServices(today);

        // Create legacy empty draft with 0 categories on all 7 days
        var emptyDraft = new WeeklyMenu
        {
            WeekStartDate = today,
            IsoYear = CalendarWeek.FromDate(today).IsoYear,
            IsoWeek = CalendarWeek.FromDate(today).IsoWeek,
            Status = WeeklyMenuStatus.Draft
        };
        WeeklyMenuGenerator.EnsureSevenDaysInitialized(emptyDraft);
        await bundle.WeeklyMenuService.SaveDraftAsync(emptyDraft);

        var cut = RenderComponent<WeeklyMenuPage>();
        cut.WaitForAssertion(() => cut.FindAll(".weekly-day-card").Should().HaveCount(7));

        // Ordinary load does NOT silently rewrite empty draft
        for (int i = 0; i < 7; i++)
        {
            cut.FindAll($".weekly-day-card[data-day-index='{i}'] .day-category-chip").Should().BeEmpty();
        }

        // Repair manually via "Återställ standardkategorier"
        cut.Find(".weekly-more-actions-toggle").Click();
        cut.Find("button.reset-categories-btn").Click();
        cut.WaitForAssertion(() => cut.Find(".modal-dialog-custom button.btn-primary-custom").Should().NotBeNull());
        cut.Find(".modal-dialog-custom button.btn-primary-custom").Click();

        cut.WaitForAssertion(() =>
        {
            cut.Find(".weekly-day-card[data-day-index='0'] .day-category-chip").TextContent.Should().Contain("Vardagsmat");
            cut.Find(".weekly-day-card[data-day-index='4'] .day-category-chip").TextContent.Should().Contain("Fest & Helg");
        });

        // Manually clear Monday again, then save and reload -> intentional clearing respected
        cut.Find(".weekly-day-card[data-day-index='0'] .day-category-remove-btn").Click();
        cut.Find("button.save-draft-btn").Click();
        cut.WaitForAssertion(() => cut.FindAll(".weekly-status-badge.status-unsaved").Should().BeEmpty());

        var reloaded = RenderComponent<WeeklyMenuPage>();
        reloaded.WaitForAssertion(() => reloaded.FindAll(".weekly-day-card").Should().HaveCount(7));
        reloaded.FindAll(".weekly-day-card[data-day-index='0'] .day-category-chip").Should().BeEmpty();
        reloaded.Find(".weekly-day-card[data-day-index='4'] .day-category-chip").TextContent.Should().Contain("Fest & Helg");
    }

    [Fact]
    public async Task CategoryPickerChips_HaveNoPlusOrCheckSymbols_AndExposeAriaPressedAndLabels()
    {
        var today = new DateOnly(2026, 11, 2);
        using var bundle = SetupServices(today);
        var cats = await bundle.CategoryService.GetCategoriesAsync();
        var vardag = cats.First(c => c.Name == "Vardagsmat");
        var pasta = cats.First(c => c.Name == "Pasta");

        var cut = RenderComponent<WeeklyMenuPage>();
        cut.WaitForAssertion(() => cut.FindAll(".weekly-day-card").Should().HaveCount(7));

        var mondayCard = cut.Find(".weekly-day-card[data-day-index='0']");
        mondayCard.QuerySelector(".weekly-day-category-toggle")!.Click();

        var options = cut.FindAll(".weekly-day-card[data-day-index='0'] .day-category-option");
        options.Should().NotBeEmpty();
        foreach (var opt in options)
        {
            opt.GetAttribute("type").Should().Be("button");
            opt.TextContent.Should().NotContain("+").And.NotContain("✓");
            opt.GetAttribute("aria-pressed").Should().BeOneOf("true", "false");
            opt.GetAttribute("aria-label").Should().Contain("Måndag");
            opt.QuerySelector(".category-dot").Should().NotBeNull();
        }

        var vardagBtn = cut.Find($".weekly-day-card[data-day-index='0'] .day-category-option[data-option-category-id='{vardag.Id}']");
        vardagBtn.GetAttribute("aria-pressed").Should().Be("true");
        vardagBtn.ClassList.Should().Contain("selected");

        var pastaBtn = cut.Find($".weekly-day-card[data-day-index='0'] .day-category-option[data-option-category-id='{pasta.Id}']");
        pastaBtn.GetAttribute("aria-pressed").Should().Be("false");
        pastaBtn.ClassList.Should().NotContain("selected");

        // Toggle Pasta on and verify aria-pressed updates
        pastaBtn.Click();
        cut.WaitForAssertion(() =>
        {
            var updatedPasta = cut.Find($".weekly-day-card[data-day-index='0'] .day-category-option[data-option-category-id='{pasta.Id}']");
            updatedPasta.GetAttribute("aria-pressed").Should().Be("true");
            updatedPasta.ClassList.Should().Contain("selected");
        });
    }

    [Fact]
    public async Task SingleActionToolbar_HasNoDuplicateBottomBar_KeepsSaveDraftVisible_AndProvidesAllActions()
    {
        var today = new DateOnly(2026, 11, 2);
        using var bundle = SetupServices(today);

        var cut = RenderComponent<WeeklyMenuPage>();
        cut.WaitForAssertion(() => cut.FindAll(".weekly-day-card").Should().HaveCount(7));

        // Exactly one toolbar above day cards, no bottom bar, no persistent helper text
        cut.FindAll(".weekly-actions-bar").Should().ContainSingle();
        cut.FindAll(".weekly-bottom-bar").Should().BeEmpty();
        cut.FindAll(".weekly-action-hint").Should().BeEmpty();

        // Single instances of each primary/common button
        cut.FindAll("button.generate-week-btn").Should().ContainSingle();
        cut.FindAll("button.save-draft-btn").Should().ContainSingle();
        cut.FindAll("button.create-shopping-list-btn").Should().ContainSingle();
        cut.FindAll("button.confirm-week-btn").Should().ContainSingle();

        // Save Draft is never hidden even on a clean draft
        var saveDraftBtn = cut.Find("button.save-draft-btn");
        saveDraftBtn.HasAttribute("disabled").Should().BeFalse();

        // Secondary actions live inside Fler åtgärder
        cut.FindAll("button.weekly-more-actions-toggle").Should().ContainSingle();
        cut.Find(".weekly-more-actions-toggle").Click();
        cut.WaitForAssertion(() =>
        {
            cut.Find("#weekly-more-actions-menu").ClassList.Should().Contain("is-open");
            cut.Find("button.reset-categories-btn").Should().NotBeNull();
        });
    }
}
