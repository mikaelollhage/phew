using Bunit;
using FluentAssertions;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.FluentUI.AspNetCore.Components;
using Receptbanken.Core.Interfaces;
using Receptbanken.Core.Models;
using Receptbanken.Core.Services;
using Receptbanken.Infrastructure.Services;
using Receptbanken.Tests.Infrastructure;
using Receptbanken.Web.Components.Pages;
using Receptbanken.Web.Components.Recipes;
using Receptbanken.Web.Services;
using Xunit;

namespace Receptbanken.Tests.Components;

public class IngredientSectionReorderingTests : TestContext
{
    public IngredientSectionReorderingTests()
    {
        Services.AddFluentUIComponents();
        JSInterop.Mode = JSRuntimeMode.Loose;
    }

    [Fact]
    public void IngredientSectionList_LoadAndSync_PreservesExactOrderRepeatedGroupsAndUngroupedBetweenGroups()
    {
        var original = new List<Ingredient>
        {
            new() { OrderIndex = 1, Name = "salt", GroupHeader = null },
            new() { OrderIndex = 2, Name = "mjöl", GroupHeader = "Deg" },
            new() { OrderIndex = 3, Name = "smör", GroupHeader = "Deg" },
            new() { OrderIndex = 4, Name = "vatten", GroupHeader = null },
            new() { OrderIndex = 5, Name = "socker", GroupHeader = "Deg" }
        };

        var list = new IngredientSectionList();
        list.LoadFromIngredients(original);

        // Expect: salt, Section("Deg"), mjöl, smör, Section("") [ungrouped boundary], vatten, Section("Deg"), socker
        list.Items.Select(i => i.IsSection ? $"[{i.SectionTitle}]" : i.Ingredient!.Name)
            .Should().Equal("salt", "[Deg]", "mjöl", "smör", "[]", "vatten", "[Deg]", "socker");

        var synced = new List<Ingredient>();
        list.SyncToIngredients(synced);

        synced.Select(i => (i.Name, i.GroupHeader, i.OrderIndex))
            .Should().Equal(
                ("salt", (string?)null, 1),
                ("mjöl", "Deg", 2),
                ("smör", "Deg", 3),
                ("vatten", (string?)null, 4),
                ("socker", "Deg", 5));
    }

    [Fact]
    public void IngredientSectionList_MoveIngredientAcrossSections_UpdatesGroupHeaderAndOrderIndex()
    {
        var ingredients = new List<Ingredient>
        {
            new() { OrderIndex = 1, Name = "mjöl", GroupHeader = "Deg" },
            new() { OrderIndex = 2, Name = "smör", GroupHeader = "Deg" },
            new() { OrderIndex = 3, Name = "äpple", GroupHeader = "Fyllning" }
        };

        var list = new IngredientSectionList();
        list.LoadFromIngredients(ingredients);

        var mjolKey = list.Items.Single(i => i.IsIngredient && i.Ingredient!.Name == "mjöl").Key;
        var appleKey = list.Items.Single(i => i.IsIngredient && i.Ingredient!.Name == "äpple").Key;

        // Drag mjöl down onto äpple (into "Fyllning")
        list.MoveItem(mjolKey, appleKey, ingredients).Should().BeTrue();

        ingredients.Select(i => (i.Name, i.GroupHeader, i.OrderIndex))
            .Should().Equal(
                ("smör", "Deg", 1),
                ("äpple", "Fyllning", 2),
                ("mjöl", "Fyllning", 3));
    }

    [Fact]
    public void IngredientSectionList_MoveSectionHeader_MovesOnlyHeaderAndReassignsFollowingIngredients()
    {
        var ingredients = new List<Ingredient>
        {
            new() { OrderIndex = 1, Name = "Ingrediens 1", GroupHeader = "Sektion A" },
            new() { OrderIndex = 2, Name = "Ingrediens 2", GroupHeader = "Sektion A" },
            new() { OrderIndex = 3, Name = "Ingrediens 3", GroupHeader = "Sektion B" },
            new() { OrderIndex = 4, Name = "Ingrediens 4", GroupHeader = "Sektion B" }
        };

        var list = new IngredientSectionList();
        list.LoadFromIngredients(ingredients);

        var sectionBKey = list.Items.First(i => i.IsSection && i.SectionTitle == "Sektion B").Key;
        var ing2Key = list.Items.Single(i => i.IsIngredient && i.Ingredient!.Name == "Ingrediens 2").Key;

        // Drop indicator shows exact insertion position above Ingrediens 2
        list.GetDropIndicatorTarget(sectionBKey, ing2Key).Should().Be((ing2Key, true));

        // Move Sektion B header between Ingrediens 1 and Ingrediens 2
        list.MoveItem(sectionBKey, ing2Key, ingredients).Should().BeTrue();

        ingredients.Select(i => (i.Name, i.GroupHeader, i.OrderIndex))
            .Should().Equal(
                ("Ingrediens 1", "Sektion A", 1),
                ("Ingrediens 2", "Sektion B", 2),
                ("Ingrediens 3", "Sektion B", 3),
                ("Ingrediens 4", "Sektion B", 4));

        // Move Sektion B header back down by 1 row via offset (keyboard/button fallback)
        list.MoveByOffset(sectionBKey, 1, ingredients).Should().BeTrue();

        ingredients.Select(i => (i.Name, i.GroupHeader, i.OrderIndex))
            .Should().Equal(
                ("Ingrediens 1", "Sektion A", 1),
                ("Ingrediens 2", "Sektion A", 2),
                ("Ingrediens 3", "Sektion B", 3),
                ("Ingrediens 4", "Sektion B", 4));
    }

