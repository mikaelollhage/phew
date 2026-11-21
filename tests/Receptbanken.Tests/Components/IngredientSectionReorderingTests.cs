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

        // Open floating row menu on first section ("Deg") and click "Flytta ned": moves header 1 row down (below "mjöl")
        var firstMenuBtn = cut.FindAll(".ingredient-row-menu-btn").First();
        firstMenuBtn.Click();
        cut.FindAll(".ingredient-row-menu-popover").Should().ContainSingle();
        cut.Find(".ingredient-move-down-btn").Click();

        // Menu remains open on the moved row
        cut.FindAll(".ingredient-row-menu-popover").Should().ContainSingle();

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

    [Fact]
    public void IngredientSectionList_ConvertBetweenIngredientAndSection_PreservesPositionOrderAndRecalculatesGrouping()
    {
        var ingredients = new List<Ingredient>
        {
            new() { OrderIndex = 1, Name = "tomater", GroupHeader = "Tomatsallad", RawText = "4 tomater" },
            new() { OrderIndex = 2, Name = "Dressing", GroupHeader = "Tomatsallad", RawText = "Dressing" },
            new() { OrderIndex = 3, Name = "olivolja", GroupHeader = "Tomatsallad", RawText = "2 msk olivolja" },
            new() { OrderIndex = 4, Name = "vinäger", GroupHeader = "Tomatsallad", RawText = "1 msk vinäger" }
        };

        var list = new IngredientSectionList();
        list.LoadFromIngredients(ingredients);

        var dressingKey = list.Items.Single(i => i.IsIngredient && i.Ingredient!.Name == "Dressing").Key;
        var convertedSection = list.ConvertIngredientToSection(dressingKey, ingredients);

        convertedSection.Should().NotBeNull();
        convertedSection!.IsSection.Should().BeTrue();
        convertedSection.SectionTitle.Should().Be("Dressing");

        list.Items.Select(i => i.IsSection ? $"[{i.SectionTitle}]" : i.Ingredient!.Name)
            .Should().Equal("[Tomatsallad]", "tomater", "[Dressing]", "olivolja", "vinäger");

        ingredients.Select(i => (i.Name, i.GroupHeader, i.OrderIndex))
            .Should().Equal(
                ("tomater", "Tomatsallad", 1),
                ("olivolja", "Dressing", 2),
                ("vinäger", "Dressing", 3));

        // Convert the section back to an editable ingredient at the exact same position
        var convertedIngredient = list.ConvertSectionToIngredient(convertedSection.Key, ingredients);
        convertedIngredient.Should().NotBeNull();
        convertedIngredient!.IsIngredient.Should().BeTrue();
        convertedIngredient.Ingredient!.Name.Should().Be("Dressing");

        list.Items.Select(i => i.IsSection ? $"[{i.SectionTitle}]" : i.Ingredient!.Name)
            .Should().Equal("[Tomatsallad]", "tomater", "Dressing", "olivolja", "vinäger");

        ingredients.Select(i => (i.Name, i.GroupHeader, i.OrderIndex))
            .Should().Equal(
                ("tomater", "Tomatsallad", 1),
                ("Dressing", "Tomatsallad", 2),
                ("olivolja", "Tomatsallad", 3),
                ("vinäger", "Tomatsallad", 4));
    }

    [Fact]
    public void IngredientSectionList_RoundTripUnchangedTitle_RestoresAllIngredientFieldsAndGrouping()
    {
        var ingredients = new List<Ingredient>
        {
            new() { Id = 1, RecipeId = 9, OrderIndex = 1, Name = "mjöl", Quantity = 2, Unit = "dl", GroupHeader = "Deg", RawText = "2 dl mjöl" },
            new()
            {
                Id = 2,
                RecipeId = 9,
                OrderIndex = 2,
                Quantity = 1m,
                QuantityMax = 2m,
                Unit = "msk",
                Name = "salt",
                Comment = "flingsalt",
                Preparation = "mortlat",
                GroupHeader = "Deg",
                RawText = "1–2 msk salt (flingsalt), mortlat"
            },
            new() { Id = 3, RecipeId = 9, OrderIndex = 3, Name = "smör", Quantity = 100, Unit = "g", GroupHeader = "Deg", RawText = "100 g smör" }
        };

        var list = new IngredientSectionList();
        list.LoadFromIngredients(ingredients);

        var saltKey = list.Items.Single(i => i.IsIngredient && i.Ingredient!.Name == "salt").Key;
        var section = list.ConvertIngredientToSection(saltKey, ingredients);

        section.Should().NotBeNull();
        section!.IsSection.Should().BeTrue();
        section.SectionTitle.Should().Be("1–2 msk salt (flingsalt), mortlat");

        ingredients.Select(i => (i.Name, i.GroupHeader, i.OrderIndex))
            .Should().Equal(
                ("mjöl", "Deg", 1),
                ("smör", "1–2 msk salt (flingsalt), mortlat", 2));

        // Convert back without editing title -> exact original fields restored
        var restored = list.ConvertSectionToIngredient(section.Key, ingredients);
        restored.Should().NotBeNull();
        restored!.IsIngredient.Should().BeTrue();

        var ing = restored.Ingredient!;
        ing.Id.Should().Be(2);
        ing.RecipeId.Should().Be(9);
        ing.Quantity.Should().Be(1m);
        ing.QuantityMax.Should().Be(2m);
        ing.Unit.Should().Be("msk");
        ing.Name.Should().Be("salt");
        ing.Comment.Should().Be("flingsalt");
        ing.Preparation.Should().Be("mortlat");
        ing.RawText.Should().Be("1–2 msk salt (flingsalt), mortlat");
        ing.GroupHeader.Should().Be("Deg");
        ing.OrderIndex.Should().Be(2);

        ingredients.Select(i => (i.Name, i.GroupHeader, i.OrderIndex))
            .Should().Equal(
                ("mjöl", "Deg", 1),
                ("salt", "Deg", 2),
                ("smör", "Deg", 3));
    }

    [Fact]
    public void IngredientSectionList_ConvertEditedSectionTitleAndOriginalSection_ParsesOrPreservesNameAndGrouping()
    {
        var ingredients = new List<Ingredient>
        {
            new() { OrderIndex = 1, Quantity = 1, Unit = "msk", Name = "salt", RawText = "1 msk salt", GroupHeader = "Bas" },
            new() { OrderIndex = 2, Quantity = 2, Unit = "dl", Name = "vatten", RawText = "2 dl vatten", GroupHeader = "Bas" }
        };

        var list = new IngredientSectionList();
        list.LoadFromIngredients(ingredients);

        // 1. Ingredient -> Section, edit title to structured ingredient -> parses new title
        var saltKey = list.Items.Single(i => i.IsIngredient && i.Ingredient!.Name == "salt").Key;
        var section = list.ConvertIngredientToSection(saltKey, ingredients)!;
        list.RenameSection(section.Key, "2 tsk oregano", ingredients);

        var parsedItem = list.ConvertSectionToIngredient(section.Key, ingredients)!;
        parsedItem.Ingredient!.Quantity.Should().Be(2m);
        parsedItem.Ingredient.Unit.Should().Be("tsk");
        parsedItem.Ingredient.Name.Should().Be("oregano");
        parsedItem.Ingredient.RawText.Should().Be("2 tsk oregano");
        parsedItem.Ingredient.GroupHeader.Should().Be("Bas");
        parsedItem.Ingredient.OrderIndex.Should().Be(1);

        // 2. Ingredient -> Section, edit title to plain text -> preserves as editable name
        var section2 = list.ConvertIngredientToSection(parsedItem.Key, ingredients)!;
        list.RenameSection(section2.Key, "Kryddblandning", ingredients);

        var plainItem = list.ConvertSectionToIngredient(section2.Key, ingredients)!;
        plainItem.Ingredient!.Quantity.Should().BeNull();
        plainItem.Ingredient.Unit.Should().BeNull();
        plainItem.Ingredient.Name.Should().Be("Kryddblandning");
        plainItem.Ingredient.RawText.Should().Be("Kryddblandning");
        plainItem.Ingredient.GroupHeader.Should().Be("Bas");

        // 3. Original section with structured text -> parsed when converted to ingredient
        var originalSection = list.Items.First(i => i.IsSection);
        list.RenameSection(originalSection.Key, "3 dl grädde", ingredients);
        var fromOriginalSection = list.ConvertSectionToIngredient(originalSection.Key, ingredients)!;
        fromOriginalSection.Ingredient!.Quantity.Should().Be(3m);
        fromOriginalSection.Ingredient.Unit.Should().Be("dl");
        fromOriginalSection.Ingredient.Name.Should().Be("grädde");
        fromOriginalSection.Ingredient.GroupHeader.Should().BeNull();
        fromOriginalSection.Ingredient.OrderIndex.Should().Be(1);

        ingredients.Select(i => (i.Name, i.GroupHeader, i.OrderIndex))
            .Should().Equal(
                ("grädde", (string?)null, 1),
                ("Kryddblandning", (string?)null, 2),
                ("vatten", (string?)null, 3));
    }

    [Fact]
    public void RecipeIngredientEditor_FloatingMenu_ShouldConvertBothDirectionsInPlaceAndCloseMenu()
    {
        var ingredients = new List<Ingredient>
        {
            new() { OrderIndex = 1, Name = "salt", Quantity = 1, Unit = "msk", RawText = "1 msk salt" },
            new() { OrderIndex = 2, Name = "olivolja", Quantity = 2, Unit = "msk", RawText = "2 msk olivolja" }
        };

        var cut = RenderComponent<RecipeIngredientEditor>(parameters => parameters
            .Add(p => p.Ingredients, ingredients)
            .Add(p => p.Title, "Ingredienser"));

        // Conversion buttons and old reorder bar should not be permanently visible
        cut.FindAll(".ingredient-to-section-btn, .section-to-ingredient-btn, .ingredient-reorder-bar").Should().BeEmpty();

        // Ingredient row layout: [Drag handle] [Ingredient fields] [⋯ menu]
        cut.FindAll(".ingredient-remove-btn").Should().BeEmpty();
        var firstRowChildClasses = cut.FindAll(".ingredient-editor-item").First()
            .QuerySelectorAll(".ingredient-editor-row > *")
            .Select(el => el.ClassName ?? string.Empty)
            .ToList();
        firstRowChildClasses.Should().HaveCount(3);
        firstRowChildClasses[0].Should().Contain("ingredient-row-drag");
        firstRowChildClasses[1].Should().Contain("ingredient-editor-fields");
        firstRowChildClasses[2].Should().Contain("ingredient-row-actions");

        var firstRowDragHandle = cut.FindAll(".ingredient-editor-item").First().QuerySelector(".ingredient-row-drag .ingredient-drag-handle");
        firstRowDragHandle.Should().NotBeNull();

        var firstRowActionClasses = cut.FindAll(".ingredient-editor-item").First()
            .QuerySelectorAll(".ingredient-row-actions > *")
            .Select(el => el.ClassName ?? string.Empty)
            .ToList();
        firstRowActionClasses.Should().ContainSingle();
        firstRowActionClasses[0].Should().Contain("ingredient-row-menu-anchor");

        // Open menu on first ingredient and convert to section -> closes menu
        cut.FindAll(".ingredient-row-menu-btn").First().Click();
        var toSectionBtn = cut.Find(".ingredient-to-section-btn");
        toSectionBtn.TextContent.Trim().Should().Be("Gör till sektion");
        toSectionBtn.Click();

        cut.FindAll(".ingredient-row-menu-popover").Should().BeEmpty();
        cut.FindAll(".ingredient-section-header-row").Should().ContainSingle();
        ingredients.Select(i => (i.Name, i.GroupHeader, i.OrderIndex))
            .Should().Equal(("olivolja", "1 msk salt", 1));

        // Section row controls should also be in order: ⋯, ✕, drag handle
        var sectionActionClasses = cut.Find(".ingredient-section-header-row")
            .QuerySelectorAll(".ingredient-row-actions > *")
            .Select(el => el.ClassName ?? string.Empty)
            .ToList();
        sectionActionClasses.Should().HaveCount(3);
        sectionActionClasses[0].Should().Contain("ingredient-row-menu-anchor");
        sectionActionClasses[1].Should().Contain("ingredient-section-remove-btn");
        sectionActionClasses[2].Should().Contain("ingredient-drag-handle");

        // Open menu on section and convert back to ingredient -> lossless round-trip and closes menu
        cut.FindAll(".ingredient-row-menu-btn").First().Click();
        var toIngredientBtn = cut.Find(".section-to-ingredient-btn");
        toIngredientBtn.TextContent.Trim().Should().Be("Gör till ingrediens");
        toIngredientBtn.Click();

        cut.FindAll(".ingredient-row-menu-popover").Should().BeEmpty();
        cut.FindAll(".ingredient-section-header-row").Should().BeEmpty();
        ingredients.Select(i => (i.Name, i.Quantity, i.Unit, i.GroupHeader, i.OrderIndex))
            .Should().Equal(
                ("salt", 1m, "msk", (string?)null, 1),
                ("olivolja", 2m, "msk", (string?)null, 2));
    }

    [Fact]
    public void RecipeIngredientEditor_FloatingMenu_ShouldFollowSameRowOnRepeatedMovesAndCloseOnOutsideClickOrEscape()
    {
        var ingredients = new List<Ingredient>
        {
            new() { OrderIndex = 1, Name = "mjöl", GroupHeader = "Deg" },
            new() { OrderIndex = 2, Name = "smör", GroupHeader = "Deg" },
            new() { OrderIndex = 3, Name = "äpple", GroupHeader = "Fyllning" }
        };

        var cut = RenderComponent<RecipeIngredientEditor>(parameters => parameters
            .Add(p => p.Ingredients, ingredients)
            .Add(p => p.Title, "Ingredienser"));

        // Items order: [Deg](0), mjöl(1), smör(2), [Fyllning](3), äpple(4)
        var mjolRowId = cut.FindAll(".ingredient-editor-item").ElementAt(1).GetAttribute("data-editor-item-id");
        mjolRowId.Should().NotBeNullOrWhiteSpace();

        // Open menu on "mjöl" (index 1)
        cut.FindAll(".ingredient-row-menu-btn").ElementAt(1).Click();
        cut.FindAll(".ingredient-row-menu-popover").Should().ContainSingle();
        cut.Find(".ingredient-move-up-btn").HasAttribute("disabled").Should().BeFalse();
        cut.Find(".ingredient-move-down-btn").HasAttribute("disabled").Should().BeFalse();

        // Move "mjöl" up to first row (index 0, above [Deg]) -> menu stays open on "mjöl" and "Flytta upp" becomes disabled
        cut.Find(".ingredient-move-up-btn").Click();
        cut.FindAll(".ingredient-row-menu-popover").Should().ContainSingle();
        cut.Find(".ingredient-row-menu-popover").Closest(".ingredient-editor-item")!
            .GetAttribute("data-editor-item-id").Should().Be(mjolRowId);
        cut.Find(".ingredient-move-up-btn").HasAttribute("disabled").Should().BeTrue();
        cut.Find(".ingredient-move-down-btn").HasAttribute("disabled").Should().BeFalse();

        // Repeatedly move "mjöl" down 4 times across [Deg] and [Fyllning] to the last row
        for (var step = 0; step < 4; step++)
        {
            cut.Find(".ingredient-move-down-btn").Click();
            cut.FindAll(".ingredient-row-menu-popover").Should().ContainSingle();
            cut.Find(".ingredient-row-menu-popover").Closest(".ingredient-editor-item")!
                .GetAttribute("data-editor-item-id").Should().Be(mjolRowId);
        }

        // Now at last row: "Flytta ned" must be disabled and "mjöl" is under "Fyllning"
        cut.Find(".ingredient-move-down-btn").HasAttribute("disabled").Should().BeTrue();
        cut.Find(".ingredient-move-up-btn").HasAttribute("disabled").Should().BeFalse();
        ingredients.Select(i => (i.Name, i.GroupHeader, i.OrderIndex))
            .Should().Equal(
                ("smör", "Deg", 1),
                ("äpple", "Fyllning", 2),
                ("mjöl", "Fyllning", 3));

        // Outside click (backdrop) closes the menu
        cut.Find(".ingredient-row-menu-backdrop").Click();
        cut.FindAll(".ingredient-row-menu-popover").Should().BeEmpty();

        // Re-open menu and press Escape -> closes the menu
        cut.FindAll(".ingredient-row-menu-btn").Last().Click();
        cut.FindAll(".ingredient-row-menu-popover").Should().ContainSingle();
        cut.Find(".ingredient-row-menu-anchor.is-open").KeyDown(new KeyboardEventArgs { Key = "Escape" });
        cut.FindAll(".ingredient-row-menu-popover").Should().BeEmpty();
    }

    [Fact]
    public void RecipeIngredientEditor_AddButtons_ShouldRenderBelowList_AndFocusNewItemInputs()
    {
        var ingredients = new List<Ingredient>
        {
            new() { OrderIndex = 1, Name = "mjöl" }
        };

        var cut = RenderComponent<RecipeIngredientEditor>(parameters => parameters
            .Add(p => p.Ingredients, ingredients)
            .Add(p => p.Title, "Ingredienser"));

        // Actions must not be in header; they must appear after the ingredient list
        cut.Find(".ingredient-editor-header").QuerySelector(".ingredient-editor-actions").Should().BeNull();
        var listElement = cut.Find(".ingredient-editor-list");
        listElement.NextElementSibling.Should().NotBeNull();
        listElement.NextElementSibling!.ClassList.Should().Contain("ingredient-editor-actions");

        // Add ingredient -> focuses first input of newly appended row
        cut.Find(".ingredient-add-btn").Click();
        ingredients.Should().HaveCount(2);
        var addedIngredientId = cut.FindAll(".ingredient-editor-item").Last().GetAttribute("data-editor-item-id");
        JSInterop.VerifyInvoke("receptbanken.focusElement")
            .Arguments.Should().Contain($"ingredient-qty-input-{addedIngredientId}");

        // Add section -> focuses section title input
        cut.Find(".ingredient-add-section-btn").Click();
        var addedSectionId = cut.FindAll(".ingredient-editor-item").Last().GetAttribute("data-editor-item-id");
        JSInterop.VerifyInvoke("receptbanken.focusElement", 2)
            .Last().Arguments.Should().Contain($"ingredient-section-input-{addedSectionId}");
    }

    [Fact]
    public void RecipeIngredientEditor_DragAndDrop_ShouldReorderAcrossSections_AndPreserveIngredientValues()
    {
        var ingredients = new List<Ingredient>
        {
            new() { OrderIndex = 1, Quantity = 2.5m, Unit = "dl", Name = "mjöl", Comment = "ekologiskt", Preparation = "siktat", GroupHeader = "Deg" },
            new() { OrderIndex = 2, Quantity = 100m, Unit = "g", Name = "smör", Comment = "kallt", Preparation = "tärnat", GroupHeader = "Deg" },
            new() { OrderIndex = 3, Quantity = 3m, QuantityMax = 4m, Unit = "st", Name = "äpple", Comment = "svenska", Preparation = "skivade", GroupHeader = "Fyllning" }
        };

        var cut = RenderComponent<RecipeIngredientEditor>(parameters => parameters
            .Add(p => p.Ingredients, ingredients)
            .Add(p => p.Title, "Ingredienser"));

        // Only the drag handle is draggable, never the row itself
        foreach (var item in cut.FindAll(".ingredient-editor-item"))
        {
            item.HasAttribute("draggable").Should().BeFalse();
        }
        foreach (var row in cut.FindAll(".ingredient-editor-row"))
        {
            row.HasAttribute("draggable").Should().BeFalse();
        }
        foreach (var handle in cut.FindAll(".ingredient-drag-handle"))
        {
            handle.GetAttribute("draggable").Should().Be("true");
        }

        // Items: [Deg](0), mjöl(1), smör(2), [Fyllning](3), äpple(4)
        cut.FindAll(".ingredient-editor-item").ElementAt(1).QuerySelector(".ingredient-drag-handle")!.DragStart();
        cut.FindAll(".ingredient-editor-item").ElementAt(4).DragOver();
        cut.FindAll(".ingredient-editor-item").ElementAt(4).Drop();
        cut.FindAll(".ingredient-editor-item").ElementAt(4).QuerySelector(".ingredient-drag-handle")!.DragEnd();

        ingredients.Select(i => (i.Name, i.Quantity, i.QuantityMax, i.Unit, i.Comment, i.Preparation, i.GroupHeader, i.OrderIndex))
            .Should().Equal(
                ("smör", 100m, (decimal?)null, "g", "kallt", "tärnat", "Deg", 1),
                ("äpple", 3m, 4m, "st", "svenska", "skivade", "Fyllning", 2),
                ("mjöl", 2.5m, (decimal?)null, "dl", "ekologiskt", "siktat", "Fyllning", 3));
    }

    [Fact]
    public void RecipeIngredientEditor_MenuDelete_ShouldRemoveIngredient_CloseMenu_AndInvokeCallback()
    {
        var ingredients = new List<Ingredient>
        {
            new() { OrderIndex = 1, Quantity = 2m, Unit = "dl", Name = "grädde" },
            new() { OrderIndex = 2, Quantity = 1m, Unit = "tsk", Name = "salt" },
            new() { OrderIndex = 3, Quantity = 3m, Unit = "msk", Name = "smör" }
        };
        int? removedIndex = null;

        var cut = RenderComponent<RecipeIngredientEditor>(parameters => parameters
            .Add(p => p.Ingredients, ingredients)
            .Add(p => p.Title, "Ingredienser")
            .Add(p => p.OnIngredientRemoved, idx => { removedIndex = idx; }));

        // Open menu on "salt" (index 1)
        cut.FindAll(".ingredient-row-menu-btn").ElementAt(1).Click();
        var popover = cut.Find(".ingredient-row-menu-popover");

        // Delete action is at the bottom, separated by a divider, and styled as destructive
        var menuChildren = popover.Children.ToList();
        menuChildren.Last().ClassList.Should().Contain("ingredient-menu-remove-btn");
        menuChildren.Last().ClassList.Should().Contain("is-danger");
        menuChildren[^2].GetAttribute("role").Should().Be("separator");
        menuChildren.Last().TextContent.Trim().Should().Be("Ta bort ingrediens");
        menuChildren.Last().QuerySelector("svg").Should().NotBeNull();

        // Click delete -> removes "salt", closes menu, invokes OnIngredientRemoved(1)
        menuChildren.Last().Click();

        cut.FindAll(".ingredient-row-menu-popover").Should().BeEmpty();
        removedIndex.Should().Be(1);
        ingredients.Select(i => (i.Name, i.OrderIndex))
            .Should().Equal(("grädde", 1), ("smör", 2));
    }

    [Fact]
    public async Task RecipeIngredientEditor_SwitchingMenus_Backdrop_AndJsEscape_ShouldBehavePredictably()
    {
        var ingredients = new List<Ingredient>
        {
            new() { OrderIndex = 1, Name = "mjöl" },
            new() { OrderIndex = 2, Name = "socker" }
        };

        var cut = RenderComponent<RecipeIngredientEditor>(parameters => parameters
            .Add(p => p.Ingredients, ingredients)
            .Add(p => p.Title, "Ingredienser"));

        var items = cut.FindAll(".ingredient-editor-item");
        var firstKey = items.ElementAt(0).GetAttribute("data-editor-item-id");
        var secondKey = items.ElementAt(1).GetAttribute("data-editor-item-id");

        // Open first menu
        cut.FindAll(".ingredient-row-menu-btn").ElementAt(0).Click();
        cut.FindAll(".ingredient-row-menu-popover").Should().ContainSingle();
        cut.Find(".ingredient-row-menu-popover").Closest(".ingredient-editor-item")!
            .GetAttribute("data-editor-item-id").Should().Be(firstKey);

        // Switch directly to second menu
        cut.FindAll(".ingredient-row-menu-btn").ElementAt(1).Click();
        cut.FindAll(".ingredient-row-menu-popover").Should().ContainSingle();
        cut.Find(".ingredient-row-menu-popover").Closest(".ingredient-editor-item")!
            .GetAttribute("data-editor-item-id").Should().Be(secondKey);

        // Backdrop closes menu
        cut.Find(".ingredient-row-menu-backdrop").Click();
        cut.FindAll(".ingredient-row-menu-popover").Should().BeEmpty();
        cut.FindAll(".ingredient-row-menu-backdrop").Should().BeEmpty();

        // Re-open and close via JSInvokable CloseRowMenuAsync (global Escape)
        cut.FindAll(".ingredient-row-menu-btn").ElementAt(0).Click();
        cut.FindAll(".ingredient-row-menu-popover").Should().ContainSingle();
        await cut.InvokeAsync(() => cut.Instance.CloseRowMenuAsync());
        cut.FindAll(".ingredient-row-menu-popover").Should().BeEmpty();
    }

    [Fact]
    public void RecipeIngredientEditor_AddAndConvertMutations_ShouldCloseOpenMenu()
    {
        var ingredients = new List<Ingredient>
        {
            new() { OrderIndex = 1, Name = "Dressing" },
            new() { OrderIndex = 2, Name = "olivolja" }
        };

        var cut = RenderComponent<RecipeIngredientEditor>(parameters => parameters
            .Add(p => p.Ingredients, ingredients)
            .Add(p => p.Title, "Ingredienser"));

        // AddIngredient closes open menu
        cut.FindAll(".ingredient-row-menu-btn").ElementAt(0).Click();
        cut.FindAll(".ingredient-row-menu-popover").Should().ContainSingle();
        cut.Find(".ingredient-add-btn").Click();
        cut.FindAll(".ingredient-row-menu-popover").Should().BeEmpty();
        cut.FindAll(".ingredient-row-menu-backdrop").Should().BeEmpty();

        // AddSection closes open menu
        cut.FindAll(".ingredient-row-menu-btn").ElementAt(0).Click();
        cut.FindAll(".ingredient-row-menu-popover").Should().ContainSingle();
        cut.Find(".ingredient-add-section-btn").Click();
        cut.FindAll(".ingredient-row-menu-popover").Should().BeEmpty();
        cut.FindAll(".ingredient-row-menu-backdrop").Should().BeEmpty();

        // ConvertIngredientToSection closes open menu
        cut.FindAll(".ingredient-row-menu-btn").ElementAt(0).Click();
        cut.Find(".ingredient-to-section-btn").Click();
        cut.FindAll(".ingredient-row-menu-popover").Should().BeEmpty();
        cut.FindAll(".ingredient-row-menu-backdrop").Should().BeEmpty();

        // ConvertSectionToIngredient closes open menu
        cut.FindAll(".ingredient-row-menu-btn").ElementAt(0).Click();
        cut.Find(".section-to-ingredient-btn").Click();
        cut.FindAll(".ingredient-row-menu-popover").Should().BeEmpty();
        cut.FindAll(".ingredient-row-menu-backdrop").Should().BeEmpty();
    }

    [Fact]
    public void RecipeIngredientEditor_RepeatedMoveUpAndDown_ShouldKeepMenuOpenOnSameLogicalItem()
    {
        var ingredients = new List<Ingredient>
        {
            new() { OrderIndex = 1, Name = "ägg" },
            new() { OrderIndex = 2, Name = "mjöl" },
            new() { OrderIndex = 3, Name = "mjölk" }
        };

        var cut = RenderComponent<RecipeIngredientEditor>(parameters => parameters
            .Add(p => p.Ingredients, ingredients)
            .Add(p => p.Title, "Ingredienser"));

        var targetKey = cut.FindAll(".ingredient-editor-item").ElementAt(1).GetAttribute("data-editor-item-id");

        // Open menu on "mjöl" (index 1)
        cut.FindAll(".ingredient-row-menu-btn").ElementAt(1).Click();
        cut.Find(".ingredient-move-up-btn").Click();
        cut.Find(".ingredient-row-menu-popover").Closest(".ingredient-editor-item")!
            .GetAttribute("data-editor-item-id").Should().Be(targetKey);
        cut.Find(".ingredient-move-up-btn").HasAttribute("disabled").Should().BeTrue();

        // Move down twice
        cut.Find(".ingredient-move-down-btn").Click();
        cut.Find(".ingredient-move-down-btn").Click();
        cut.Find(".ingredient-row-menu-popover").Closest(".ingredient-editor-item")!
            .GetAttribute("data-editor-item-id").Should().Be(targetKey);
        cut.Find(".ingredient-move-down-btn").HasAttribute("disabled").Should().BeTrue();

        ingredients.Select(i => i.Name).Should().Equal("ägg", "mjölk", "mjöl");
    }

    [Fact]
    public void RecipeIngredientRow_QuantityInputDeferredAndPartialSafe_WhileTextInputsAreImmediate()
    {
        var ingredient = new Ingredient
        {
            Quantity = 1m,
            Unit = "dl",
            Name = "mjöl"
        };

        var cut = RenderComponent<RecipeIngredientRow>(parameters => parameters
            .Add(p => p.Ingredient, ingredient));

        var fields = cut.FindAll("fluent-text-field");
        var qtyField = fields.ElementAt(0);
        var nameField = fields.ElementAt(2);

        // Partial input on quantity does not prematurely overwrite model on oninput
        qtyField.Input("0,");
        ingredient.Quantity.Should().Be(1m);

        qtyField.Input("1-");
        ingredient.Quantity.Should().Be(1m);
        ingredient.QuantityMax.Should().BeNull();

        // Committing formatted decimal and range works as expected
        qtyField.Change("0,5");
        ingredient.Quantity.Should().Be(0.5m);
        ingredient.QuantityMax.Should().BeNull();

        qtyField.Change("1–2");
        ingredient.Quantity.Should().Be(1m);
        ingredient.QuantityMax.Should().Be(2m);

        // Text field (Name) updates immediately (after debounce) on Input without requiring blur
        nameField.Input("dinkelmjöl");
        cut.WaitForAssertion(() => ingredient.Name.Should().Be("dinkelmjöl"));
    }
}
