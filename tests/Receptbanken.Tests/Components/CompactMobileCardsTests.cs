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
using Receptbanken.Web.Components.WeeklyMenu;
using Receptbanken.Web.Services;

namespace Receptbanken.Tests.Components;

public class CompactMobileCardsTests : TestContext
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
        public required FamilyMemberService FamilyMemberService { get; init; }
        public required RecipeRatingService RecipeRatingService { get; init; }
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
        var dateProvider = new FixedLocalDateProvider(localToday ?? new DateOnly(2026, 11, 21));

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
            FamilyMemberService = memberService,
            RecipeRatingService = ratingService,
            WeeklyMenuService = weeklyMenuService
        };
    }

    [Fact]
    public async Task TodaysDinner_CompactMobileMarkup_HasSingleSetOfControlsAndCompactLabels()
    {
        var today = new DateOnly(2026, 11, 21);
        using var bundle = SetupServices(today);
        const string longTitle = "Långkokt högrevsgryta med rödvin, rosmarin, vitlök och krämig potatispuré";
        var recipe = await bundle.RecipeService.CreateRecipeAsync(new Recipe
        {
            Title = longTitle,
            ImagePath = "images/recipes/gryta.jpg",
            CookTimeMinutes = 90,
            Servings = 4
        });
        await bundle.FamilyMemberService.CreateAsync("Anna");

        var dayIndex = CalendarWeek.GetDayIndex(today);
        var menu = await bundle.WeeklyMenuService.GetOrCreateWeekMenuAsync(today);
        menu.Days[dayIndex].RecipeId = recipe.Id;
        menu.Days[dayIndex].Servings = 4;
        await bundle.WeeklyMenuService.ConfirmMenuAsync(menu);

        var cut = RenderComponent<Home>();
        cut.WaitForAssertion(() => cut.Find(".todays-dinner-card").ClassList.Should().Contain("has-image"));

        var card = cut.Find(".todays-dinner-card");

        // No duplicate interactive controls
        card.QuerySelectorAll("a.todays-dinner-cook-btn").Should().ContainSingle();
        card.QuerySelectorAll("a.todays-dinner-view-btn").Should().ContainSingle();
        card.QuerySelectorAll("button.todays-dinner-rating-badge").Should().ContainSingle();

        // Compact mobile labels + desktop labels present in single semantic controls
        var cookBtn = card.QuerySelector("a.todays-dinner-cook-btn")!;
        cookBtn.GetAttribute("href").Should().Be($"/recept/{recipe.Id}/laga?portioner=4");
        cookBtn.QuerySelector(".todays-dinner-label-mobile")!.TextContent.Should().Be("Laga!");
        cookBtn.QuerySelector(".todays-dinner-label-desktop")!.TextContent.Should().Contain("Starta matlagningsläge");

        var viewBtn = card.QuerySelector("a.todays-dinner-view-btn")!;
        viewBtn.GetAttribute("href").Should().Be($"/recept/{recipe.Id}");
        viewBtn.QuerySelector(".todays-dinner-label-mobile")!.TextContent.Should().Be("Recept");

        // Unrated compact CTA is "Rösta"
        var ratingBtn = card.QuerySelector("button.todays-dinner-rating-badge")!;
        ratingBtn.QuerySelector(".recipe-rating-badge-cta-compact")!.TextContent.Should().Be("Rösta");
        ratingBtn.QuerySelector(".recipe-rating-badge-cta-full")!.TextContent.Should().Be("Betygsätt");

        // Long title and compact image present (no overlay gradient)
        card.QuerySelector(".todays-dinner-title")!.TextContent.Trim().Should().Be(longTitle);
        card.QuerySelector(".todays-dinner-image-wrap").Should().NotBeNull();
        card.QuerySelector(".todays-dinner-image-gradient").Should().BeNull();

        // Clicking rating button opens shared modal and updates to rated score
        ratingBtn.Click();
        cut.WaitForAssertion(() => cut.FindAll(".rating-member-row").Should().HaveCount(1));
        cut.FindAll(".rating-star-btn").ElementAt(4).Click();
        cut.WaitForAssertion(() =>
        {
            var updatedRating = cut.Find("button.todays-dinner-rating-badge");
            updatedRating.TextContent.Should().Contain("★").And.Contain("5,0");
        });
    }

    [Fact]
    public async Task TodaysDinner_WithoutImage_RendersGracefulFallbackWithoutImageWrapper()
    {
        var today = new DateOnly(2026, 11, 21);
        using var bundle = SetupServices(today);
        var recipe = await bundle.RecipeService.CreateRecipeAsync(new Recipe
        {
            Title = "Enkel tomatsoppa",
            CookTimeMinutes = 25,
            Servings = 4
        });
        await bundle.FamilyMemberService.CreateAsync("Anna");

        var dayIndex = CalendarWeek.GetDayIndex(today);
        var menu = await bundle.WeeklyMenuService.GetOrCreateWeekMenuAsync(today);
        menu.Days[dayIndex].RecipeId = recipe.Id;
        await bundle.WeeklyMenuService.SaveDraftAsync(menu);

        var cut = RenderComponent<Home>();
        cut.WaitForAssertion(() => cut.Find(".todays-dinner-card").ClassList.Should().Contain("no-image"));

        var card = cut.Find(".todays-dinner-card");
        card.QuerySelector(".todays-dinner-image-wrap").Should().BeNull();
        card.QuerySelectorAll("a.todays-dinner-cook-btn").Should().ContainSingle();
        card.QuerySelectorAll("a.todays-dinner-view-btn").Should().ContainSingle();
        card.QuerySelectorAll("button.todays-dinner-rating-badge").Should().ContainSingle();
        card.QuerySelector(".todays-dinner-title")!.TextContent.Trim().Should().Be("Enkel tomatsoppa");
    }

    [Fact]
    public async Task WeeklyMenuDayCard_CompactLayout_SeparatesServingsFromMetaAndUsesCompactLabels()
    {
        var today = new DateOnly(2026, 11, 2);
        using var bundle = SetupServices(today);
        var anna = await bundle.FamilyMemberService.CreateAsync("Anna");
        var erik = await bundle.FamilyMemberService.CreateAsync("Erik");

        var rated = await bundle.RecipeService.CreateRecipeAsync(new Recipe
        {
            Title = "Ugnsbakad lax",
            PrepTimeMinutes = 15,
            CookTimeMinutes = 25,
            Servings = 4
        });
        await bundle.RecipeRatingService.SetRatingAsync(rated.Id, anna.Id, 5);
        await bundle.RecipeRatingService.SetRatingAsync(rated.Id, erik.Id, 4); // 4,5 (2)

        var menu = await bundle.WeeklyMenuService.GetOrCreateWeekMenuAsync(today);
        menu.Days[0].RecipeId = rated.Id;
        menu.Days[0].Servings = 4;
        await bundle.WeeklyMenuService.ConfirmMenuAsync(menu);

        var cut = RenderComponent<WeeklyMenuPage>();
        cut.WaitForAssertion(() => cut.FindAll(".weekly-day-card.has-recipe").Should().HaveCount(1));

        var mondayCard = cut.Find(".weekly-day-card[data-day-index='0']");

        // Compact cooking time & read-only rating in meta
        var meta = mondayCard.QuerySelector(".weekly-day-meta")!;
        meta.TextContent.Should().Contain("40 min");
        meta.QuerySelector(".weekly-day-rating")!.TextContent.Should().Contain("★").And.Contain("4,5 (2 röster)");
        meta.QuerySelector(".weekly-day-rating-votes-unit")!.TextContent.Should().Be(" röster");

        // Portion adjuster is in its own row, not inside .weekly-day-meta
        meta.QuerySelector(".weekly-day-servings-control").Should().BeNull();
        var servingsRow = mondayCard.QuerySelector(".weekly-day-servings-row")!;
        servingsRow.QuerySelector(".weekly-day-servings-control").Should().NotBeNull();

        // Single set of 3 action buttons with compact mobile labels
        mondayCard.QuerySelectorAll(".weekly-day-actions .weekly-day-view-link").Should().ContainSingle();
        mondayCard.QuerySelectorAll(".weekly-day-actions .weekly-day-reroll-btn").Should().ContainSingle();
        mondayCard.QuerySelectorAll(".weekly-day-actions .weekly-day-change-recipe-toggle").Should().ContainSingle();

        mondayCard.QuerySelector(".weekly-day-view-link .weekly-action-label-mobile")!.TextContent.Should().Be("Recept");
        mondayCard.QuerySelector(".weekly-day-reroll-btn .weekly-action-label-desktop-suffix")!.TextContent.Should().Be(" nytt");
        mondayCard.QuerySelector(".weekly-day-change-recipe-toggle .weekly-action-label-desktop-suffix")!.TextContent.Should().Be(" recept…");

        // Empty day card (Tuesday) renders gracefully without recipe
        var tuesdayCard = cut.Find(".weekly-day-card[data-day-index='1']");
        tuesdayCard.ClassList.Should().Contain("is-empty");
        tuesdayCard.QuerySelector(".weekly-day-empty-text")!.TextContent.Should().Contain("Inget recept valt");

        // Adjusting servings preserves logic & marks unsaved changes on confirmed menu
        mondayCard.QuerySelectorAll(".servings-btn")[1].Click();
        cut.WaitForAssertion(() =>
        {
            cut.Find(".weekly-day-card[data-day-index='0'] .servings-count").TextContent.Should().Contain("5 portioner");
            cut.Find(".weekly-status-badge.status-unsaved").TextContent.Should().Contain("Osparade ändringar");
        });
    }

    [Fact]
    public async Task WeeklyMenuDayCard_CategoryToggle_PreservesSelectedCategoriesWhenCollapsed()
    {
        var today = new DateOnly(2026, 11, 2);
        using var bundle = SetupServices(today);
        var cats = await bundle.CategoryService.GetCategoriesAsync();
        var vardag = cats.First(c => c.Name == "Vardagsmat");

        var recipe = await bundle.RecipeService.CreateRecipeAsync(
            new Recipe { Title = "Pasta Carbonara", CookTimeMinutes = 20, Servings = 4 },
            [vardag.Id]);

        var menu = await bundle.WeeklyMenuService.GetOrCreateWeekMenuAsync(today);
        menu.Days[0].RecipeId = recipe.Id;
        await bundle.WeeklyMenuService.SaveDraftAsync(menu);

        var cut = RenderComponent<WeeklyMenuPage>();
        cut.WaitForAssertion(() => cut.FindAll(".weekly-day-card.has-recipe").Should().HaveCount(1));

        // Remove existing default category -> toggle has no count and aria-label reflects 0 selected
        foreach (var removeBtn in cut.FindAll(".weekly-day-card[data-day-index='0'] .day-category-remove-btn").ToList())
        {
            removeBtn.Click();
        }

        cut.WaitForAssertion(() =>
        {
            var toggle = cut.Find(".weekly-day-card[data-day-index='0'] .weekly-day-category-toggle");
            toggle.ClassList.Should().NotContain("has-selected");
            toggle.QuerySelector(".weekly-day-category-count").Should().BeNull();
            toggle.GetAttribute("aria-label").Should().Contain("inga valda");
            toggle.GetAttribute("aria-expanded").Should().Be("false");
        });

        // Open picker via compact header toggle
        cut.Find(".weekly-day-card[data-day-index='0'] .weekly-day-category-toggle").Click();
        cut.WaitForAssertion(() =>
        {
            cut.Find(".weekly-day-card[data-day-index='0'] .weekly-day-category-toggle")
                .GetAttribute("aria-expanded").Should().Be("true");
            cut.Find(".weekly-day-card[data-day-index='0'] .weekly-day-category-picker").Should().NotBeNull();
        });

        // Select category
        cut.Find($".weekly-day-card[data-day-index='0'] .day-category-option[data-option-category-id='{vardag.Id}']").Click();
        cut.WaitForAssertion(() =>
        {
            var toggle = cut.Find(".weekly-day-card[data-day-index='0'] .weekly-day-category-toggle");
            toggle.ClassList.Should().Contain("has-selected");
            toggle.QuerySelector(".weekly-day-category-count")!.TextContent.Should().Be("1");
            toggle.GetAttribute("aria-label").Should().Contain("1 vald");
        });

        // Collapse picker -> selected category must NOT be cleared
        cut.Find(".weekly-day-card[data-day-index='0'] .weekly-day-category-toggle").Click();
        cut.WaitForAssertion(() =>
        {
            cut.FindAll(".weekly-day-card[data-day-index='0'] .weekly-day-category-picker").Should().BeEmpty();
            var toggle = cut.Find(".weekly-day-card[data-day-index='0'] .weekly-day-category-toggle");
            toggle.GetAttribute("aria-expanded").Should().Be("false");
            toggle.ClassList.Should().Contain("has-selected");
            toggle.QuerySelector(".weekly-day-category-count")!.TextContent.Should().Be("1");
        });
    }

    [Fact]
    public async Task TodaysDinner_PreRatedRecipe_DisplaysRatingAndAllThreeActions()
    {
        var today = new DateOnly(2026, 11, 21);
        using var bundle = SetupServices(today);
        var anna = await bundle.FamilyMemberService.CreateAsync("Anna");
        var erik = await bundle.FamilyMemberService.CreateAsync("Erik");

        var recipe = await bundle.RecipeService.CreateRecipeAsync(new Recipe
        {
            Title = "Krämig svamppasta",
            ImagePath = "images/recipes/svamppasta.jpg",
            CookTimeMinutes = 25,
            Servings = 4
        });
        await bundle.RecipeRatingService.SetRatingAsync(recipe.Id, anna.Id, 5);
        await bundle.RecipeRatingService.SetRatingAsync(recipe.Id, erik.Id, 4);

        var dayIndex = CalendarWeek.GetDayIndex(today);
        var menu = await bundle.WeeklyMenuService.GetOrCreateWeekMenuAsync(today);
        menu.Days[dayIndex].RecipeId = recipe.Id;
        menu.Days[dayIndex].Servings = 4;
        await bundle.WeeklyMenuService.ConfirmMenuAsync(menu);

        var cut = RenderComponent<Home>();
        cut.WaitForAssertion(() => cut.Find(".todays-dinner-card").ClassList.Should().Contain("has-image"));

        var card = cut.Find(".todays-dinner-card");
        card.QuerySelectorAll("a.todays-dinner-cook-btn").Should().ContainSingle();
        card.QuerySelectorAll("a.todays-dinner-view-btn").Should().ContainSingle();
        card.QuerySelectorAll("button.todays-dinner-rating-badge").Should().ContainSingle();

        var ratingBtn = card.QuerySelector("button.todays-dinner-rating-badge")!;
        ratingBtn.TextContent.Should().Contain("★").And.Contain("4,5");
    }

    [Fact]
    public async Task WeeklyMenuDayCard_PortionAdjuster_RespectsBoundsAndAccessibleLabels()
    {
        var today = new DateOnly(2026, 11, 2);
        using var bundle = SetupServices(today);
        var recipe = await bundle.RecipeService.CreateRecipeAsync(new Recipe
        {
            Title = "Köttfärssås",
            CookTimeMinutes = 30,
            Servings = 1
        });

        var menu = await bundle.WeeklyMenuService.GetOrCreateWeekMenuAsync(today);
        menu.Days[0].RecipeId = recipe.Id;
        menu.Days[0].Servings = 1;
        await bundle.WeeklyMenuService.ConfirmMenuAsync(menu);

        var cut = RenderComponent<WeeklyMenuPage>();
        cut.WaitForAssertion(() => cut.FindAll(".weekly-day-card.has-recipe").Should().HaveCount(1));

        var mondayCard = cut.Find(".weekly-day-card[data-day-index='0']");
        var decBtn = mondayCard.QuerySelectorAll(".servings-btn")[0];
        var incBtn = mondayCard.QuerySelectorAll(".servings-btn")[1];
        var countSpan = mondayCard.QuerySelector(".servings-count")!;

        decBtn.GetAttribute("aria-label").Should().Be("Minska portioner");
        incBtn.GetAttribute("aria-label").Should().Be("Öka portioner");
        decBtn.HasAttribute("disabled").Should().BeTrue();
        countSpan.GetAttribute("aria-label").Should().Be("1 portioner");
        countSpan.QuerySelector(".servings-unit")!.TextContent.Should().Be("portioner");

        incBtn.Click();
        cut.WaitForAssertion(() =>
        {
            var updated = cut.Find(".weekly-day-card[data-day-index='0'] .servings-count");
            updated.GetAttribute("aria-label").Should().Be("2 portioner");
            cut.Find(".weekly-day-card[data-day-index='0']").QuerySelectorAll(".servings-btn")[0].HasAttribute("disabled").Should().BeFalse();
        });
    }

    [Fact]
    public async Task WeeklyMenuDayCard_MultipleCategories_UpdatesCountAndPreservesOnCollapse()
    {
        var today = new DateOnly(2026, 11, 2);
        using var bundle = SetupServices(today);
        var cats = await bundle.CategoryService.GetCategoriesAsync();
        var vardag = cats.First(c => c.Name == "Vardagsmat");
        var pasta = cats.First(c => c.Name == "Pasta");

        var cut = RenderComponent<WeeklyMenuPage>();
        cut.WaitForAssertion(() => cut.FindAll(".weekly-day-card").Should().HaveCount(7));

        // Monday starts with 1 default category (Vardagsmat)
        var toggle = cut.Find(".weekly-day-card[data-day-index='0'] .weekly-day-category-toggle");
        toggle.QuerySelector(".weekly-day-category-count")!.TextContent.Should().Be("1");
        toggle.GetAttribute("aria-label").Should().Contain("1 vald");

        // Expand and add Pasta -> count becomes 2
        toggle.Click();
        cut.Find($".weekly-day-card[data-day-index='0'] .day-category-option[data-option-category-id='{pasta.Id}']").Click();

        cut.WaitForAssertion(() =>
        {
            var updatedToggle = cut.Find(".weekly-day-card[data-day-index='0'] .weekly-day-category-toggle");
            updatedToggle.QuerySelector(".weekly-day-category-count")!.TextContent.Should().Be("2");
            updatedToggle.GetAttribute("aria-label").Should().Contain("2 valda");
        });

        // Collapse -> still 2
        cut.Find(".weekly-day-card[data-day-index='0'] .weekly-day-category-toggle").Click();
        cut.WaitForAssertion(() =>
        {
            cut.FindAll(".weekly-day-card[data-day-index='0'] .weekly-day-category-picker").Should().BeEmpty();
            cut.Find(".weekly-day-card[data-day-index='0'] .weekly-day-category-toggle .weekly-day-category-count").TextContent.Should().Be("2");
        });
    }

    [Fact]
    public async Task WeeklyMenuPage_ToolbarAndCollapsiblePantry_ReflectUnsavedStateAndRetainValues()
    {
        var today = new DateOnly(2026, 11, 2);
        using var bundle = SetupServices(today);
        var cats = await bundle.CategoryService.GetCategoriesAsync();
        var vardag = cats.First(c => c.Name == "Vardagsmat");

        var recipe = await bundle.RecipeService.CreateRecipeAsync(
            new Recipe { Title = "Fiskgratäng", CookTimeMinutes = 35, Servings = 4 },
            [vardag.Id]);

        var menu = await bundle.WeeklyMenuService.GetOrCreateWeekMenuAsync(today);
        menu.Days[0].RecipeId = recipe.Id;
        menu.Days[0].Servings = 4;
        await bundle.WeeklyMenuService.ConfirmMenuAsync(menu);

        var cut = RenderComponent<WeeklyMenuPage>();
        cut.WaitForAssertion(() => cut.FindAll(".weekly-day-card.has-recipe").Should().HaveCount(1));

        // Confirmed without unsaved changes -> confirm-week-btn group is marked clean (hidden on mobile)
        cut.Find(".weekly-confirm-action-group").ClassList.Should().Contain("is-confirmed-clean");

        // Pantry defaults to collapsed
        var pantryToggle = cut.Find(".weekly-pantry-toggle");
        pantryToggle.GetAttribute("aria-expanded").Should().Be("false");
        cut.Find("#weekly-pantry-body").ClassList.Should().Contain("is-collapsed");

        // Expand pantry, add ingredient, then collapse -> value & count badge retained
        pantryToggle.Click();
        cut.WaitForAssertion(() =>
        {
            cut.Find(".weekly-pantry-toggle").GetAttribute("aria-expanded").Should().Be("true");
            cut.Find("#weekly-pantry-body").ClassList.Should().Contain("is-expanded");
        });

        cut.Find("input.pantry-input").Input("Kycklingfilé");
        cut.Find("button.pantry-add-btn").Click();

        cut.WaitForAssertion(() =>
        {
            cut.Find(".weekly-pantry-count-badge").TextContent.Should().Be("(1)");
            // Adding pantry marks unsaved changes -> confirm button becomes prominent (not clean)
            cut.Find(".weekly-confirm-action-group").ClassList.Should().NotContain("is-confirmed-clean");
        });

        cut.Find(".weekly-pantry-toggle").Click();
        cut.WaitForAssertion(() =>
        {
            cut.Find(".weekly-pantry-toggle").GetAttribute("aria-expanded").Should().Be("false");
            cut.Find(".weekly-pantry-count-badge").TextContent.Should().Be("(1)");
            cut.FindAll(".pantry-tag-chip").Should().ContainSingle(e => e.TextContent.Contains("Kycklingfilé"));
        });

        // "Fler åtgärder" toggles restore and clear actions
        var moreToggle = cut.Find(".weekly-more-actions-toggle");
        moreToggle.GetAttribute("aria-expanded").Should().Be("false");
        cut.Find("#weekly-more-actions-menu").ClassList.Should().NotContain("is-open");

        moreToggle.Click();
        cut.WaitForAssertion(() =>
        {
            cut.Find(".weekly-more-actions-toggle").GetAttribute("aria-expanded").Should().Be("true");
            cut.Find("#weekly-more-actions-menu").ClassList.Should().Contain("is-open");
            cut.Find("button.discard-changes-btn").Should().NotBeNull();
            cut.Find("button.clear-menu-btn").Should().NotBeNull();
        });
    }

    [Fact]
    public async Task WeeklyMenuPage_DefaultCategories_AppliedOnNewWeekAndPreservedOnExistingSavedWeeks()
    {
        var week1Date = new DateOnly(2026, 11, 2); // Monday
        var week2Date = week1Date.AddDays(7);
        var week3Date = week1Date.AddDays(14);
        using var bundle = SetupServices(week1Date);

        var cats = await bundle.CategoryService.GetCategoriesAsync();
        var pasta = cats.First(c => c.Name == "Pasta");

        // Save week 1 with custom category on Monday and intentionally empty categories on Tuesday
        var week1Menu = await bundle.WeeklyMenuService.GetOrCreateWeekMenuAsync(week1Date);
        week1Menu.Days[0].SelectedCategories = [new WeeklyMenuDayCategory { CategoryId = pasta.Id }];
        week1Menu.Days[1].SelectedCategories = [];
        await bundle.WeeklyMenuService.SaveDraftAsync(week1Menu);

        // Save week 2 with all days intentionally empty
        var week2Menu = await bundle.WeeklyMenuService.GetOrCreateWeekMenuAsync(week2Date);
        foreach (var d in week2Menu.Days)
        {
            d.SelectedCategories = [];
        }
        await bundle.WeeklyMenuService.SaveDraftAsync(week2Menu);

        // Render week 1 -> custom & empty categories preserved
        var cut = RenderComponent<WeeklyMenuPage>();
        cut.WaitForAssertion(() => cut.FindAll(".weekly-day-card").Should().HaveCount(7));

        cut.Find(".weekly-day-card[data-day-index='0'] .day-category-chip").TextContent.Should().Contain("Pasta");
        cut.FindAll(".weekly-day-card[data-day-index='1'] .day-category-chip").Should().BeEmpty();

        // Navigate to week 2 -> intentionally empty categories preserved (never reapplied)
        cut.Find("button.week-next-btn").Click();
        cut.WaitForAssertion(() => cut.Find(".page-title").TextContent.Should().Contain($"Vecka {CalendarWeek.FromDate(week2Date).IsoWeek}"));
        for (int i = 0; i < 7; i++)
        {
            cut.FindAll($".weekly-day-card[data-day-index='{i}'] .day-category-chip").Should().BeEmpty();
        }

        // Navigate to week 3 (genuinely new week) -> weekday defaults applied
        cut.Find("button.week-next-btn").Click();
        cut.WaitForAssertion(() => cut.Find(".page-title").TextContent.Should().Contain($"Vecka {CalendarWeek.FromDate(week3Date).IsoWeek}"));
        for (int i = 0; i < 7; i++)
        {
            var expected = (i is 4 or 5) ? "Fest & Helg" : "Vardagsmat";
            cut.Find($".weekly-day-card[data-day-index='{i}'] .day-category-chip").TextContent.Should().Contain(expected);
        }
    }

    [Fact]
    public async Task WeeklyMenuPage_RatingNotifierUpdate_DoesNotResetUnsavedEditsOrCategories()
    {
        var today = new DateOnly(2026, 11, 2);
        using var bundle = SetupServices(today);
        var anna = await bundle.FamilyMemberService.CreateAsync("Anna");
        var cats = await bundle.CategoryService.GetCategoriesAsync();
        var pasta = cats.First(c => c.Name == "Pasta");

        var recipe = await bundle.RecipeService.CreateRecipeAsync(new Recipe
        {
            Title = "Pestopasta",
            CookTimeMinutes = 15,
            Servings = 4
        });

        var menu = await bundle.WeeklyMenuService.GetOrCreateWeekMenuAsync(today);
        menu.Days[0].RecipeId = recipe.Id;
        menu.Days[0].Servings = 4;
        await bundle.WeeklyMenuService.ConfirmMenuAsync(menu);

        var cut = RenderComponent<WeeklyMenuPage>();
        cut.WaitForAssertion(() => cut.FindAll(".weekly-day-card.has-recipe").Should().HaveCount(1));

        // Make unsaved edits: add Pasta category on Monday and increment servings to 5
        cut.Find(".weekly-day-card[data-day-index='0'] .weekly-day-category-toggle").Click();
        cut.Find($".weekly-day-card[data-day-index='0'] .day-category-option[data-option-category-id='{pasta.Id}']").Click();
        cut.FindAll(".weekly-day-card[data-day-index='0'] .servings-btn").ElementAt(1).Click();

        cut.WaitForAssertion(() =>
        {
            cut.Find(".weekly-status-badge.status-unsaved").Should().NotBeNull();
            cut.Find(".weekly-day-card[data-day-index='0'] .servings-count").TextContent.Should().Contain("5 portioner");
            cut.Find(".weekly-day-card[data-day-index='0'] .weekly-day-category-count").TextContent.Should().Be("2");
        });

        // Trigger rating update via RecipeRatingService
        await bundle.RecipeRatingService.SetRatingAsync(recipe.Id, anna.Id, 5);

        cut.WaitForAssertion(() =>
        {
            cut.Find(".weekly-day-card[data-day-index='0'] .weekly-day-rating").TextContent.Should().Contain("5,0");
            cut.Find(".weekly-status-badge.status-unsaved").Should().NotBeNull();
            cut.Find(".weekly-day-card[data-day-index='0'] .servings-count").TextContent.Should().Contain("5 portioner");
            cut.Find(".weekly-day-card[data-day-index='0'] .weekly-day-category-count").TextContent.Should().Be("2");
        });
    }
}
