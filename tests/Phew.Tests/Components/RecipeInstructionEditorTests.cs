using Bunit;
using FluentAssertions;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.FluentUI.AspNetCore.Components;
using Phew.Core.Models;
using Phew.Infrastructure.Services;
using Phew.Tests.Infrastructure;
using Phew.Web.Components.Recipes;

namespace Phew.Tests.Components;

public class RecipeInstructionEditorTests : TestContext
{
    public RecipeInstructionEditorTests()
    {
        Services.AddFluentUIComponents();
        JSInterop.Mode = JSRuntimeMode.Loose;
    }

    [Fact]
    public void InsertAboveAndBelow_ShouldInsertEmptyStepAtExpectedPosition_Renumber_AndRequestFocus()
    {
        var instructions = new List<InstructionStep>
        {
            new() { StepNumber = 1, InstructionText = "Första steget" },
            new() { StepNumber = 2, InstructionText = "Andra steget" }
        };

        var cut = RenderComponent<RecipeInstructionEditor>(parameters => parameters
            .Add(p => p.Instructions, instructions));

        // Open menu on step 2 (index 1) and insert above
        cut.FindAll(".instruction-row-menu-btn").ElementAt(1).Click();
        cut.Find(".instruction-insert-above-btn").Click();

        instructions.Should().HaveCount(3);
        instructions.Select(s => (s.StepNumber, s.InstructionText)).Should().Equal(
            (1, "Första steget"),
            (2, string.Empty),
            (3, "Andra steget"));
        cut.FindAll(".step-number").Select(e => e.TextContent.Trim()).Should().Equal("1", "2", "3");
        cut.FindAll(".instruction-row-menu-popover").Should().BeEmpty();
        JSInterop.VerifyInvoke("phew.focusElement");

        // Open menu on step 1 (index 0) and insert below
        cut.FindAll(".instruction-row-menu-btn").ElementAt(0).Click();
        cut.Find(".instruction-insert-below-btn").Click();

        instructions.Should().HaveCount(4);
        instructions.Select(s => (s.StepNumber, s.InstructionText)).Should().Equal(
            (1, "Första steget"),
            (2, string.Empty),
            (3, string.Empty),
            (4, "Andra steget"));
        cut.FindAll(".step-number").Select(e => e.TextContent.Trim()).Should().Equal("1", "2", "3", "4");
    }

    [Fact]
    public void MoveStepsUpAndDown_ShouldKeepMenuOpenForRepeatedMoves_UpdateDisabledStates_AndRenumber()
    {
        var step1 = new InstructionStep { StepNumber = 1, InstructionText = "Hacka lök", GroupHeader = "Förberedelse" };
        var step2 = new InstructionStep { StepNumber = 2, InstructionText = "Stek lök" };
        var step3 = new InstructionStep { StepNumber = 3, InstructionText = "Servera" };
        var instructions = new List<InstructionStep> { step1, step2, step3 };

        var cut = RenderComponent<RecipeInstructionEditor>(parameters => parameters
            .Add(p => p.Instructions, instructions));

        // Open menu on step 2 ("Stek lök")
        cut.FindAll(".instruction-row-menu-btn").ElementAt(1).Click();
        cut.FindAll(".instruction-row-menu-popover").Should().ContainSingle();
        cut.Find(".instruction-move-up-btn").HasAttribute("disabled").Should().BeFalse();
        cut.Find(".instruction-move-down-btn").HasAttribute("disabled").Should().BeFalse();

        // Move up to index 0 -> menu stays open on "Stek lök", MoveUp becomes disabled
        cut.Find(".instruction-move-up-btn").Click();
        cut.FindAll(".instruction-row-menu-popover").Should().ContainSingle();
        cut.Find(".instruction-row-menu-popover").Closest(".instruction-editor-row")!
            .GetAttribute("data-step-number").Should().Be("1");
        cut.Find(".instruction-move-up-btn").HasAttribute("disabled").Should().BeTrue();
        cut.Find(".instruction-move-down-btn").HasAttribute("disabled").Should().BeFalse();

        // Repeatedly move down twice to index 2 -> menu stays open on "Stek lök", MoveDown becomes disabled
        cut.Find(".instruction-move-down-btn").Click();
        cut.Find(".instruction-row-menu-popover").Closest(".instruction-editor-row")!
            .GetAttribute("data-step-number").Should().Be("2");

        cut.Find(".instruction-move-down-btn").Click();
        cut.Find(".instruction-row-menu-popover").Closest(".instruction-editor-row")!
            .GetAttribute("data-step-number").Should().Be("3");
        cut.Find(".instruction-move-down-btn").HasAttribute("disabled").Should().BeTrue();
        cut.Find(".instruction-move-up-btn").HasAttribute("disabled").Should().BeFalse();

        instructions.Select(s => (s.StepNumber, s.InstructionText, s.GroupHeader)).Should().Equal(
            (1, "Hacka lök", "Förberedelse"),
            (2, "Servera", (string?)null),
            (3, "Stek lök", (string?)null));
    }

