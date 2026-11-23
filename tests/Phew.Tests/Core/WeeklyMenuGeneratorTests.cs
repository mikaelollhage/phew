using FluentAssertions;
using Phew.Core.Interfaces;
using Phew.Core.Models;
using Phew.Core.Services;

namespace Phew.Tests.Core;

public class WeeklyMenuGeneratorTests
{
    private sealed class FixedSequenceRandomProvider : IRandomProvider
    {
        private readonly double[] _values;
        private int _index;

        public FixedSequenceRandomProvider(params double[] values)
        {
            _values = values.Length > 0 ? values : [0.0];
        }

        public double NextDouble()
        {
            var val = _values[_index % _values.Length];
            _index++;
            return val;
        }
    }

    private static Recipe CreateRecipe(
        int id,
        string title,
        int[]? categoryIds = null,
        string[]? ingredientNames = null,
        bool isFavorite = false)
    {
        return new Recipe
        {
            Id = id,
            Title = title,
            Servings = 4,
            ServingsUnit = "portioner",
            IsFavorite = isFavorite,
            RecipeCategories = (categoryIds ?? []).Select(cid => new RecipeCategory
            {
                RecipeId = id,
                CategoryId = cid,
                Category = new Category { Id = cid, Name = $"Kat-{cid}" }
            }).ToList(),
            Ingredients = (ingredientNames ?? []).Select((name, idx) => new Ingredient
            {
                Id = idx + 1,
                RecipeId = id,
                OrderIndex = idx + 1,
                Name = name,
                Quantity = 400m,
                Unit = "g",
                RawText = $"400 g {name}"
            }).ToList()
        };
    }

    private static WeeklyMenu CreateEmptyMenuForWeek(DateOnly weekStart, int? defaultCategoryId = null)
    {
        var week = CalendarWeek.FromDate(weekStart);
        var menu = new WeeklyMenu
        {
            WeekStartDate = week.WeekStartDate,
            IsoYear = week.IsoYear,
            IsoWeek = week.IsoWeek,
            Status = WeeklyMenuStatus.Draft
        };

        for (int i = 0; i < 7; i++)
        {
            var day = new WeeklyMenuDay
            {
                DayIndex = i,
                Date = week.GetDateForDayIndex(i)
            };
            if (defaultCategoryId.HasValue)
            {
                day.SelectedCategories.Add(new WeeklyMenuDayCategory { CategoryId = defaultCategoryId.Value });
            }
            menu.Days.Add(day);
        }

        return menu;
    }

    [Fact]
    public void GenerateWeek_WhenEnoughRecipesExist_ShouldSelectSevenUniqueRecipes()
    {
        var generator = new WeeklyMenuGenerator(new SeededRandomProvider(42));
        var recipes = Enumerable.Range(1, 10)
            .Select(i => CreateRecipe(i, $"Recept {i}", categoryIds: [1]))
            .ToList();

        var menu = CreateEmptyMenuForWeek(new DateOnly(2026, 11, 2), defaultCategoryId: 1);

        var result = generator.GenerateWeek(menu, recipes);

        result.AllDaysFilled.Should().BeTrue();
        result.FilledDaysCount.Should().Be(7);
        result.EmptyDaysCount.Should().Be(0);

        var assignedIds = menu.Days.Select(d => d.RecipeId).ToList();
        assignedIds.Should().AllSatisfy(id => id.Should().NotBeNull());
        assignedIds.Distinct().Should().HaveCount(7, "samma recept får inte dupliceras inom samma veckomeny");
    }

    [Fact]
    public void GenerateWeek_CategoryFilter_AndMultipleCategoriesWithOrSemantics_AndNoCategoriesSelected_ShouldWorkCorrectly()
    {
        var generator = new WeeklyMenuGenerator(new SeededRandomProvider(7));

        // Cat 1 = Vardagsmat, Cat 2 = Pasta, Cat 3 = Vegetariskt, Cat 4 = Soppa
        var r1 = CreateRecipe(1, "Vardagsgryta", categoryIds: [1]);
        var r2 = CreateRecipe(2, "Carbonara", categoryIds: [2]);
        var r3 = CreateRecipe(3, "Linssoppa", categoryIds: [3, 4]);
        var r4 = CreateRecipe(4, "Okategoriserad rätt", categoryIds: []);

        var menu = CreateEmptyMenuForWeek(new DateOnly(2026, 11, 2));
        // Day 0: Only Cat 2 (Pasta) -> must pick r2
        menu.Days[0].SelectedCategories.Add(new WeeklyMenuDayCategory { CategoryId = 2 });
        // Day 1: Cat 1 OR Cat 3 -> can pick r1 or r3
        menu.Days[1].SelectedCategories.Add(new WeeklyMenuDayCategory { CategoryId = 1 });
        menu.Days[1].SelectedCategories.Add(new WeeklyMenuDayCategory { CategoryId = 3 });
        // Day 2: Cat 4 -> must pick r3 (so Day 1 must get r1!)
        menu.Days[2].SelectedCategories.Add(new WeeklyMenuDayCategory { CategoryId = 4 });
        // Day 3: No categories selected -> any remaining recipe qualifies (must pick r4)
        // Days 4, 5, 6: Category 99 (no matching recipes)
        menu.Days[4].SelectedCategories.Add(new WeeklyMenuDayCategory { CategoryId = 99 });
        menu.Days[5].SelectedCategories.Add(new WeeklyMenuDayCategory { CategoryId = 99 });
        menu.Days[6].SelectedCategories.Add(new WeeklyMenuDayCategory { CategoryId = 99 });

        var result = generator.GenerateWeek(menu, [r1, r2, r3, r4]);

        menu.Days[0].RecipeId.Should().Be(2);
        menu.Days[1].RecipeId.Should().Be(1);
        menu.Days[2].RecipeId.Should().Be(3);
        menu.Days[3].RecipeId.Should().Be(4);
        menu.Days[4].RecipeId.Should().BeNull();
        menu.Days[4].StatusMessage.Should().Contain("Inga recept matchar");
        result.FilledDaysCount.Should().Be(4);
    }

