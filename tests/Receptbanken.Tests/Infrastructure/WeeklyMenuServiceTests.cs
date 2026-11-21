using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Receptbanken.Core.Interfaces;
using Receptbanken.Core.Models;
using Receptbanken.Core.Services;
using Receptbanken.Infrastructure.Data;
using Receptbanken.Infrastructure.Services;

namespace Receptbanken.Tests.Infrastructure;

public class WeeklyMenuServiceTests : IDisposable
{
    private readonly TestDbContextFactory _factory;
    private readonly MockImageStorageService _imageStorage;
    private readonly RecipeService _recipeService;
    private readonly CategoryService _categoryService;
    private readonly WeeklyMenuService _weeklyMenuService;

    public WeeklyMenuServiceTests()
    {
        _factory = new TestDbContextFactory();
        _imageStorage = new MockImageStorageService();
        _recipeService = new RecipeService(_factory, _imageStorage, NullLogger<RecipeService>.Instance);
        _categoryService = new CategoryService(_factory, NullLogger<CategoryService>.Instance);

        var pantryMatcher = new PantryIngredientMatcher();
        var generator = new WeeklyMenuGenerator(new SeededRandomProvider(42), pantryMatcher);
        _weeklyMenuService = new WeeklyMenuService(_factory, generator, pantryMatcher, NullLogger<WeeklyMenuService>.Instance);
    }

    public void Dispose()
    {
        _factory.Dispose();
    }

    private async Task<(Category Vardag, Category Fest, Category Pasta)> SeedCategoriesAsync()
    {
        await using var db = _factory.CreateDbContext();
        await DbInitializer.InitializeAsync(db, NullLogger.Instance);

        var cats = await _categoryService.GetCategoriesAsync();
        var vardag = cats.First(c => c.Name == "Vardagsmat");
        var fest = cats.First(c => c.Name == "Fest & Helg");
        var pasta = cats.First(c => c.Name == "Pasta");

        return (
            new Category { Id = vardag.Id, Name = vardag.Name, ColorHex = vardag.ColorHex },
            new Category { Id = fest.Id, Name = fest.Name, ColorHex = fest.ColorHex },
            new Category { Id = pasta.Id, Name = pasta.Name, ColorHex = pasta.ColorHex }
        );
    }

    [Fact]
    public async Task GetOrCreateWeekMenuAsync_NewWeek_ShouldAssignDefaultCategoriesWithoutCreatingDuplicates()
    {
        var (vardag, fest, _) = await SeedCategoriesAsync();
        var countBefore = (await _categoryService.GetCategoriesAsync()).Count;

        var menu = await _weeklyMenuService.GetOrCreateWeekMenuAsync(new DateOnly(2026, 11, 4));

        var countAfter = (await _categoryService.GetCategoriesAsync()).Count;
        countAfter.Should().Be(countBefore, "inga dubblettkategorier ska skapas");

        menu.Status.Should().Be(WeeklyMenuStatus.Draft);
        menu.WeekStartDate.Should().Be(new DateOnly(2026, 11, 2));
        menu.IsoYear.Should().Be(2026);
        menu.IsoWeek.Should().Be(45);
        menu.Days.Should().HaveCount(7);

        // Mon-Thu (0..3) & Sun (6) -> Vardagsmat; Fri-Sat (4..5) -> Fest & Helg
        for (int i = 0; i < 7; i++)
        {
            var expectedCatId = i is 4 or 5 ? fest.Id : vardag.Id;
            menu.Days[i].SelectedCategories.Select(sc => sc.CategoryId).Should().Equal(expectedCatId);
        }
    }