    [Fact]
    public void DeleteStep_ShouldRemoveOnlyTargetStep_PreserveOthers_AndRenumber()
    {
        var instructions = new List<InstructionStep>
        {
            new() { StepNumber = 1, InstructionText = "Steg A", GroupHeader = "Del 1" },
            new() { StepNumber = 2, InstructionText = "Steg B" },
            new() { StepNumber = 3, InstructionText = "Steg C\nMed ny rad", GroupHeader = "Del 2" }
        };

        var cut = RenderComponent<RecipeInstructionEditor>(parameters => parameters
            .Add(p => p.Instructions, instructions));

        cut.FindAll(".instruction-row-menu-btn").ElementAt(1).Click();
        cut.Find(".instruction-remove-btn").Click();

        instructions.Should().HaveCount(2);
        instructions[0].InstructionText.Should().Be("Steg A");
        instructions[0].GroupHeader.Should().Be("Del 1");
        instructions[0].StepNumber.Should().Be(1);
        instructions[1].InstructionText.Should().Be("Steg C\nMed ny rad");
        instructions[1].GroupHeader.Should().Be("Del 2");
        instructions[1].StepNumber.Should().Be(2);
        cut.FindAll(".step-number").Select(e => e.TextContent.Trim()).Should().Equal("1", "2");
        cut.FindAll(".instruction-row-menu-popover").Should().BeEmpty();
    }

    [Fact]
    public void CompactAddRow_ShouldDisableWhenBlank_SupportClickAndEnter_AndHaveAccessibleLabel()
    {
        var instructions = new List<InstructionStep>();
        var cut = RenderComponent<RecipeInstructionEditor>(parameters => parameters
            .Add(p => p.Instructions, instructions));

        var addButton = cut.Find(".instruction-add-btn");
        addButton.GetAttribute("aria-label").Should().Be("Lägg till steg");
        addButton.HasAttribute("disabled").Should().BeTrue();

        // Whitespace-only keeps button disabled
        cut.Find(".instruction-add-input").Input("   ");
        cut.Find(".instruction-add-btn").HasAttribute("disabled").Should().BeTrue();

        // Enter key on blank input does nothing
        cut.Find(".instruction-add-input").KeyDown(new KeyboardEventArgs { Key = "Enter" });
        instructions.Should().BeEmpty();

        // Valid input enables button and adds on click
        cut.Find(".instruction-add-input").Input("Koka pastan i saltat vatten");
        cut.Find(".instruction-add-btn").HasAttribute("disabled").Should().BeFalse();
        cut.Find(".instruction-add-btn").Click();

        instructions.Should().ContainSingle();
        instructions[0].StepNumber.Should().Be(1);
        instructions[0].InstructionText.Should().Be("Koka pastan i saltat vatten");
        cut.Find(".instruction-add-btn").HasAttribute("disabled").Should().BeTrue();

        // Enter key submits second step
        cut.Find(".instruction-add-input").Input("Häll av vattnet");
        cut.Find(".instruction-add-input").KeyDown(new KeyboardEventArgs { Key = "Enter" });

        instructions.Should().HaveCount(2);
        instructions[1].StepNumber.Should().Be(2);
        instructions[1].InstructionText.Should().Be("Häll av vattnet");
    }

    [Fact]
    public void Menu_ShouldCloseOnBackdropClickAndEscapeKey()
    {
        var instructions = new List<InstructionStep>
        {
            new() { StepNumber = 1, InstructionText = "Steg 1" },
            new() { StepNumber = 2, InstructionText = "Steg 2" }
        };

        var cut = RenderComponent<RecipeInstructionEditor>(parameters => parameters
            .Add(p => p.Instructions, instructions));

        cut.FindAll(".instruction-row-menu-btn").ElementAt(0).Click();
        cut.FindAll(".instruction-row-menu-popover").Should().ContainSingle();

        cut.Find(".instruction-row-menu-backdrop").Click();
        cut.FindAll(".instruction-row-menu-popover").Should().BeEmpty();

        cut.FindAll(".instruction-row-menu-btn").ElementAt(1).Click();
        cut.FindAll(".instruction-row-menu-popover").Should().ContainSingle();

        cut.Find(".instruction-row-menu-anchor.is-open").KeyDown(new KeyboardEventArgs { Key = "Escape" });
        cut.FindAll(".instruction-row-menu-popover").Should().BeEmpty();
    }