    [Fact]
    public void GenerateWeek_WhenTooFewRecipesExist_ShouldLeaveEmptyDaysWithClearMessageAndNeverDuplicate()
    {
        var generator = new WeeklyMenuGenerator(new SeededRandomProvider(99));
        var recipes = new List<Recipe>
        {
            CreateRecipe(1, "Köttfärssås", categoryIds: [1]),
            CreateRecipe(2, "Korv stroganoff", categoryIds: [1]),
            CreateRecipe(3, "Pannkakor", categoryIds: [1])
        };

        var menu = CreateEmptyMenuForWeek(new DateOnly(2026, 11, 2), defaultCategoryId: 1);

        var result = generator.GenerateWeek(menu, recipes);

        result.FilledDaysCount.Should().Be(3);
        result.EmptyDaysCount.Should().Be(4);

        var filledIds = menu.Days.Where(d => d.RecipeId.HasValue).Select(d => d.RecipeId!.Value).ToList();
        filledIds.Should().BeEquivalentTo([1, 2, 3]);

        var emptyDays = menu.Days.Where(d => !d.RecipeId.HasValue).ToList();
        emptyDays.Should().HaveCount(4);
        emptyDays.Should().AllSatisfy(d => d.StatusMessage.Should().NotBeNullOrWhiteSpace());
    }

    [Fact]
    public void GenerateWeek_ShouldAvoidGreedyStarvation_WhenConstrainedDayNeedsSpecificRecipe()
    {
        // Even if random provider returns 0.0 (which would greedily pick Recipe 1 first on Day 0),
        // Day 1 ONLY accepts Recipe 1, while Day 0 accepts Recipe 1 or Recipe 2.
        // The generator must assign Recipe 2 to Day 0 and Recipe 1 to Day 1 so both days are filled!
        var generator = new WeeklyMenuGenerator(new FixedSequenceRandomProvider(0.0));

        var r1 = CreateRecipe(1, "Lyxpasta", categoryIds: [1, 2]);
        var r2 = CreateRecipe(2, "Köttbullar", categoryIds: [1]);

        var menu = CreateEmptyMenuForWeek(new DateOnly(2026, 11, 2));
        menu.Days[0].SelectedCategories.Add(new WeeklyMenuDayCategory { CategoryId = 1 }); // Matches r1, r2
        menu.Days[1].SelectedCategories.Add(new WeeklyMenuDayCategory { CategoryId = 2 }); // Matches ONLY r1
        for (int i = 2; i < 7; i++)
        {
            menu.Days[i].SelectedCategories.Add(new WeeklyMenuDayCategory { CategoryId = 99 }); // Empty
        }

        generator.GenerateWeek(menu, [r1, r2]);

        menu.Days[0].RecipeId.Should().Be(2);
        menu.Days[1].RecipeId.Should().Be(1);
    }

    [Fact]
    public void CalculateRecipeWeight_ShouldPenalizeRecentAndFrequentRecipes_WithoutPermanentlyExcludingThem()
    {
        var generator = new WeeklyMenuGenerator();
        var recipe = CreateRecipe(1, "Fiskgratäng", categoryIds: [1]);

        var targetMonday = new DateOnly(2026, 11, 2);
        var prevWeekMonday = targetMonday.AddDays(-7);
        var sixWeeksAgoMonday = targetMonday.AddDays(-42);

        var neverPlannedWeight = generator.CalculateRecipeWeight(recipe, history: null);

        var plannedSixWeeksAgoOnce = new RecipeHistoryStats(
            RecipeId: 1,
            TotalConfirmedOccurrences: 1,
            LastPlannedWeekStartDate: sixWeeksAgoMonday,
            WeeksSinceLastPlanned: 6,
            WasPlannedPreviousWeek: false);

        var plannedSixWeeksAgoOften = new RecipeHistoryStats(
            RecipeId: 1,
            TotalConfirmedOccurrences: 8,
            LastPlannedWeekStartDate: sixWeeksAgoMonday,
            WeeksSinceLastPlanned: 6,
            WasPlannedPreviousWeek: false);

        var plannedLastWeek = new RecipeHistoryStats(
            RecipeId: 1,
            TotalConfirmedOccurrences: 1,
            LastPlannedWeekStartDate: prevWeekMonday,
            WeeksSinceLastPlanned: 1,
            WasPlannedPreviousWeek: true);

        double weightSixWeeksOnce = generator.CalculateRecipeWeight(recipe, plannedSixWeeksAgoOnce);
        double weightSixWeeksOften = generator.CalculateRecipeWeight(recipe, plannedSixWeeksAgoOften);
        double weightLastWeek = generator.CalculateRecipeWeight(recipe, plannedLastWeek);

        neverPlannedWeight.Should().BeGreaterThan(weightSixWeeksOnce);
        weightSixWeeksOnce.Should().BeGreaterThan(weightSixWeeksOften);
        weightSixWeeksOften.Should().BeGreaterThan(weightLastWeek);
        weightLastWeek.Should().BeGreaterThanOrEqualTo(generator.Weights.MinimumWeight, "inga recept får uteslutas permanent");
    }

