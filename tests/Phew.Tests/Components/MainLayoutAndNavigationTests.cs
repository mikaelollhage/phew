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
using Phew.Web.Components.Layout;
using Phew.Web.Components.Pages;
using Phew.Web.Services;

namespace Phew.Tests.Components;

public class MainLayoutAndNavigationTests : TestContext
{
    private sealed class FixedLocalDateProvider : ILocalDateProvider
    {
        private readonly DateOnly _today;
        public FixedLocalDateProvider(DateOnly today) => _today = today;
        public DateOnly GetLocalToday() => _today;
        public void SetClientLocalDate(DateOnly clientDate) { }
    }

    private (TestDbContextFactory DbFactory, RecipeService RecipeService) SetupTestContext(bool seedRecipes = true)
    {
        var dbFactory = new TestDbContextFactory();
        if (seedRecipes)
        {
            using var db = dbFactory.CreateDbContext();
            DbInitializer.InitializeAsync(db, NullLogger.Instance).GetAwaiter().GetResult();
        }

        var imageStorage = new MockImageStorageService();
        var recipeService = new RecipeService(dbFactory, imageStorage, NullLogger<RecipeService>.Instance);
        var categoryService = new CategoryService(dbFactory, NullLogger<CategoryService>.Instance);
        var pantryMatcher = new PantryIngredientMatcher();
        var generator = new WeeklyMenuGenerator(new SeededRandomProvider(42), pantryMatcher);
        var weeklyMenuService = new WeeklyMenuService(dbFactory, generator, pantryMatcher, NullLogger<WeeklyMenuService>.Instance);
        var localDateProvider = new FixedLocalDateProvider(new DateOnly(2026, 11, 2));
        var calc = new ServingCalculator();
        var aggregator = new ShoppingListAggregator(calc);
        var parser = new SwedishIngredientParser();
        var notifier = new ShoppingListNotifier();
        var shoppingService = new ShoppingListService(dbFactory, aggregator, parser, notifier);

        Services.AddFluentUIComponents();
        Services.AddScoped<AppNotificationService>();
        Services.AddScoped<AppThemeService>();
        Services.AddSingleton<IDbContextFactory<AppDbContext>>(dbFactory);
        Services.AddSingleton<IImageStorageService>(imageStorage);
        Services.AddSingleton<ILocalDateProvider>(localDateProvider);
        Services.AddSingleton(recipeService);
        Services.AddSingleton(categoryService);
        Services.AddSingleton(pantryMatcher);
        Services.AddSingleton(generator);
        Services.AddSingleton(weeklyMenuService);
        Services.AddSingleton(calc);
        Services.AddSingleton(aggregator);
        Services.AddSingleton(parser);
        Services.AddSingleton(notifier);
        Services.AddSingleton(shoppingService);
        JSInterop.Mode = JSRuntimeMode.Loose;

        return (dbFactory, recipeService);
    }

    [Theory]
    [InlineData("http://localhost/")]
    [InlineData("http://localhost/inkopslista")]
    [InlineData("http://localhost/veckomeny")]
    [InlineData("http://localhost/recept")]
    public void MainLayout_ShouldNotRenderAppHeaderOrGlobalNewRecipeButton_OnAnyRoute(string url)
    {
        SetupTestContext(seedRecipes: false);
        var nav = Services.GetRequiredService<Microsoft.AspNetCore.Components.NavigationManager>();
        nav.NavigateTo(url);

        var cut = RenderComponent<MainLayout>();

        cut.FindAll(".app-header").Should().BeEmpty(
            "UX-2D.1: den globala .app-header-sektionen har tagits bort från applikationens layout.");
        cut.FindAll("a[href='/recept/ny']").Should().BeEmpty(
            "UX-2D.1: receptskapande ska inte ligga som en global knapp i MainLayout.");
        cut.FindAll(".app-content").Should().ContainSingle(
            "Applikationens huvudinnehållscontainer (.app-content) ska renderas ren utan överflödig header.");
    }

    [Fact]
    public void RecipeListPage_WhenRecipesExist_ShouldRenderLocalNewRecipeButton_InPageHeader()
    {
        var (dbFactory, _) = SetupTestContext(seedRecipes: true);
        using var _ = dbFactory;

        var cut = RenderComponent<RecipeListPage>();

        cut.WaitForAssertion(() =>
        {
            var pageHeader = cut.Find(".page-header");
            var newRecipeBtn = pageHeader.QuerySelector("#home-create-recipe-btn");
            newRecipeBtn.Should().NotBeNull("Receptöversikten ska ha en lokal 'Nytt recept'-knapp i sidhuvudet");
            newRecipeBtn!.GetAttribute("href").Should().Be("/recept/ny");
            newRecipeBtn.ClassList.Should().Contain("btn-primary-custom");
            newRecipeBtn.TextContent.Should().Contain("Nytt recept");

            var importBtn = pageHeader.QuerySelector("a[href='/importera']");
            importBtn.Should().NotBeNull("Receptöversikten ska också erbjuda 'Importera från recept'");
            importBtn!.ClassList.Should().Contain("btn-secondary-custom");
        });
    }