    [Fact]
    public async Task SaveDraft_Confirm_EditConfirmed_And_Reopen_ShouldPersistCorrectWeekAndAvoidDoubleCounting()
    {
        var (vardag, fest, pasta) = await SeedCategoriesAsync();

        var r1 = await _recipeService.CreateRecipeAsync(new Recipe { Title = "Köttfärssås" }, [vardag.Id, pasta.Id]);
        var r2 = await _recipeService.CreateRecipeAsync(new Recipe { Title = "Fiskgratäng" }, [vardag.Id]);
        var r3 = await _recipeService.CreateRecipeAsync(new Recipe { Title = "Oxfilé Provencale" }, [fest.Id]);

        var week45Date = new DateOnly(2026, 11, 2);
        var draft = await _weeklyMenuService.GetOrCreateWeekMenuAsync(week45Date);
        draft.PantryIngredients = ["Köttfärs", "Lax"];
        draft.Days[0].RecipeId = r1.Id;
        draft.Days[0].SelectedCategories.Add(new WeeklyMenuDayCategory { CategoryId = pasta.Id });
        draft.Days[4].RecipeId = r3.Id;

        // 1. Save as Draft
        var savedDraft = await _weeklyMenuService.SaveDraftAsync(draft);
        savedDraft.Status.Should().Be(WeeklyMenuStatus.Draft);
        savedDraft.ConfirmedAt.Should().BeNull();
        savedDraft.PantryIngredients.Should().Equal("Köttfärs", "Lax");

        // Draft must NOT count in history for week 46
        var historyAfterDraft = await _weeklyMenuService.GetHistoryLookupAsync(new DateOnly(2026, 11, 9));
        historyAfterDraft.Should().BeEmpty();

        // 2. Confirm ("Spika") the menu
        var confirmed = await _weeklyMenuService.ConfirmMenuAsync(savedDraft);
        confirmed.Status.Should().Be(WeeklyMenuStatus.Confirmed);
        confirmed.ConfirmedAt.Should().NotBeNull();

        // History for week 46 should now reflect r1 (1 occurrence) and r3 (1 occurrence)
        var historyAfterConfirm = await _weeklyMenuService.GetHistoryLookupAsync(new DateOnly(2026, 11, 9));
        historyAfterConfirm[r1.Id].TotalConfirmedOccurrences.Should().Be(1);
        historyAfterConfirm[r1.Id].WasPlannedPreviousWeek.Should().BeTrue();
        historyAfterConfirm[r3.Id].TotalConfirmedOccurrences.Should().Be(1);

        // 3. Edit the confirmed menu: replace r1 on Monday with r2, update pantry tags, and save again
        confirmed.Days[0].RecipeId = r2.Id;
        confirmed.PantryIngredients = ["Torsk"];
        var updatedConfirmed = await _weeklyMenuService.ConfirmMenuAsync(confirmed);

        updatedConfirmed.Id.Should().Be(confirmed.Id, "samma veckomeny uppdateras utan dubbletter");
        updatedConfirmed.Days[0].RecipeId.Should().Be(r2.Id);
        updatedConfirmed.PantryIngredients.Should().Equal("Torsk");

        // History must now reflect r2 (1) and r3 (1), and r1 must NOT be counted anymore (no double counting!)
        var historyAfterEdit = await _weeklyMenuService.GetHistoryLookupAsync(new DateOnly(2026, 11, 9));
        historyAfterEdit.Should().NotContainKey(r1.Id);
        historyAfterEdit[r2.Id].TotalConfirmedOccurrences.Should().Be(1);
        historyAfterEdit[r3.Id].TotalConfirmedOccurrences.Should().Be(1);

        // 4. Reopen saved menu for week 45 and verify another week (week 46) still gets default categories
        var reopened45 = await _weeklyMenuService.GetSavedMenuForWeekAsync(new DateOnly(2026, 11, 5));
        reopened45.Should().NotBeNull();
        reopened45!.Days[0].RecipeId.Should().Be(r2.Id);
        reopened45.Days[0].SelectedCategories.Select(sc => sc.CategoryId).Should().Contain([vardag.Id, pasta.Id]);

        var freshWeek46 = await _weeklyMenuService.GetOrCreateWeekMenuAsync(new DateOnly(2026, 11, 9));
        freshWeek46.Status.Should().Be(WeeklyMenuStatus.Draft);
        freshWeek46.Days[0].SelectedCategories.Select(sc => sc.CategoryId).Should().Equal([vardag.Id], "kategorival i vecka 45 ändrar inte standard för vecka 46");
    }