    [Fact]
    public void CalculateRecipeWeight_ShouldGiveNoBonusToFavoriteRecipes()
    {
        var generator = new WeeklyMenuGenerator();
        var regularRecipe = CreateRecipe(1, "Rätt A", categoryIds: [1], ingredientNames: ["kycklingfilé"], isFavorite: false);
        var favoriteRecipe = CreateRecipe(2, "Rätt B", categoryIds: [1], ingredientNames: ["kycklingfilé"], isFavorite: true);

        double wRegular = generator.CalculateRecipeWeight(regularRecipe, history: null, pantryIngredients: ["kycklingfilé"]);
        double wFavorite = generator.CalculateRecipeWeight(favoriteRecipe, history: null, pantryIngredients: ["kycklingfilé"]);

        wFavorite.Should().Be(wRegular, "favoritmarkerade recept ska inte få någon särskild bonus (E1)");
    }

    [Fact]
    public void PantryIngredientMatcher_And_CalculateRecipeWeight_ShouldMatchInflectionsAndSynonyms_AndIgnoreTrivialStaples()
    {
        var matcher = new PantryIngredientMatcher();
        var generator = new WeeklyMenuGenerator(pantryMatcher: matcher);

        // Inflections & Synonyms per D2
        matcher.IsMatch("Kycklingfilé", "kycklingfiléer").Should().BeTrue();
        matcher.IsMatch("kycklingfiléer", "Kycklingfilé").Should().BeTrue();
        matcher.IsMatch("Köttfärs", "nötfärs").Should().BeTrue();
        matcher.IsMatch("Lax", "laxfilé").Should().BeTrue();
        matcher.IsMatch("laxfilé", "färsk lax").Should().BeTrue();

        // Negative cases: no aggressive fuzzy matching
        matcher.IsMatch("lök", "vitlök").Should().BeFalse();
        matcher.IsMatch("lök", "purjolök").Should().BeFalse();
        matcher.IsMatch("mjöl", "mjölk").Should().BeFalse();
        matcher.IsMatch("ris", "chorizo").Should().BeFalse();

        // Trivial staples should not match or give bonus
        matcher.IsMatch("salt", "salt").Should().BeFalse();
        matcher.IsMatch("vatten", "vatten").Should().BeFalse();

        var recipeWithSaltAndWater = CreateRecipe(1, "Enkel soppa", ingredientNames: ["salt", "vatten", "potatis"]);
        var recipeWithChickenAndSalmon = CreateRecipe(2, "Kycklingpanna", ingredientNames: ["kycklingfiléer"]);

        double baseWeightSaltWater = generator.CalculateRecipeWeight(recipeWithSaltAndWater, null, pantryIngredients: []);
        double pantryWeightSaltWater = generator.CalculateRecipeWeight(recipeWithSaltAndWater, null, pantryIngredients: ["salt", "vatten"]);
        pantryWeightSaltWater.Should().Be(baseWeightSaltWater, "salt och vatten ska inte öka poängen");

        double baseChicken = generator.CalculateRecipeWeight(recipeWithChickenAndSalmon, null, pantryIngredients: []);
        double matchedChicken = generator.CalculateRecipeWeight(recipeWithChickenAndSalmon, null, pantryIngredients: ["Kycklingfilé"]);
        matchedChicken.Should().BeGreaterThan(baseChicken);
    }

    [Fact]
    public void BuildHistoryLookup_ShouldOnlyCountConfirmedMenus_AndExcludeDraftsAndTargetWeek()
    {
        var targetMonday = new DateOnly(2026, 11, 2);
        var prevMonday = targetMonday.AddDays(-7);
        var twoWeeksAgo = targetMonday.AddDays(-14);

        var confirmedPrevWeek = new WeeklyMenu
        {
            WeekStartDate = prevMonday,
            Status = WeeklyMenuStatus.Confirmed,
            Days = [new WeeklyMenuDay { DayIndex = 0, Date = prevMonday, RecipeId = 10 }]
        };

        var draftTwoWeeksAgo = new WeeklyMenu
        {
            WeekStartDate = twoWeeksAgo,
            Status = WeeklyMenuStatus.Draft,
            Days = [new WeeklyMenuDay { DayIndex = 0, Date = twoWeeksAgo, RecipeId = 20 }]
        };

        var confirmedTargetWeekItself = new WeeklyMenu
        {
            WeekStartDate = targetMonday,
            Status = WeeklyMenuStatus.Confirmed,
            Days = [new WeeklyMenuDay { DayIndex = 0, Date = targetMonday, RecipeId = 30 }]
        };

        var lookup = WeeklyMenuGenerator.BuildHistoryLookup(
            [confirmedPrevWeek, draftTwoWeeksAgo, confirmedTargetWeekItself],
            targetMonday);

        lookup.Should().ContainKey(10);
        lookup[10].TotalConfirmedOccurrences.Should().Be(1);
        lookup[10].WasPlannedPreviousWeek.Should().BeTrue();
        lookup[10].WeeksSinceLastPlanned.Should().Be(1);

        lookup.Should().NotContainKey(20, "utkast ska inte påverka historiken");
        lookup.Should().NotContainKey(30, "den egna veckan ska inte dubbelräknas mot sig själv vid redigering");
    }