    [Fact]
    public void RecipeListPage_WhenEmptyState_ShouldRenderBothHeaderNewRecipeButton_AndEmptyStateActionButtons()
    {
        var (dbFactory, _) = SetupTestContext(seedRecipes: false);
        using var _ = dbFactory;

        var cut = RenderComponent<RecipeListPage>();

        cut.WaitForAssertion(() =>
        {
            // Sidhuvudets lokala knapp ska finnas även när listan är tom
            var pageHeader = cut.Find(".page-header");
            var headerBtn = pageHeader.QuerySelector("#home-create-recipe-btn");
            headerBtn.Should().NotBeNull();
            headerBtn!.GetAttribute("href").Should().Be("/recept/ny");
            headerBtn.ClassList.Should().Contain("btn-primary-custom");

            // Empty state-ytan ska också erbjuda knappar
            var emptyState = cut.Find(".empty-state");
            emptyState.Should().NotBeNull();
            var emptyStateCreateBtn = emptyState.QuerySelector("a[href='/recept/ny']");
            emptyStateCreateBtn.Should().NotBeNull();
            emptyStateCreateBtn!.TextContent.Should().Contain("Skapa recept");
            emptyStateCreateBtn.ClassList.Should().Contain("btn-primary-custom");

            var emptyStateImportBtn = emptyState.QuerySelector("a[href='/importera']");
            emptyStateImportBtn.Should().NotBeNull();
            emptyStateImportBtn!.TextContent.Should().Contain("Importera recept");
            emptyStateImportBtn.ClassList.Should().Contain("btn-secondary-custom");
        });
    }

    [Fact]
    public void Home_WhenNoWeeklyMenu_ShouldRenderPlanWeekEmptyState_AndShortcuts()
    {
        var (dbFactory, _) = SetupTestContext(seedRecipes: false);
        using var _ = dbFactory;

        var cut = RenderComponent<Home>();

        cut.WaitForAssertion(() =>
        {
            cut.Find(".page-title").TextContent.Should().Be("Dagens middag");
            cut.Find(".home-empty-state").TextContent.Should().Contain("Dags att planera veckan?");
            cut.Find("#home-empty-plan-week-btn").GetAttribute("href").Should().Be("/veckomeny");
            cut.Find("#home-empty-browse-recipes-btn").GetAttribute("href").Should().Be("/recept");

            // Snabbval
            cut.Find("#home-shortcut-weekly-menu").GetAttribute("href").Should().Be("/veckomeny");
            cut.Find("#home-shortcut-shopping-list").GetAttribute("href").Should().Be("/inkopslista");
            cut.Find("#home-shortcut-recipes").GetAttribute("href").Should().Be("/recept");
        });
    }

    [Fact]
    public void NavMenu_ShouldRenderPhewBrandIdentity_WithFullDesktopLogoAndCompactMobileBranding_WithoutFryingPanEmoji()
    {
        SetupTestContext(seedRecipes: false);

        var cut = RenderComponent<NavMenu>();

        var brandSection = cut.Find(".brand-section");
        brandSection.TextContent.Should().NotContain("🍳", "the old frying pan and egg branding must be replaced");

        var brandLink = brandSection.QuerySelector("a.brand-link");
        brandLink.Should().NotBeNull();
        brandLink!.GetAttribute("href").Should().Be("");
        brandLink.GetAttribute("aria-label").Should().Be("PHEW! – Family life, sorted.");

        var fullLogoImg = brandSection.QuerySelector("img.brand-logo-full");
        fullLogoImg.Should().NotBeNull("B08: desktop sidebar header renders the full illustrated PHEW! logo");
        fullLogoImg!.GetAttribute("src").Should().Be("/phew-logo.webp");
        fullLogoImg.GetAttribute("width").Should().Be("212");
        fullLogoImg.GetAttribute("height").Should().Be("212");
        fullLogoImg.GetAttribute("alt").Should().Be("");
        fullLogoImg.GetAttribute("aria-hidden").Should().Be("true");

        var compactContainer = brandSection.QuerySelector(".brand-compact");
        compactContainer.Should().NotBeNull("B08: mobile header retains the compact branding arrangement");
        compactContainer!.GetAttribute("aria-hidden").Should().Be("true");

        var logoImg = brandSection.QuerySelector("img.brand-logo-mark");
        logoImg.Should().NotBeNull();
        logoImg!.GetAttribute("src").Should().Be("/favicon.svg");
        logoImg.GetAttribute("width").Should().Be("36");
        logoImg.GetAttribute("height").Should().Be("36");
        logoImg.GetAttribute("alt").Should().Be("");
        logoImg.GetAttribute("aria-hidden").Should().Be("true");

        brandSection.QuerySelector(".brand-title")!.TextContent.Trim().Should().Be("PHEW!");
        brandSection.QuerySelector(".brand-subtitle")!.TextContent.Trim().Should().Be("Family life, sorted.");
    }

    [Fact]
    public async Task MainLayout_ShouldSyncFluentDesignThemeWithAppThemeService_OnLiveClientThemeChanges()
    {
        SetupTestContext(seedRecipes: false);
        var themeService = Services.GetRequiredService<AppThemeService>();

        var cut = RenderComponent<MainLayout>();
        var fluentTheme = cut.FindComponent<FluentDesignTheme>();
        fluentTheme.Instance.Mode.Should().Be(DesignThemeModes.Dark);

        // Simulate client resolving System mode with light OS preference
        await cut.Instance.OnClientThemeChangedAsync("system", "light");
        cut.WaitForAssertion(() =>
        {
            themeService.Preference.Should().Be(ThemePreference.System);
            themeService.ResolvedTheme.Should().Be("light");
            cut.FindComponent<FluentDesignTheme>().Instance.Mode.Should().Be(DesignThemeModes.Light);
        });

        // Simulate live OS preference switching to dark while System mode is active
        await cut.Instance.OnClientThemeChangedAsync("system", "dark");
        cut.WaitForAssertion(() =>
        {
            themeService.Preference.Should().Be(ThemePreference.System);
            themeService.ResolvedTheme.Should().Be("dark");
            cut.FindComponent<FluentDesignTheme>().Instance.Mode.Should().Be(DesignThemeModes.Dark);
        });
    }
}