    [Fact]
    public async Task PreservesLegacyMultilineFormatting_AndSavesReloadsCorrectOrder()
    {
        using var dbFactory = new TestDbContextFactory();
        var service = new RecipeService(dbFactory, new MockImageStorageService(), NullLogger<RecipeService>.Instance);

        var legacyText = "Rad 1\n\n  Rad 2 med indrag\n• Punktlista";
        var created = await service.CreateRecipeAsync(new Recipe
        {
            Title = "Legacy-recept",
            Servings = 4,
            Instructions = new List<InstructionStep>
            {
                new() { StepNumber = 1, InstructionText = legacyText, GroupHeader = "Sås" },
                new() { StepNumber = 2, InstructionText = "Steg två" }
            }
        });

        var loaded = await service.GetRecipeByIdAsync(created.Id);
        loaded.Should().NotBeNull();

        var cut = RenderComponent<RecipeInstructionEditor>(parameters => parameters
            .Add(p => p.Instructions, loaded!.Instructions));

        // Move legacy step down and insert a new step above index 0
        cut.FindAll(".instruction-row-menu-btn").ElementAt(0).Click();
        cut.Find(".instruction-move-down-btn").Click();
        cut.Find(".instruction-row-menu-backdrop").Click();

        cut.FindAll(".instruction-row-menu-btn").ElementAt(0).Click();
        cut.Find(".instruction-insert-above-btn").Click();
        loaded!.Instructions[0].InstructionText = "Förbered allt";

        await service.UpdateRecipeAsync(loaded);

        var reloaded = await service.GetRecipeByIdAsync(created.Id);
        reloaded.Should().NotBeNull();
        reloaded!.Instructions.Should().HaveCount(3);
        reloaded.Instructions[0].StepNumber.Should().Be(1);
        reloaded.Instructions[0].InstructionText.Should().Be("Förbered allt");
        reloaded.Instructions[1].StepNumber.Should().Be(2);
        reloaded.Instructions[1].InstructionText.Should().Be("Steg två");
        reloaded.Instructions[2].StepNumber.Should().Be(3);
        reloaded.Instructions[2].InstructionText.Should().Be(legacyText);
        reloaded.Instructions[2].GroupHeader.Should().Be("Sås");
    }

    [Fact]
    public async Task SwitchingMenus_ShouldClosePreviousAndOpenTarget_AndCloseViaJsEscape()
    {
        var instructions = new List<InstructionStep>
        {
            new() { StepNumber = 1, InstructionText = "Steg 1" },
            new() { StepNumber = 2, InstructionText = "Steg 2" }
        };

        var cut = RenderComponent<RecipeInstructionEditor>(parameters => parameters
            .Add(p => p.Instructions, instructions));

        // Open step 1 menu
        cut.FindAll(".instruction-row-menu-btn").ElementAt(0).Click();
        cut.FindAll(".instruction-row-menu-popover").Should().ContainSingle();
        cut.Find(".instruction-row-menu-popover").Closest(".instruction-editor-row")!
            .GetAttribute("data-step-number").Should().Be("1");

        // Switch directly to step 2 menu
        cut.FindAll(".instruction-row-menu-btn").ElementAt(1).Click();
        cut.FindAll(".instruction-row-menu-popover").Should().ContainSingle();
        cut.Find(".instruction-row-menu-popover").Closest(".instruction-editor-row")!
            .GetAttribute("data-step-number").Should().Be("2");

        // Close via JSInvokable CloseRowMenuAsync (global Escape)
        await cut.InvokeAsync(() => cut.Instance.CloseRowMenuAsync());
        cut.FindAll(".instruction-row-menu-popover").Should().BeEmpty();
        cut.FindAll(".instruction-row-menu-backdrop").Should().BeEmpty();
    }

    [Fact]
    public void RecipeInstructionRow_ImmediateInput_ShouldUpdateModelWithoutBlur_AndPreserveMultilineText()
    {
        var step = new InstructionStep
        {
            StepNumber = 1,
            GroupHeader = "Del 1",
            InstructionText = "Ursprunglig text"
        };

        var cut = RenderComponent<RecipeInstructionRow>(parameters => parameters
            .Add(p => p.Step, step)
            .Add(p => p.StepNumber, 1));

        cut.Find("fluent-text-field").Input("Sås");
        cut.WaitForAssertion(() => step.GroupHeader.Should().Be("Sås"));

        var multiline = "Rad 1\nRad 2\n  Indrag";
        cut.Find("fluent-text-area").Input(multiline);
        cut.WaitForAssertion(() => step.InstructionText.Should().Be(multiline));
    }
}