    [Fact]
    public void RerollDay_ShouldReplaceSingleDay_KeepOtherDays_AvoidCurrentAndOtherDaysRecipes_AndReportWhenNoOtherCandidate()
    {
        var generator = new WeeklyMenuGenerator(new SeededRandomProvider(123));
        var recipes = Enumerable.Range(1, 8)
            .Select(i => CreateRecipe(i, $"Recept {i}", categoryIds: [1]))
            .ToList();

        var menu = CreateEmptyMenuForWeek(new DateOnly(2026, 11, 2), defaultCategoryId: 1);
        for (int i = 0; i < 7; i++)
        {
            menu.Days[i].RecipeId = recipes[i].Id;
            menu.Days[i].Recipe = recipes[i];
        }

        // Only Recipe 8 is unused across the other 6 days and different from Day 2's current Recipe 3!
        var rerollResult = generator.RerollDay(menu, dayIndex: 2, recipes);

        rerollResult.Changed.Should().BeTrue();
        rerollResult.NewRecipeId.Should().Be(8);
        menu.Days[2].RecipeId.Should().Be(8);

        // Other 6 days are completely unchanged
        menu.Days[0].RecipeId.Should().Be(1);
        menu.Days[1].RecipeId.Should().Be(2);
        menu.Days[3].RecipeId.Should().Be(4);
        menu.Days[4].RecipeId.Should().Be(5);
        menu.Days[5].RecipeId.Should().Be(6);
        menu.Days[6].RecipeId.Should().Be(7);

        // If only 7 recipes exist total, re-rolling Day 2 has no other unused candidate -> returns clear message
        var sevenRecipesOnly = recipes.Take(7).ToList();
        menu.Days[2].RecipeId = 3;
        menu.Days[2].Recipe = recipes[2];

        var noOtherResult = generator.RerollDay(menu, dayIndex: 2, sevenRecipesOnly);
        noOtherResult.Changed.Should().BeFalse();
        noOtherResult.Message.Should().NotBeNullOrWhiteSpace();
        menu.Days[2].RecipeId.Should().Be(3);
    }

    [Fact]
    public void MoveOrSwapRecipe_ShouldSwapRecipesOrMoveToEmptyDay_AndKeepCategoriesOnEachDay()
    {
        var generator = new WeeklyMenuGenerator();
        var r1 = CreateRecipe(1, "Vardagspasta", categoryIds: [1]);
        var r2 = CreateRecipe(2, "Helgstek", categoryIds: [2]);

        var menu = CreateEmptyMenuForWeek(new DateOnly(2026, 11, 2));
        // Monday has Category 1 and Recipe 1
        menu.Days[0].SelectedCategories.Add(new WeeklyMenuDayCategory { CategoryId = 1 });
        menu.Days[0].RecipeId = r1.Id;
        menu.Days[0].Recipe = r1;
        menu.Days[0].Servings = 4;

        // Friday has Category 2 and Recipe 2
        menu.Days[4].SelectedCategories.Add(new WeeklyMenuDayCategory { CategoryId = 2 });
        menu.Days[4].RecipeId = r2.Id;
        menu.Days[4].Recipe = r2;
        menu.Days[4].Servings = 6;

        // Tuesday has Category 3 and is empty
        menu.Days[1].SelectedCategories.Add(new WeeklyMenuDayCategory { CategoryId = 3 });

        // 1. Swap Monday (0) and Friday (4)
        generator.MoveOrSwapRecipe(menu, 0, 4);

        menu.Days[0].RecipeId.Should().Be(2);
        menu.Days[0].Servings.Should().Be(6);
        menu.Days[0].SelectedCategories.Select(c => c.CategoryId).Should().Equal([1], "kategorin tillhör måndagen");

        menu.Days[4].RecipeId.Should().Be(1);
        menu.Days[4].Servings.Should().Be(4);
        menu.Days[4].SelectedCategories.Select(c => c.CategoryId).Should().Equal([2], "kategorin tillhör fredagen");

        // 2. Move from Friday (4) to empty Tuesday (1)
        generator.MoveOrSwapRecipe(menu, 4, 1);

        menu.Days[1].RecipeId.Should().Be(1);
        menu.Days[1].SelectedCategories.Select(c => c.CategoryId).Should().Equal([3], "tisdagens kategori ligger kvar");
        menu.Days[4].RecipeId.Should().BeNull("fredagen blev tom efter flytt till tom dag");
        menu.Days[4].SelectedCategories.Select(c => c.CategoryId).Should().Equal([2]);
    }

    [Fact]
    public void GenerateWeek_PantryFirst_WhenPantryIngredientSpecified_ShouldGuaranteeMatchingRecipeOnEligibleDay()
    {
        var generator = new WeeklyMenuGenerator(new SeededRandomProvider(42));

        // 10 recipes in Category 1 (Bakning & Desserter), but only Recipe 5 has "blåbär"
        var recipes = Enumerable.Range(1, 10)
            .Select(i => CreateRecipe(i, i == 5 ? "Blåbärspannacotta" : $"Dessert {i}", categoryIds: [1], ingredientNames: i == 5 ? ["blåbär"] : ["socker", "vetemjöl"]))
            .ToList();

        var menu = CreateEmptyMenuForWeek(new DateOnly(2026, 11, 2));
        // Day 0 has Category 1 (Bakning & Desserter)
        menu.Days[0].SelectedCategories.Add(new WeeklyMenuDayCategory { CategoryId = 1 });
        // Days 1..6 have Category 99 (no recipes)
        for (int i = 1; i < 7; i++)
        {
            menu.Days[i].SelectedCategories.Add(new WeeklyMenuDayCategory { CategoryId = 99 });
        }

        // Specify "Blåbär" under Ingredienser hemma
        menu.PantryIngredients = ["Blåbär"];

        var result = generator.GenerateWeek(menu, recipes);

        result.FilledDaysCount.Should().Be(1);
        menu.Days[0].RecipeId.Should().Be(5, "Pantry-first ska garantera att Blåbärspannacotta väljs när blåbär är specificerat hemma");
    }