    [Fact]
    public async Task DeleteRecipe_WhenReferencedInConfirmedMenu_ShouldSetDayRecipeIdToNullWithoutError()
    {
        var (vardag, _, _) = await SeedCategoriesAsync();
        var recipe = await _recipeService.CreateRecipeAsync(new Recipe { Title = "Recept som tas bort" }, [vardag.Id]);

        var menu = await _weeklyMenuService.GetOrCreateWeekMenuAsync(new DateOnly(2026, 11, 2));
        menu.Days[0].RecipeId = recipe.Id;
        await _weeklyMenuService.ConfirmMenuAsync(menu);

        // Delete the recipe via RecipeService
        await _recipeService.DeleteRecipeAsync(recipe.Id);

        // Reopen the menu -> day 0 should safely have RecipeId == null
        var reopened = await _weeklyMenuService.GetSavedMenuForWeekAsync(new DateOnly(2026, 11, 2));
        reopened.Should().NotBeNull();
        reopened!.Days[0].RecipeId.Should().BeNull();
        reopened.Days[0].Recipe.Should().BeNull();
    }

    [Fact]
    public async Task GetTodaysDinnerAsync_ShouldReturnCorrectRecipeWhenConfirmed_AndFalseWhenNoConfirmedMenu()
    {
        var (vardag, _, _) = await SeedCategoriesAsync();
        var tuesdayRecipe = await _recipeService.CreateRecipeAsync(
            new Recipe
            {
                Title = "Marry me chicken",
                PrepTimeMinutes = 15,
                CookTimeMinutes = 30,
                Servings = 4,
                ServingsUnit = "portioner"
            },
            [vardag.Id]);

        var tuesdayDate = new DateOnly(2026, 11, 3); // Tuesday of week 45

        // 1. Before any menu is confirmed -> HasConfirmedMenu is false
        var beforeConfirm = await _weeklyMenuService.GetTodaysDinnerAsync(tuesdayDate);
        beforeConfirm.HasConfirmedMenu.Should().BeFalse();
        beforeConfirm.Recipe.Should().BeNull();

        // 2. When only a Draft is saved -> still HasConfirmedMenu is false
        var menu = await _weeklyMenuService.GetOrCreateWeekMenuAsync(tuesdayDate);
        menu.Days[1].RecipeId = tuesdayRecipe.Id; // DayIndex 1 = Tuesday
        await _weeklyMenuService.SaveDraftAsync(menu);

        var withDraftOnly = await _weeklyMenuService.GetTodaysDinnerAsync(tuesdayDate);
        withDraftOnly.HasConfirmedMenu.Should().BeFalse();
        withDraftOnly.Recipe.Should().BeNull();

        // 3. After confirming the menu -> returns Tuesday's recipe for Tuesday, and null recipe for Wednesday
        await _weeklyMenuService.ConfirmMenuAsync(menu);

        var tuesdayDinner = await _weeklyMenuService.GetTodaysDinnerAsync(tuesdayDate);
        tuesdayDinner.HasConfirmedMenu.Should().BeTrue();
        tuesdayDinner.Recipe.Should().NotBeNull();
        tuesdayDinner.Recipe!.Title.Should().Be("Marry me chicken");
        tuesdayDinner.Recipe.TotalTimeMinutes.Should().Be(45);
        tuesdayDinner.Servings.Should().Be(4);

        var wednesdayDinner = await _weeklyMenuService.GetTodaysDinnerAsync(new DateOnly(2026, 11, 4));
        wednesdayDinner.HasConfirmedMenu.Should().BeTrue();
        wednesdayDinner.Recipe.Should().BeNull();
    }

    private sealed class FixedRandomProvider(double value) : IRandomProvider
    {
        public double NextDouble() => value;
    }