    [Fact]
    public void IngredientSectionList_RenameAndRemoveSection_PreservesIngredients_AndEmptySectionNotSaved()
    {
        var ingredients = new List<Ingredient>
        {
            new() { OrderIndex = 1, Name = "grädde", GroupHeader = "Sås" },
            new() { OrderIndex = 2, Name = "smör", GroupHeader = "Sås" }
        };

        var list = new IngredientSectionList();
        list.LoadFromIngredients(ingredients);

        var sasKey = list.Items.First(i => i.IsSection && i.SectionTitle == "Sås").Key;
        var emptySection = list.AddSection("Tom sektion", ingredients);

        list.GetSectionIngredientCount(emptySection.Key).Should().Be(0);
        // Empty section does not affect saved ingredients
        ingredients.Select(i => (i.Name, i.GroupHeader)).Should().Equal(
            ("grädde", "Sås"),
            ("smör", "Sås"));

        // Rename section
        list.RenameSection(sasKey, "Varm sås", ingredients).Should().BeTrue();
        ingredients.Should().OnlyContain(i => i.GroupHeader == "Varm sås");

        // Remove section without deleting ingredients
        list.RemoveSection(sasKey, ingredients).Should().BeTrue();
        ingredients.Select(i => (i.Name, i.GroupHeader)).Should().Equal(
            ("grädde", (string?)null),
            ("smör", (string?)null));
    }

    [Fact]
    public void RecipeIngredientEditor_ShouldPreservingEmptySectionsOnRerender_AndSupportKeyboardFallback()
    {
        var ingredients = new List<Ingredient>
        {
            new() { OrderIndex = 1, Name = "mjöl", GroupHeader = "Deg" },
            new() { OrderIndex = 2, Name = "äpple", GroupHeader = "Fyllning" }
        };

        var cut = RenderComponent<RecipeIngredientEditor>(parameters => parameters
            .Add(p => p.Ingredients, ingredients)
            .Add(p => p.Title, "Ingredienser"));

        // Add an empty section
        cut.Find(".ingredient-add-section-btn").Click();
        cut.FindAll(".ingredient-section-header-row").Should().HaveCount(3);
        cut.Markup.Should().Contain("Tom sektion (sparas inte)");

        // Trigger a parent re-render with the same Ingredients reference: empty section must NOT be lost
        cut.SetParametersAndRender(parameters => parameters
            .Add(p => p.Ingredients, ingredients)
            .Add(p => p.Title, "Ingredienser (uppdaterad)"));

        cut.FindAll(".ingredient-section-header-row").Should().HaveCount(3);

        // Open reorder menu on first section ("Deg") and press ArrowDown: moves header 1 row down (below "mjöl")
        var firstHandle = cut.FindAll(".ingredient-drag-handle").First();
        firstHandle.Click();
        cut.FindAll(".ingredient-reorder-bar").Should().ContainSingle();
        cut.FindAll(".ingredient-drag-handle").First().KeyDown(new KeyboardEventArgs { Key = "ArrowDown" });

        // "mjöl" is now before "Deg" (ungrouped), "äpple" remains under "Fyllning"
        ingredients.Select(i => (i.Name, i.GroupHeader, i.OrderIndex))
            .Should().Equal(
                ("mjöl", (string?)null, 1),
                ("äpple", "Fyllning", 2));
    }

    [Fact]
    public async Task RecipeDetail_And_CookingMode_ShouldRenderGroupHeadersIncludingRepeatedGroups()
    {
        using var dbFactory = new TestDbContextFactory();
        var imageStorage = new MockImageStorageService();
        var recipeService = new RecipeService(dbFactory, imageStorage, NullLogger<RecipeService>.Instance);
        var servingCalc = new ServingCalculator();
        var shoppingListService = new ShoppingListService(
            dbFactory,
            new ShoppingListAggregator(servingCalc),
            new SwedishIngredientParser());

        Services.AddSingleton(recipeService);
        Services.AddSingleton(shoppingListService);
        Services.AddSingleton(servingCalc);
        Services.AddScoped<AppNotificationService>();

        var recipe = await recipeService.CreateRecipeAsync(new Recipe
        {
            Title = "Sektionspaj",
            Servings = 4,
            ServingsUnit = "portioner",
            Ingredients =
            [
                new() { OrderIndex = 1, Name = "mjöl", Quantity = 3, Unit = "dl", GroupHeader = "Deg", RawText = "3 dl mjöl" },
                new() { OrderIndex = 2, Name = "salt", Quantity = 1, Unit = "tsk", GroupHeader = null, RawText = "1 tsk salt" },
                new() { OrderIndex = 3, Name = "smör", Quantity = 100, Unit = "g", GroupHeader = "Deg", RawText = "100 g smör" }
            ],
            Instructions = [new() { StepNumber = 1, InstructionText = "Blanda allt." }]
        });

        var detailCut = RenderComponent<RecipeDetail>(p => p.Add(x => x.Id, recipe.Id));
        detailCut.WaitForAssertion(() =>
        {
            var headers = detailCut.FindAll("li.ingredient-group-header").ToList();
            headers.Should().HaveCount(2);
            headers[0].TextContent.Trim().Should().Be("Deg");
            headers[1].TextContent.Trim().Should().Be("Deg");
            detailCut.FindAll("li.ingredient-group-break").Should().ContainSingle();
        });

        var cookingCut = RenderComponent<CookingMode>(p => p.Add(x => x.Id, recipe.Id));
        cookingCut.WaitForAssertion(() =>
        {
            cookingCut.Find("button:contains('Visa ingredienser')").Click();
            var headers = cookingCut.FindAll(".cooking-ingredients-drawer li.ingredient-group-header").ToList();
            headers.Should().HaveCount(2);
            headers[0].TextContent.Trim().Should().Be("Deg");
            headers[1].TextContent.Trim().Should().Be("Deg");
        });
    }
}