    [Fact]
    public void RerollDay_PantryFirst_ShouldPickPantryMatchingRecipeFirst_AndFallbackWhenAlreadySelected()
    {
        var generator = new WeeklyMenuGenerator(new SeededRandomProvider(99));

        var r1 = CreateRecipe(1, "Chokladmousse", categoryIds: [1], ingredientNames: ["choklad"]);
        var r2 = CreateRecipe(2, "Blåbärspannacotta", categoryIds: [1], ingredientNames: ["blåbär"]);
        var r3 = CreateRecipe(3, "Äppelpaj", categoryIds: [1], ingredientNames: ["äpple"]);

        var menu = CreateEmptyMenuForWeek(new DateOnly(2026, 11, 2));
        menu.Days[0].SelectedCategories.Add(new WeeklyMenuDayCategory { CategoryId = 1 });
        menu.Days[0].RecipeId = r1.Id;
        menu.Days[0].Recipe = r1;
        menu.PantryIngredients = ["Blåbär"];

        // 1. Reroll Day 0: Even though r3 (Äppelpaj) is also available, r2 (Blåbärspannacotta) matches the pantry ingredient and must be chosen!
        var reroll1 = generator.RerollDay(menu, dayIndex: 0, [r1, r2, r3]);
        reroll1.Changed.Should().BeTrue();
        reroll1.NewRecipeId.Should().Be(2);
        menu.Days[0].RecipeId.Should().Be(2);

        // 2. Reroll Day 0 again when Blåbärspannacotta is currently displayed:
        // No other blueberry recipe exists, so it smoothly falls back to other available candidates (r1 or r3) without getting stuck.
        var reroll2 = generator.RerollDay(menu, dayIndex: 0, [r1, r2, r3]);
        reroll2.Changed.Should().BeTrue();
        reroll2.NewRecipeId.Should().BeOneOf(1, 3);
    }

    [Fact]
    public void GenerateWeek_PantryFirst_WithMultiplePantryIngredients_ShouldSatisfyAcrossDifferentDays()
    {
        var generator = new WeeklyMenuGenerator(new SeededRandomProvider(1));

        var dessert1 = CreateRecipe(1, "Kladdkaka", categoryIds: [1]);
        var dessert2 = CreateRecipe(2, "Blåbärspannacotta", categoryIds: [1], ingredientNames: ["blåbär"]);
        var fish1 = CreateRecipe(3, "Torskrygg", categoryIds: [2], ingredientNames: ["torsk"]);
        var fish2 = CreateRecipe(4, "Laxfilé med dillsås", categoryIds: [2], ingredientNames: ["lax"]);
        var meat1 = CreateRecipe(5, "Köttfärssås", categoryIds: [3], ingredientNames: ["nötfärs"]);

        var menu = CreateEmptyMenuForWeek(new DateOnly(2026, 11, 2));
        menu.Days[0].SelectedCategories.Add(new WeeklyMenuDayCategory { CategoryId = 1 }); // Bakning
        menu.Days[1].SelectedCategories.Add(new WeeklyMenuDayCategory { CategoryId = 2 }); // Fisk
        menu.Days[2].SelectedCategories.Add(new WeeklyMenuDayCategory { CategoryId = 3 }); // Kött
        for (int i = 3; i < 7; i++)
        {
            menu.Days[i].SelectedCategories.Add(new WeeklyMenuDayCategory { CategoryId = 99 });
        }

        menu.PantryIngredients = ["Blåbär", "Lax"];

        var result = generator.GenerateWeek(menu, [dessert1, dessert2, fish1, fish2, meat1]);

        result.FilledDaysCount.Should().Be(3);
        menu.Days[0].RecipeId.Should().Be(2, "Dag 0 ska välja Blåbärspannacotta för att tillgodose Blåbär");
        menu.Days[1].RecipeId.Should().Be(4, "Dag 1 ska välja Laxfilé för att tillgodose Lax");
        menu.Days[2].RecipeId.Should().Be(5);
    }

    private static RecipeRating CreateRating(int stars, bool isActive = true, int memberId = 1) =>
        new()
        {
            FamilyMemberId = memberId,
            Stars = stars,
            FamilyMember = new FamilyMember
            {
                Id = memberId,
                Name = $"Medlem {memberId}",
                IsActive = isActive
            }
        };

    [Theory]
    [InlineData(new int[0], 1.0)]
    [InlineData(new[] { 5 }, 1.0833333333333333)]
    [InlineData(new[] { 1 }, 0.9166666666666666)]
    [InlineData(new[] { 5, 5, 5, 5 }, 1.1666666666666667)]
    [InlineData(new[] { 1, 1, 1, 1 }, 0.8333333333333334)]
    [InlineData(new[] { 3, 3, 3, 3 }, 1.0)]
    public void CalculateRatingMultiplier_ShouldMatchBayesianFormula(int[] stars, double expectedMultiplier)
    {
        var generator = new WeeklyMenuGenerator();
        var recipe = CreateRecipe(1, "Testrecept");
        recipe.Ratings = stars
            .Select((s, idx) => CreateRating(s, isActive: true, memberId: idx + 1))
            .ToList();

        double multiplier = generator.CalculateRatingMultiplier(recipe);

        multiplier.Should().BeApproximately(expectedMultiplier, 1e-5);
        if (stars.Length == 0 || stars.All(s => s == 3))
        {
            multiplier.Should().Be(1.0);
        }
    }

