using Bunit;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.FluentUI.AspNetCore.Components;
using Phew.Core.Interfaces;
using Phew.Core.Models;
using Phew.Core.Services;
using Phew.Infrastructure.Data;
using Phew.Infrastructure.Services;
using Phew.Tests.Infrastructure;
using Phew.Web.Components.Pages;
using Phew.Web.Components.Recipes;
using Phew.Web.Services;

namespace Phew.Tests.Components;

public class RecipeRatingComponentTests : TestContext
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
        public required FamilyMemberService FamilyMemberService { get; init; }
        public required RecipeRatingService RecipeRatingService { get; init; }
        public required RecipeRatingNotifier RatingNotifier { get; init; }
        public required FamilyMemberNotifier MemberNotifier { get; init; }
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
            FamilyMemberService = memberService,
            RecipeRatingService = ratingService,
            RatingNotifier = ratingNotifier,
            MemberNotifier = memberNotifier,
            WeeklyMenuService = weeklyMenuService
        };
    }

    [Fact]
    public async Task RecipeDetail_ShouldShowUnratedBadge_ThenOpenModalAndUpdateBadgeAfterRating()
    {
        using var bundle = SetupServices();
        var recipe = await bundle.RecipeService.CreateRecipeAsync(new Recipe { Title = "Fiskgratäng", Servings = 4 });
        await bundle.FamilyMemberService.CreateAsync("Anna");
        await bundle.FamilyMemberService.CreateAsync("Erik");

        var cut = RenderComponent<RecipeDetail>(p => p.Add(x => x.Id, recipe.Id));

        // Unrated badge on hero
        var badge = cut.Find(".recipe-detail-hero-rating-badge");
        badge.TextContent.Should().Contain("☆").And.Contain("Betygsätt");

        // Click badge to open shared modal
        badge.Click();
        cut.WaitForAssertion(() => cut.FindAll(".rating-member-row").Should().HaveCount(2));

        var rows = cut.FindAll(".rating-member-row").ToList();
        rows[0].TextContent.Should().Contain("Anna");
        rows[0].QuerySelectorAll(".rating-star-btn").Should().HaveCount(5);
        rows[1].TextContent.Should().Contain("Erik");
        rows[1].QuerySelectorAll(".rating-star-btn").Should().HaveCount(5);

        // Rate Anna 5 stars; Erik remains unrated
        rows[0].QuerySelectorAll(".rating-star-btn")[4].Click();

        cut.WaitForAssertion(() =>
        {
            cut.Find(".recipe-detail-hero-rating-badge").TextContent.Should().Contain("5,0").And.Contain("1 röst");
            cut.Find(".rating-modal-summary").TextContent.Should().Contain("5,0").And.Contain("1 röst");
            cut.Find(".rb-feedback-floating.rb-feedback-success").TextContent.Should().Contain("Anna");
        });

        // Erik's row still has 0 filled stars
        cut.FindAll(".rating-member-row").Skip(1).First().QuerySelectorAll(".rating-star-btn.is-filled").Should().BeEmpty();

        // Rate Erik 4 stars -> average 4,5 (2 röster)
        cut.FindAll(".rating-member-row").Skip(1).First().QuerySelectorAll(".rating-star-btn")[3].Click();
        cut.WaitForAssertion(() =>
        {
            cut.Find(".recipe-detail-hero-rating-badge").TextContent.Should().Contain("4,5").And.Contain("2 röster");
        });

        // Close modal
        cut.Find(".rating-modal-close").Click();
        cut.WaitForAssertion(() => cut.FindAll(".rating-modal").Should().BeEmpty());
    }

    [Fact]
    public async Task RecipeRatingModal_WhenNoFamilyMembers_ShouldShowSwedishExplanationAndSettingsLink()
    {
        using var bundle = SetupServices();
        var recipe = await bundle.RecipeService.CreateRecipeAsync(new Recipe { Title = "Soppa", Servings = 4 });

        var cut = RenderComponent<RecipeRatingModal>(p => p
            .Add(x => x.IsVisible, true)
            .Add(x => x.RecipeId, recipe.Id)
            .Add(x => x.RecipeTitle, recipe.Title));

        cut.WaitForAssertion(() =>
        {
            var empty = cut.Find(".rating-modal-empty");
            empty.TextContent.Should().Contain("familjemedlemmar");
            empty.QuerySelector("a[href='/installningar']").Should().NotBeNull();
        });
    }

    [Fact]
    public async Task Home_TodaysMeal_ShouldDisplayRatingBadge_OpenModal_AndRefresh()
    {
        var today = new DateOnly(2026, 11, 21);
        using var bundle = SetupServices(today);
        var recipe = await bundle.RecipeService.CreateRecipeAsync(new Recipe { Title = "Fredagstacos", Servings = 4 });
        var anna = await bundle.FamilyMemberService.CreateAsync("Anna");
        await bundle.FamilyMemberService.CreateAsync("Erik");

        var dayIndex = CalendarWeek.GetDayIndex(today);
        var menu = await bundle.WeeklyMenuService.GetOrCreateWeekMenuAsync(today);
        menu.Days[dayIndex].RecipeId = recipe.Id;
        menu.Days[dayIndex].Servings = 4;
        await bundle.WeeklyMenuService.SaveDraftAsync(menu);

        var cut = RenderComponent<Home>();
        cut.WaitForAssertion(() => cut.Find(".todays-dinner-rating-badge").TextContent.Should().Contain("Betygsätt"));

        cut.Find(".todays-dinner-rating-badge").Click();
        cut.WaitForAssertion(() => cut.FindAll(".rating-member-row").Should().HaveCount(2));

        // Rate Anna 4 stars
        cut.FindAll(".rating-member-row").First().QuerySelectorAll(".rating-star-btn")[3].Click();

        cut.WaitForAssertion(() =>
        {
            cut.Find(".todays-dinner-rating-badge").TextContent.Should().Contain("4,0").And.Contain("1 röst");
        });

        // External update via service/notifier also refreshes Home
        var erik = (await bundle.FamilyMemberService.GetAllAsync()).Single(m => m.Name == "Erik");
        await bundle.RecipeRatingService.SetRatingAsync(recipe.Id, erik.Id, 5);
        cut.WaitForAssertion(() =>
        {
            cut.Find(".todays-dinner-rating-badge").TextContent.Should().Contain("4,5").And.Contain("2 röster");
        });
    }

    [Fact]
    public async Task RecipeListPage_ShouldDisplayReadOnlyRatings_AndFilterAndSortByRating()
    {
        using var bundle = SetupServices();
        var anna = await bundle.FamilyMemberService.CreateAsync("Anna");
        var erik = await bundle.FamilyMemberService.CreateAsync("Erik");

        var high = await bundle.RecipeService.CreateRecipeAsync(new Recipe { Title = "Topprecept", Servings = 4 });
        var mid = await bundle.RecipeService.CreateRecipeAsync(new Recipe { Title = "Mellanrecept", Servings = 4 });
        var unrated = await bundle.RecipeService.CreateRecipeAsync(new Recipe { Title = "Obetygsatt recept", Servings = 4 });

        await bundle.RecipeRatingService.SetRatingAsync(high.Id, anna.Id, 5);
        await bundle.RecipeRatingService.SetRatingAsync(high.Id, erik.Id, 4); // 4,5
        await bundle.RecipeRatingService.SetRatingAsync(mid.Id, anna.Id, 3);  // 3,0

        var cut = RenderComponent<RecipeListPage>();
        cut.WaitForAssertion(() => cut.FindAll(".recipe-card").Should().HaveCountGreaterThanOrEqualTo(3));

        // Cards show compact read-only rating
        var highCard = cut.FindAll(".recipe-card").Single(c => c.TextContent.Contains("Topprecept"));
        highCard.TextContent.Should().Contain("4,5 (2 röster)");

        // Filter by >= 4 stars
        var ratingFilter = cut.Find(".filter-rating-wrapper fluent-select");
        ratingFilter.Change("min4");
        cut.WaitForAssertion(() =>
        {
            var titles = cut.FindAll(".recipe-card-title").Select(t => t.TextContent.Trim()).ToList();
            titles.Should().Contain("Topprecept");
            titles.Should().NotContain("Mellanrecept");
            titles.Should().NotContain("Obetygsatt recept");
        });

        // Filter for unrated
        ratingFilter.Change("unrated");
        cut.WaitForAssertion(() =>
        {
            var titles = cut.FindAll(".recipe-card-title").Select(t => t.TextContent.Trim()).ToList();
            titles.Should().Contain("Obetygsatt recept");
            titles.Should().NotContain("Topprecept");
            titles.Should().NotContain("Mellanrecept");
        });

        // Reset filter to all and sort by highest rating
        ratingFilter.Change("all");
        var sortSelect = cut.Find(".filter-sort-wrapper fluent-select");
        sortSelect.Change("rating-desc");
        cut.WaitForAssertion(() =>
        {
            var titles = cut.FindAll(".recipe-card-title").Select(t => t.TextContent.Trim()).ToList();
            titles.IndexOf("Topprecept").Should().BeLessThan(titles.IndexOf("Mellanrecept"));
            titles.IndexOf("Mellanrecept").Should().BeLessThan(titles.IndexOf("Obetygsatt recept"));
        });

        // Sort by lowest rating (unrated still last)
        sortSelect.Change("rating-asc");
        cut.WaitForAssertion(() =>
        {
            var titles = cut.FindAll(".recipe-card-title").Select(t => t.TextContent.Trim()).ToList();
            titles.IndexOf("Mellanrecept").Should().BeLessThan(titles.IndexOf("Topprecept"));
            titles.IndexOf("Topprecept").Should().BeLessThan(titles.IndexOf("Obetygsatt recept"));
        });
    }

    [Fact]
    public async Task RecipeRatingModal_WhenSaveFails_ShouldShowInlineError()
    {
        using var bundle = SetupServices();
        var recipe = await bundle.RecipeService.CreateRecipeAsync(new Recipe { Title = "Raderad", Servings = 4 });
        await bundle.FamilyMemberService.CreateAsync("Anna");

        var cut = RenderComponent<RecipeRatingModal>(p => p
            .Add(x => x.IsVisible, true)
            .Add(x => x.RecipeId, recipe.Id)
            .Add(x => x.RecipeTitle, recipe.Title));

        cut.WaitForAssertion(() => cut.FindAll(".rating-member-row").Should().HaveCount(1));

        // Delete recipe behind the scenes so SetRatingAsync throws KeyNotFoundException
        await bundle.RecipeService.DeleteRecipeAsync(recipe.Id);

        cut.FindAll(".rating-star-btn").Skip(4).First().Click();
        cut.WaitForAssertion(() =>
        {
            var error = cut.Find(".rating-modal-error");
            error.GetAttribute("role").Should().Be("alert");
            error.TextContent.Should().Contain("Kunde inte spara betyget");
        });
    }

    [Fact]
    public async Task Home_TodaysDinner_WithLongTitleAndImage_ShouldRenderHeroOverlayMarkupAndAllControls()
    {
        var today = new DateOnly(2026, 11, 21);
        using var bundle = SetupServices(today);
        const string longTitle = "Långkokt högrevsgryta med rödvin, rosmarin, vitlök och krämig potatispuré";
        var recipe = await bundle.RecipeService.CreateRecipeAsync(new Recipe
        {
            Title = longTitle,
            ImagePath = "images/recipes/test.jpg",
            CookTimeMinutes = 90,
            Servings = 4
        });
        await bundle.FamilyMemberService.CreateAsync("Anna");

        var dayIndex = CalendarWeek.GetDayIndex(today);
        var menu = await bundle.WeeklyMenuService.GetOrCreateWeekMenuAsync(today);
        menu.Days[dayIndex].RecipeId = recipe.Id;
        menu.Days[dayIndex].Servings = 4;
        await bundle.WeeklyMenuService.SaveDraftAsync(menu);

        var cut = RenderComponent<Home>();
        cut.WaitForAssertion(() => cut.Find(".todays-dinner-card").ClassList.Should().Contain("has-image"));

        var card = cut.Find(".todays-dinner-card");
        card.QuerySelector(".todays-dinner-image-wrap").Should().NotBeNull();
        card.QuerySelector(".todays-dinner-image-gradient").Should().BeNull();
        card.QuerySelector(".todays-dinner-title")!.TextContent.Trim().Should().Be(longTitle);
        card.QuerySelector(".todays-dinner-header .home-card-title")!.TextContent.Should().Be("Dagens middag");
        card.QuerySelector(".todays-dinner-meta")!.TextContent.Should().Contain("90 min").And.Contain("4 portioner");
        card.QuerySelector(".todays-dinner-cook-btn").Should().NotBeNull();
        card.QuerySelector(".todays-dinner-view-btn").Should().NotBeNull();
        card.QuerySelector(".todays-dinner-week-link").Should().NotBeNull();

        // Rating badge remains interactive
        cut.Find(".todays-dinner-rating-badge").Click();
        cut.WaitForAssertion(() => cut.FindAll(".rating-member-row").Should().HaveCount(1));
    }

    [Fact]
    public async Task WeeklyMenuPage_ShouldDisplayReadOnlyRatedAndUnratedSummaries_AndRefreshOnNotifier()
    {
        var today = new DateOnly(2026, 11, 21);
        using var bundle = SetupServices(today);
        var anna = await bundle.FamilyMemberService.CreateAsync("Anna");
        var erik = await bundle.FamilyMemberService.CreateAsync("Erik");
        var lisa = await bundle.FamilyMemberService.CreateAsync("Lisa");

        var ratedThree = await bundle.RecipeService.CreateRecipeAsync(new Recipe { Title = "Lasagne", Servings = 4 });
        var ratedOne = await bundle.RecipeService.CreateRecipeAsync(new Recipe { Title = "Pannkakor", Servings = 4 });
        var unrated = await bundle.RecipeService.CreateRecipeAsync(new Recipe { Title = "Fisksoppa", Servings = 4 });

        await bundle.RecipeRatingService.SetRatingAsync(ratedThree.Id, anna.Id, 5);
        await bundle.RecipeRatingService.SetRatingAsync(ratedThree.Id, erik.Id, 4);
        await bundle.RecipeRatingService.SetRatingAsync(ratedThree.Id, lisa.Id, 4); // 4,3 (3 röster)
        await bundle.RecipeRatingService.SetRatingAsync(ratedOne.Id, anna.Id, 5);   // 5,0 (1 röst)

        var menu = await bundle.WeeklyMenuService.GetOrCreateWeekMenuAsync(today);
        menu.Days[0].RecipeId = ratedThree.Id;
        menu.Days[1].RecipeId = ratedOne.Id;
        menu.Days[2].RecipeId = unrated.Id;
        await bundle.WeeklyMenuService.SaveDraftAsync(menu);

        // Verify WeeklyMenuService eager-loads Ratings with menu
        var loaded = await bundle.WeeklyMenuService.GetSavedMenuForWeekAsync(menu.WeekStartDate);
        loaded!.Days[0].Recipe!.Ratings.Should().HaveCount(3);
        loaded.Days[2].Recipe!.Ratings.Should().BeEmpty();

        var cut = RenderComponent<WeeklyMenuPage>();
        cut.WaitForAssertion(() => cut.FindAll(".weekly-day-card.has-recipe").Should().HaveCount(3));

        var dayCards = cut.FindAll(".weekly-day-card.has-recipe").ToList();
        var rating0 = dayCards[0].QuerySelector(".weekly-day-rating")!;
        rating0.TextContent.Should().Contain("★").And.Contain("4,3 (3 röster)");
        rating0.TagName.Should().Be("SPAN");

        var rating1 = dayCards[1].QuerySelector(".weekly-day-rating")!;
        rating1.TextContent.Should().Contain("★").And.Contain("5,0 (1 röst)");

        var rating2 = dayCards[2].QuerySelector(".weekly-day-rating")!;
        rating2.TextContent.Should().Contain("☆").And.Contain("Inga betyg");

        // Strictly read-only: no onclick handler, no modal, existing actions intact
        FluentActions.Invoking(() => rating0.Click()).Should().Throw<Bunit.MissingEventHandlerException>();
        cut.FindAll(".rating-modal").Should().BeEmpty();
        dayCards[0].QuerySelector(".weekly-day-view-link").Should().NotBeNull();
        dayCards[0].QuerySelector(".weekly-day-reroll-btn").Should().NotBeNull();
        dayCards[0].QuerySelector(".weekly-day-change-recipe-toggle").Should().NotBeNull();

        // Live refresh when a rating is added elsewhere
        await bundle.RecipeRatingService.SetRatingAsync(unrated.Id, anna.Id, 4);
        cut.WaitForAssertion(() =>
        {
            var updatedRating2 = cut.FindAll(".weekly-day-card.has-recipe").Skip(2).First().QuerySelector(".weekly-day-rating")!;
            updatedRating2.TextContent.Should().Contain("★").And.Contain("4,0 (1 röst)");
        });
    }
}