    [Fact]
    public async Task GenerateWeekProposalAsync_And_RerollDayProposalAsync_ShouldLoadRatingsAndRespectFamilyMemberActiveState()
    {
        var (vardag, _, pasta) = await SeedCategoriesAsync();

        var rLow = await _recipeService.CreateRecipeAsync(new Recipe { Title = "Recept Låg" }, [vardag.Id]);
        var rHigh = await _recipeService.CreateRecipeAsync(new Recipe { Title = "Recept Hög" }, [vardag.Id]);
        var rOther = await _recipeService.CreateRecipeAsync(new Recipe { Title = "Recept Annan" }, [vardag.Id]);

        var member1 = new FamilyMember { Name = "Aktiv", IsActive = true, SortOrder = 1 };
        var member2 = new FamilyMember { Name = "Inaktiv", IsActive = false, SortOrder = 2 };

        await using (var db = _factory.CreateDbContext())
        {
            db.FamilyMembers.AddRange(member1, member2);
            await db.SaveChangesAsync();

            // member1 (active): rLow=1, rHigh=5
            // member2 (inactive): rLow=5, rHigh=1
            db.RecipeRatings.AddRange(
                new RecipeRating { RecipeId = rLow.Id, FamilyMemberId = member1.Id, Stars = 1 },
                new RecipeRating { RecipeId = rHigh.Id, FamilyMemberId = member1.Id, Stars = 5 },
                new RecipeRating { RecipeId = rLow.Id, FamilyMemberId = member2.Id, Stars = 5 },
                new RecipeRating { RecipeId = rHigh.Id, FamilyMemberId = member2.Id, Stars = 1 });
            await db.SaveChangesAsync();
        }

        var pantryMatcher = new PantryIngredientMatcher();
        var midpointGenerator = new WeeklyMenuGenerator(new FixedRandomProvider(0.5), pantryMatcher);
        var service = new WeeklyMenuService(_factory, midpointGenerator, pantryMatcher, NullLogger<WeeklyMenuService>.Instance);

        var menu = await service.GetOrCreateWeekMenuAsync(new DateOnly(2026, 11, 2));
        // Restrict Days 1..6 to Pasta (0 recipes) so Day 0 chooses between rLow, rHigh, rOther
        for (int i = 1; i < 7; i++)
        {
            menu.Days[i].SelectedCategories = [new WeeklyMenuDayCategory { CategoryId = pasta.Id }];
        }

        // Only rLow and rHigh are rated; let's remove rOther from the DB for clean 2-candidate comparison first,
        // or keep rOther for reroll: with 3 candidates [rLow(0.9167), rHigh(1.0833), rOther(1.0)],
        // cumulative at rLow is 0.9167/3.0 = 0.3056, at rHigh is (0.9167+1.0833)/3.0 = 0.6667 > 0.5 -> rHigh is picked!
        await service.GenerateWeekProposalAsync(menu);
        menu.Days[0].RecipeId.Should().Be(rHigh.Id, "aktiv 5-stjärnig röst på rHigh ger högre vikt än rLow och rOther");

        // Now flip active state: member1 becomes inactive, member2 becomes active!
        // Now rLow has 5 stars (active) and rHigh has 1 star (active).
        await using (var db = _factory.CreateDbContext())
        {
            var m1 = await db.FamilyMembers.FirstAsync(m => m.Id == member1.Id);
            var m2 = await db.FamilyMembers.FirstAsync(m => m.Id == member2.Id);
            m1.IsActive = false;
            m2.IsActive = true;
            await db.SaveChangesAsync();
        }

        // Set Day 0 to rOther so reroll candidates are [rLow(1.0833), rHigh(0.9167)]
        // Cumulative at rLow is 1.0833/2.0 = 0.5417 > 0.5 -> rLow is picked!
        menu.Days[0].RecipeId = rOther.Id;
        menu.Days[0].Recipe = rOther;

        var reroll = await service.RerollDayProposalAsync(menu, dayIndex: 0);
        reroll.Changed.Should().BeTrue();
        reroll.NewRecipeId.Should().Be(rLow.Id, "återaktiverad medlems 5-stjärniga röst på rLow blir effektiv vid slumpa nytt");
    }
}