    [Fact]
    public void CalculateRatingMultiplier_ShouldExcludeInactiveMembers_AndRestoreWhenReactivated()
    {
        var generator = new WeeklyMenuGenerator();
        var recipe = CreateRecipe(1, "Familjerecept");
        var activeMember = new FamilyMember { Id = 1, Name = "Aktiv", IsActive = true };
        var inactiveMember1 = new FamilyMember { Id = 2, Name = "Inaktiv 1", IsActive = false };
        var inactiveMember2 = new FamilyMember { Id = 3, Name = "Inaktiv 2", IsActive = false };

        recipe.Ratings =
        [
            new RecipeRating { FamilyMemberId = 1, Stars = 5, FamilyMember = activeMember },
            new RecipeRating { FamilyMemberId = 2, Stars = 1, FamilyMember = inactiveMember1 },
            new RecipeRating { FamilyMemberId = 3, Stars = 1, FamilyMember = inactiveMember2 }
        ];

        // Only activeMember (one 5-star vote) counts -> 1.08333
        generator.CalculateRatingMultiplier(recipe).Should().BeApproximately(1.0 + (1.0 / 12.0), 1e-5);

        // Deactivate activeMember too -> no active votes -> 1.0
        activeMember.IsActive = false;
        generator.CalculateRatingMultiplier(recipe).Should().Be(1.0);

        // Reactivate activeMember and inactiveMember1 -> one 5-star and one 1-star -> avg 3.0 -> 1.0
        activeMember.IsActive = true;
        inactiveMember1.IsActive = true;
        generator.CalculateRatingMultiplier(recipe).Should().Be(1.0);

        // Reactivate inactiveMember2 -> one 5-star and two 1-star -> avg 7/3, n=3 -> 1 + 0.125 * (3/5) * (7/3 - 3) = 0.95
        inactiveMember2.IsActive = true;
        generator.CalculateRatingMultiplier(recipe).Should().BeApproximately(0.95, 1e-5);
    }

    [Fact]
    public void CalculateRatingMultiplier_ShouldStayWithinBounds_AndHandleInvalidRatingsSafely()
    {
        var generator = new WeeklyMenuGenerator();

        var manyFiveStars = CreateRecipe(1, "Topprätt");
        manyFiveStars.Ratings = Enumerable.Range(1, 100)
            .Select(i => CreateRating(5, isActive: true, memberId: i))
            .ToList();

        var manyOneStars = CreateRecipe(2, "Bottenrätt");
        manyOneStars.Ratings = Enumerable.Range(1, 100)
            .Select(i => CreateRating(1, isActive: true, memberId: i))
            .ToList();

        var invalidRatings = CreateRecipe(3, "Ogiltiga betyg");
        invalidRatings.Ratings =
        [
            CreateRating(0, isActive: true, memberId: 1),
            CreateRating(6, isActive: true, memberId: 2),
            new RecipeRating { FamilyMemberId = 3, Stars = 5, FamilyMember = null! }
        ];

        double highMult = generator.CalculateRatingMultiplier(manyFiveStars);
        double lowMult = generator.CalculateRatingMultiplier(manyOneStars);
        double invalidMult = generator.CalculateRatingMultiplier(invalidRatings);

        highMult.Should().BeInRange(0.75, 1.25);
        highMult.Should().BeGreaterThan(1.20);
        lowMult.Should().BeInRange(0.75, 1.25);
        lowMult.Should().BeLessThan(0.80);
        invalidMult.Should().Be(1.0);
        double.IsNaN(invalidMult).Should().BeFalse();
    }

    [Fact]
    public void CalculateRecipeWeight_ShouldApplyRatingInfluence_WhilePreservingRecencyPantryAndFloor()
    {
        var generator = new WeeklyMenuGenerator();
        var unrated = CreateRecipe(1, "Ograderad", categoryIds: [1], ingredientNames: ["lax"]);
        var highRated = CreateRecipe(2, "Högst betyg", categoryIds: [1], ingredientNames: ["lax"]);
        var lowRated = CreateRecipe(3, "Lägst betyg", categoryIds: [1], ingredientNames: ["lax"]);

        highRated.Ratings = Enumerable.Range(1, 4).Select(i => CreateRating(5, true, i)).ToList();
        lowRated.Ratings = Enumerable.Range(1, 4).Select(i => CreateRating(1, true, i)).ToList();

        double wUnrated = generator.CalculateRecipeWeight(unrated, history: null);
        double wHigh = generator.CalculateRecipeWeight(highRated, history: null);
        double wLow = generator.CalculateRecipeWeight(lowRated, history: null);

        wHigh.Should().BeApproximately(wUnrated * (1.0 + (1.0 / 6.0)), 1e-6);
        wLow.Should().BeApproximately(wUnrated * (1.0 - (1.0 / 6.0)), 1e-6);
        wHigh.Should().BeGreaterThan(wUnrated);
        wLow.Should().BeLessThan(wUnrated);
        wLow.Should().BeGreaterThanOrEqualTo(generator.Weights.MinimumWeight);

        // Recency penalty still strongly discourages a high-rated recipe planned last week
        var prevWeekStats = new RecipeHistoryStats(
            RecipeId: highRated.Id,
            TotalConfirmedOccurrences: 1,
            LastPlannedWeekStartDate: new DateOnly(2026, 10, 26),
            WeeksSinceLastPlanned: 1,
            WasPlannedPreviousWeek: true);

        double wHighLastWeek = generator.CalculateRecipeWeight(highRated, prevWeekStats);
        double wLowLastWeek = generator.CalculateRecipeWeight(lowRated, prevWeekStats with { RecipeId = lowRated.Id });

        wHighLastWeek.Should().BeLessThan(wLow);
        wHighLastWeek.Should().BeLessThan(wUnrated * 0.25);
        wLowLastWeek.Should().BeGreaterThanOrEqualTo(generator.Weights.MinimumWeight);

        // Pantry bonus remains effective
        double wUnratedPantry = generator.CalculateRecipeWeight(unrated, history: null, pantryIngredients: ["lax"]);
        double wHighPantry = generator.CalculateRecipeWeight(highRated, history: null, pantryIngredients: ["lax"]);
        wUnratedPantry.Should().BeGreaterThan(wUnrated);
        wHighPantry.Should().BeGreaterThan(wUnratedPantry);
    }

    [Fact]
    public void GenerateWeek_And_RerollDay_WithRatings_ShouldPreserveMatchingPantryAndUseRatingWeights()
    {
        // 1. Bipartite matching safeguard still prevents starving constrained Day 1 even if r1 is 5-star rated
        var matchingGenerator = new WeeklyMenuGenerator(new FixedSequenceRandomProvider(0.0));
        var r1 = CreateRecipe(1, "Lyxpasta", categoryIds: [1, 2]);
        r1.Ratings = Enumerable.Range(1, 4).Select(i => CreateRating(5, true, i)).ToList();
        var r2 = CreateRecipe(2, "Köttbullar", categoryIds: [1]);
        r2.Ratings = Enumerable.Range(1, 4).Select(i => CreateRating(1, true, i)).ToList();

        var menu = CreateEmptyMenuForWeek(new DateOnly(2026, 11, 2));
        menu.Days[0].SelectedCategories.Add(new WeeklyMenuDayCategory { CategoryId = 1 });
        menu.Days[1].SelectedCategories.Add(new WeeklyMenuDayCategory { CategoryId = 2 });
        for (int i = 2; i < 7; i++)
        {
            menu.Days[i].SelectedCategories.Add(new WeeklyMenuDayCategory { CategoryId = 99 });
        }

        matchingGenerator.GenerateWeek(menu, [r1, r2]);
        menu.Days[0].RecipeId.Should().Be(2);
        menu.Days[1].RecipeId.Should().Be(1);

        // 2. Midpoint random sample (0.5) favors higher-rated recipe in both GenerateWeek and RerollDay
        var midpointGenerator = new WeeklyMenuGenerator(new FixedSequenceRandomProvider(0.5));
        var lowChoice = CreateRecipe(10, "Lågt betyg", categoryIds: [1]);
        lowChoice.Ratings = Enumerable.Range(1, 4).Select(i => CreateRating(1, true, i)).ToList();
        var highChoice = CreateRecipe(11, "Högt betyg", categoryIds: [1]);
        highChoice.Ratings = Enumerable.Range(1, 4).Select(i => CreateRating(5, true, i)).ToList();

        var genMenu = CreateEmptyMenuForWeek(new DateOnly(2026, 11, 2));
        genMenu.Days[0].SelectedCategories.Add(new WeeklyMenuDayCategory { CategoryId = 1 });
        for (int i = 1; i < 7; i++)
        {
            genMenu.Days[i].SelectedCategories.Add(new WeeklyMenuDayCategory { CategoryId = 99 });
        }

        midpointGenerator.GenerateWeek(genMenu, [lowChoice, highChoice]);
        genMenu.Days[0].RecipeId.Should().Be(11, "0.5 ligger över låg-betygets viktsgräns (< 0.5) och väljer högre rankade receptet");

        // RerollDay: Day 0 currently has another recipe (id 12), candidates are lowChoice (10) and highChoice (11)
        var current = CreateRecipe(12, "Nuvarande", categoryIds: [1]);
        genMenu.Days[0].RecipeId = current.Id;
        genMenu.Days[0].Recipe = current;

        var reroll = midpointGenerator.RerollDay(genMenu, 0, [lowChoice, highChoice, current]);
        reroll.Changed.Should().BeTrue();
        reroll.NewRecipeId.Should().Be(11);
    }

    private static IReadOnlyList<double> NormalizeProbabilities(params double[] weights)
    {
        double total = weights.Sum();
        return weights.Select(w => w / total).ToList();
    }

    [Fact]
    public void CalculateRecipeWeight_DeterministicProbability_RatingOnlyComparison()
    {
        var generator = new WeeklyMenuGenerator();

        // Both recipes have no planning history and no pantry matches:
        // base-before-rating = 100 (Base) + 60 (NeverPlanned) + 40 (LowFreq) = 200.0
        var recipeA = CreateRecipe(1, "Recept A (4x5 stjärnor)", categoryIds: [1]);
        recipeA.Ratings = Enumerable.Range(1, 4).Select(i => CreateRating(5, isActive: true, memberId: i)).ToList();

        var recipeB = CreateRecipe(2, "Recept B (4x1 stjärna)", categoryIds: [1]);
        recipeB.Ratings = Enumerable.Range(1, 4).Select(i => CreateRating(1, isActive: true, memberId: i)).ToList();

        double multA = generator.CalculateRatingMultiplier(recipeA);
        double multB = generator.CalculateRatingMultiplier(recipeB);
        multA.Should().BeApproximately(1.1666667, 1e-6);
        multB.Should().BeApproximately(0.8333333, 1e-6);

        double weightA = generator.CalculateRecipeWeight(recipeA, history: null);
        double weightB = generator.CalculateRecipeWeight(recipeB, history: null);
        weightA.Should().BeGreaterThan(weightB);

        // Conditional selection probabilities over the fixed 2-recipe candidate pool [A, B]:
        // P(A) = (200 * 7/6) / (200 * 7/6 + 200 * 5/6) = 7/12 ≈ 0.5833333
        // P(B) = (200 * 5/6) / (200 * 7/6 + 200 * 5/6) = 5/12 ≈ 0.4166667
        var probs = NormalizeProbabilities(weightA, weightB);
        probs[0].Should().BeApproximately(0.5833333, 1e-6);
        probs[1].Should().BeApproximately(0.4166667, 1e-6);
        probs.Sum().Should().BeApproximately(1.0, 1e-9);
    }

    [Fact]
    public void CalculateRecipeWeight_DeterministicProbability_UnratedRemainsCompetitive()
    {
        var generator = new WeeklyMenuGenerator();

        var highRated = CreateRecipe(1, "Högst betyg", categoryIds: [1]);
        highRated.Ratings = Enumerable.Range(1, 4).Select(i => CreateRating(5, isActive: true, memberId: i)).ToList();

        var unrated = CreateRecipe(2, "Ograderad", categoryIds: [1]);

        var lowRated = CreateRecipe(3, "Lägst betyg", categoryIds: [1]);
        lowRated.Ratings = Enumerable.Range(1, 4).Select(i => CreateRating(1, isActive: true, memberId: i)).ToList();

        generator.CalculateRatingMultiplier(unrated).Should().Be(1.0);

        double wHigh = generator.CalculateRecipeWeight(highRated, history: null);
        double wUnrated = generator.CalculateRecipeWeight(unrated, history: null);
        double wLow = generator.CalculateRecipeWeight(lowRated, history: null);

        // Conditional probabilities over the fixed 3-recipe candidate pool:
        // P(high) = 7/18 ≈ 0.3888889, P(unrated) = 6/18 ≈ 0.3333333, P(low) = 5/18 ≈ 0.2777778
        var probs = NormalizeProbabilities(wHigh, wUnrated, wLow);
        probs[0].Should().BeGreaterThan(probs[1]);
        probs[1].Should().BeGreaterThan(probs[2]);
        probs.Should().AllSatisfy(p => p.Should().BeGreaterThan(0.0));
        probs.Sum().Should().BeApproximately(1.0, 1e-9);
    }

    [Fact]
    public void CalculateRecipeWeight_DeterministicProbability_CombinedRelevanceFactors()
    {
        var generator = new WeeklyMenuGenerator();
        var pantry = new[] { "lax" };
        var prevWeekMonday = new DateOnly(2026, 10, 26);

        // 1. Unrated, never planned, no pantry match:
        //    100 (Base) + 60 (NeverPlanned) + 40 (LowFreq) = 200.0; rating mult = 1.0 -> 200.0
        var unratedNeverPlanned = CreateRecipe(1, "Ograderad utan skafferi", categoryIds: [1], ingredientNames: ["torsk"]);

        // 2. Low-rated (4x1 star -> mult 5/6), never planned, substantial pantry match (400 g lax -> relevance 1.2):
        //    (100 + 60 + 40 + 55*1.2) * (5/6) = 266.0 * (5/6) ≈ 221.6666667
        var lowRatedWithPantry = CreateRecipe(2, "Låg-betyg med lax", categoryIds: [1], ingredientNames: ["lax"]);
        lowRatedWithPantry.Ratings = Enumerable.Range(1, 4).Select(i => CreateRating(1, isActive: true, memberId: i)).ToList();

        // 3. High-rated (4x5 stars -> mult 7/6), planned 1 time last week, no pantry match:
        //    (100 + 10 (1*10 recency) + 20 (40/2 low-freq)) * (7/6) * 0.12 (prev-week penalty) = 130 * (7/6) * 0.12 = 18.2
        var highRatedPlannedLastWeek = CreateRecipe(3, "5-stjärnig från förra veckan", categoryIds: [1], ingredientNames: ["kyckling"]);
        highRatedPlannedLastWeek.Ratings = Enumerable.Range(1, 4).Select(i => CreateRating(5, isActive: true, memberId: i)).ToList();
        var prevWeekStats = new RecipeHistoryStats(
            RecipeId: highRatedPlannedLastWeek.Id,
            TotalConfirmedOccurrences: 1,
            LastPlannedWeekStartDate: prevWeekMonday,
            WeeksSinceLastPlanned: 1,
            WasPlannedPreviousWeek: true);

        double wUnrated = generator.CalculateRecipeWeight(unratedNeverPlanned, history: null, pantryIngredients: pantry);
        double wLowPantry = generator.CalculateRecipeWeight(lowRatedWithPantry, history: null, pantryIngredients: pantry);
        double wHighPrevWeek = generator.CalculateRecipeWeight(highRatedPlannedLastWeek, history: prevWeekStats, pantryIngredients: pantry);

        wUnrated.Should().BeApproximately(200.0, 1e-6);
        wLowPantry.Should().BeApproximately(266.0 * (5.0 / 6.0), 1e-6);
        wHighPrevWeek.Should().BeApproximately(18.2, 1e-6);

        // Conditional probabilities over this fixed 3-recipe candidate pool:
        // Total = 200.0 + 221.6666667 + 18.2 = 439.8666667
        // P(lowRatedWithPantry) ≈ 0.5039405 > P(unratedNeverPlanned) ≈ 0.4546832 > P(highRatedPlannedLastWeek) ≈ 0.0413762
        var probs = NormalizeProbabilities(wLowPantry, wUnrated, wHighPrevWeek);
        probs[0].Should().BeApproximately(221.6666667 / 439.8666667, 1e-5);
        probs[1].Should().BeApproximately(200.0 / 439.8666667, 1e-5);
        probs[2].Should().BeApproximately(18.2 / 439.8666667, 1e-5);

        probs[0].Should().BeGreaterThan(probs[1], "skafferirelevans kan väga tyngre än en måttlig negativ betygsmultiplikator");
        probs[1].Should().BeGreaterThan(probs[2], "ett nyligen planerat 5-stjärnigt recept är fortfarande mindre sannolikt än ett ograderat recept som inte planerats nyligen");
        probs.Sum().Should().BeApproximately(1.0, 1e-9);
    }
}

