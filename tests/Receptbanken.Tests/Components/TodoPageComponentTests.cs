using Bunit;
using FluentAssertions;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.FluentUI.AspNetCore.Components;
using Receptbanken.Core.Services;
using Receptbanken.Infrastructure.Services;
using Receptbanken.Tests.Infrastructure;
using Receptbanken.Web.Components.Pages;

namespace Receptbanken.Tests.Components;

public class TodoPageComponentTests : TestContext
{
    private readonly TestDbContextFactory _dbFactory;
    private readonly TodoNotifier _notifier;
    private readonly TodoService _service;

    public TodoPageComponentTests()
    {
        _dbFactory = new TestDbContextFactory();
        _notifier = new TodoNotifier();
        _service = new TodoService(_dbFactory, _notifier);

        Services.AddFluentUIComponents();
        Services.AddSingleton(_notifier);
        Services.AddSingleton(_service);
        JSInterop.Mode = JSRuntimeMode.Loose;
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing)
        {
            _dbFactory.Dispose();
        }
    }

    [Fact]
    public async Task TaskFloatingMenu_ShouldFollowTaskDuringRepeatedMoves_AndCloseOnEscapeOrOutsideClick()
    {
        var list = await _service.CreateListAsync("Helgfix");
        var t1 = await _service.AddItemAsync(list.Id, "Första");
        await _service.AddItemAsync(list.Id, "Andra");
        await _service.AddItemAsync(list.Id, "Tredje");

        var cut = RenderComponent<TodoPage>();
        cut.WaitForAssertion(() => cut.FindAll(".todo-task-row").Should().HaveCount(3));

        // Open menu on first task ("Första")
        cut.Find($"[data-todo-item-id='{t1.Id}'] .todo-task-menu-btn").Click();

        // At top: "Flytta upp" disabled, "Flytta ned" enabled
        cut.Find(".todo-move-up-btn").HasAttribute("disabled").Should().BeTrue();
        cut.Find(".todo-move-down-btn").HasAttribute("disabled").Should().BeFalse();

        // Move down once -> menu stays open on "Första", now both enabled
        cut.Find(".todo-move-down-btn").Click();
        cut.WaitForAssertion(() =>
        {
            var titles = cut.FindAll(".todo-task-title").Select(e => e.TextContent).ToList();
            titles.Should().Equal("Andra", "Första", "Tredje");
            cut.Find($"[data-todo-item-id='{t1.Id}'] .ingredient-row-menu-popover").Should().NotBeNull();
            cut.Find(".todo-move-up-btn").HasAttribute("disabled").Should().BeFalse();
            cut.Find(".todo-move-down-btn").HasAttribute("disabled").Should().BeFalse();
        });

        // Move down again -> menu stays open on "Första", now "Flytta ned" disabled
        cut.Find(".todo-move-down-btn").Click();
        cut.WaitForAssertion(() =>
        {
            var titles = cut.FindAll(".todo-task-title").Select(e => e.TextContent).ToList();
            titles.Should().Equal("Andra", "Tredje", "Första");
            cut.Find(".todo-move-up-btn").HasAttribute("disabled").Should().BeFalse();
            cut.Find(".todo-move-down-btn").HasAttribute("disabled").Should().BeTrue();
        });

        // Close on Escape
        cut.Find($"[data-todo-item-id='{t1.Id}'] .ingredient-row-menu-anchor")
            .KeyDown(new KeyboardEventArgs { Key = "Escape" });
        cut.FindAll(".ingredient-row-menu-popover").Should().BeEmpty();

        // Re-open and close on outside click backdrop
        cut.Find($"[data-todo-item-id='{t1.Id}'] .todo-task-menu-btn").Click();
        cut.FindAll(".ingredient-row-menu-popover").Should().ContainSingle();
        cut.Find(".ingredient-row-menu-backdrop").Click();
        cut.FindAll(".ingredient-row-menu-popover").Should().BeEmpty();
    }

    [Fact]
    public async Task ArchiveAndRestore_ShouldEnforceReadOnlyAndRefreshOnCrossClientNotification()
    {
        var list = await _service.CreateListAsync("Respackning");
        await _service.AddItemAsync(list.Id, "Pass");

        var cut = RenderComponent<TodoPage>();
        cut.WaitForAssertion(() => cut.Find(".todo-active-list-title").TextContent.Should().Be("Respackning"));

        // Archive via list tab floating menu
        cut.Find($"[data-todo-list-id='{list.Id}'] .todo-list-menu-btn").Click();
        cut.Find($"[data-todo-list-id='{list.Id}'] .todo-list-archive-btn").Click();
        cut.WaitForAssertion(() => cut.Find("#todo-toggle-archived-btn").TextContent.Should().Contain("1"));

        // Open archived drawer and select archived list
        cut.Find("#todo-toggle-archived-btn").Click();
        cut.Find(".todo-archived-drawer .todo-list-pill").Click();

        cut.WaitForAssertion(() =>
        {
            cut.Find(".todo-archived-banner").TextContent.Should().Contain("skrivskyddad");
            cut.FindAll("#todo-quick-add-input").Should().BeEmpty();
            cut.Find(".todo-check-btn").HasAttribute("disabled").Should().BeTrue();
        });

        // Restore via UI
        cut.Find("#todo-restore-list-btn").Click();
        cut.WaitForAssertion(() =>
        {
            cut.FindAll(".todo-archived-banner").Should().BeEmpty();
            cut.Find("#todo-quick-add-input").Should().NotBeNull();
        });

        // Cross-client update from another TodoService sharing the same TodoNotifier
        var otherClientService = new TodoService(_dbFactory, _notifier);
        await otherClientService.AddItemAsync(list.Id, "Tandborste");

        cut.WaitForAssertion(() =>
        {
            var titles = cut.FindAll(".todo-task-title").Select(e => e.TextContent).ToList();
            titles.Should().Contain("Tandborste");
        });
    }

    [Fact]
    public async Task ListTabMenu_ShouldFollowListDuringRepeatedMoves_NotSelectTab_AndCloseOnEscapeOrBackdrop()
    {
        var l1 = await _service.CreateListAsync("Lista A");
        var l2 = await _service.CreateListAsync("Lista B");
        var l3 = await _service.CreateListAsync("Lista C");

        var cut = RenderComponent<TodoPage>();
        cut.WaitForAssertion(() =>
        {
            cut.FindAll(".todo-list-tabs .todo-list-pill").Should().HaveCount(3);
            cut.Find(".todo-active-list-title").TextContent.Should().Be("Lista A");
        });

        // Header should not contain old list management buttons
        cut.FindAll(".todo-list-controls").Should().BeEmpty();
        cut.FindAll("#todo-move-list-prev-btn").Should().BeEmpty();
        cut.FindAll("#todo-move-list-next-btn").Should().BeEmpty();
        cut.FindAll("#todo-rename-list-btn").Should().BeEmpty();
        cut.FindAll("#todo-archive-list-btn").Should().BeEmpty();

        // Open menu on second tab ("Lista B") while "Lista A" is selected -> should not change selection
        cut.Find($"[data-todo-list-id='{l2.Id}'] .todo-list-menu-btn").Click();
        cut.Find(".todo-active-list-title").TextContent.Should().Be("Lista A");
        cut.Find($"[data-todo-list-id='{l2.Id}'] .todo-list-menu-popover").Should().NotBeNull();
        cut.Find(".todo-list-move-left-btn").HasAttribute("disabled").Should().BeFalse();
        cut.Find(".todo-list-move-right-btn").HasAttribute("disabled").Should().BeFalse();

        // Move "Lista B" left -> becomes first, menu stays open on "Lista B", move-left now disabled
        cut.Find(".todo-list-move-left-btn").Click();
        cut.WaitForAssertion(() =>
        {
            var names = cut.FindAll(".todo-list-tabs .todo-list-pill-name").Select(e => e.TextContent).ToList();
            names.Should().Equal("Lista B", "Lista A", "Lista C");
            cut.Find($"[data-todo-list-id='{l2.Id}'] .todo-list-menu-popover").Should().NotBeNull();
            cut.Find(".todo-list-move-left-btn").HasAttribute("disabled").Should().BeTrue();
            cut.Find(".todo-list-move-right-btn").HasAttribute("disabled").Should().BeFalse();
        });

        // Move "Lista B" right twice -> becomes last, menu stays open on "Lista B", move-right now disabled
        cut.Find(".todo-list-move-right-btn").Click();
        cut.WaitForAssertion(() =>
        {
            var names = cut.FindAll(".todo-list-tabs .todo-list-pill-name").Select(e => e.TextContent).ToList();
            names.Should().Equal("Lista A", "Lista B", "Lista C");
            cut.Find(".todo-list-move-right-btn").HasAttribute("disabled").Should().BeFalse();
        });

        cut.Find(".todo-list-move-right-btn").Click();
        cut.WaitForAssertion(() =>
        {
            var names = cut.FindAll(".todo-list-tabs .todo-list-pill-name").Select(e => e.TextContent).ToList();
            names.Should().Equal("Lista A", "Lista C", "Lista B");
            cut.Find($"[data-todo-list-id='{l2.Id}'] .todo-list-menu-popover").Should().NotBeNull();
            cut.Find(".todo-list-move-left-btn").HasAttribute("disabled").Should().BeFalse();
            cut.Find(".todo-list-move-right-btn").HasAttribute("disabled").Should().BeTrue();
        });

        // Selection should still be "Lista A"
        cut.Find(".todo-active-list-title").TextContent.Should().Be("Lista A");

        // Close on Escape
        cut.Find($"[data-todo-list-id='{l2.Id}'] .todo-list-menu-anchor")
            .KeyDown(new KeyboardEventArgs { Key = "Escape" });
        cut.FindAll(".todo-list-menu-popover").Should().BeEmpty();

        // Re-open and close on outside click backdrop
        cut.Find($"[data-todo-list-id='{l3.Id}'] .todo-list-menu-btn").Click();
        cut.FindAll(".todo-list-menu-popover").Should().ContainSingle();
        cut.Find(".ingredient-row-menu-backdrop").Click();
        cut.FindAll(".todo-list-menu-popover").Should().BeEmpty();
    }

    [Fact]
    public async Task ListTabMenu_ShouldSupportRenameAndDelete_AndCloseMenu()
    {
        var list = await _service.CreateListAsync("Gammalt namn");

        var cut = RenderComponent<TodoPage>();
        cut.WaitForAssertion(() => cut.Find(".todo-active-list-title").TextContent.Should().Be("Gammalt namn"));

        // Rename via tab menu
        cut.Find($"[data-todo-list-id='{list.Id}'] .todo-list-menu-btn").Click();
        cut.Find(".todo-list-rename-btn").Click();
        cut.FindAll(".todo-list-menu-popover").Should().BeEmpty();
        cut.Find("#todo-rename-list-input").Input("Nytt namn");
        cut.Find(".todo-rename-list-form").Submit();

        cut.WaitForAssertion(() =>
        {
            cut.Find(".todo-active-list-title").TextContent.Should().Be("Nytt namn");
            cut.Find($"[data-todo-list-id='{list.Id}'] .todo-list-pill-name").TextContent.Should().Be("Nytt namn");
        });

        // Delete via tab menu -> opens ConfirmDialog, closes menu
        cut.Find($"[data-todo-list-id='{list.Id}'] .todo-list-menu-btn").Click();
        cut.Find(".todo-list-delete-btn").Click();
        cut.FindAll(".todo-list-menu-popover").Should().BeEmpty();
        cut.Find(".modal-dialog-custom .btn-danger-custom").Click();

        cut.WaitForAssertion(() => cut.FindAll(".todo-list-tabs .todo-list-pill").Should().BeEmpty());
    }

    [Fact]
    public async Task TaskInlineEdit_ShouldCollapseNotesByDefaultUnlessPresentOrExpanded()
    {
        var list = await _service.CreateListAsync("Helgfix");
        var withoutNotes = await _service.AddItemAsync(list.Id, "Utan anteckning");
        var withNotes = await _service.AddItemAsync(list.Id, "Med anteckning", "Befintlig notis");

        var cut = RenderComponent<TodoPage>();
        cut.WaitForAssertion(() => cut.FindAll(".todo-task-row").Should().HaveCount(2));

        // Edit task without notes -> notes input hidden initially, toggle visible
        cut.Find($"[data-todo-item-id='{withoutNotes.Id}'] .todo-task-menu-btn").Click();
        cut.Find(".todo-edit-task-btn").Click();

        cut.FindAll(".todo-inline-edit-notes").Should().BeEmpty();
        cut.Find(".todo-edit-notes-toggle").Click();
        cut.FindAll(".todo-inline-edit-notes").Should().ContainSingle();
        cut.Find(".todo-inline-edit-notes").Input("Ny anteckning");
        cut.Find(".todo-edit-save-btn").Click();

        cut.WaitForAssertion(() =>
        {
            cut.Find($"[data-todo-item-id='{withoutNotes.Id}'] .todo-task-notes").TextContent.Should().Be("Ny anteckning");
        });

        // Edit task that already has notes -> notes input expanded automatically
        cut.Find($"[data-todo-item-id='{withNotes.Id}'] .todo-task-menu-btn").Click();
        cut.Find(".todo-edit-task-btn").Click();

        cut.FindAll(".todo-edit-notes-toggle").Should().BeEmpty();
        cut.Find(".todo-inline-edit-notes").GetAttribute("value").Should().Be("Befintlig notis");
    }

    [Fact]
    public async Task TodoPage_ArchiveRestoreAndDelete_ShouldShowFloatingSuccessFeedback_AndPreserveBannerAndDialog()
    {
        var list1 = await _service.CreateListAsync("Aktiva ärenden");
        var list2 = await _service.CreateListAsync("Helgfix");

        var cut = RenderComponent<TodoPage>();
        cut.WaitForAssertion(() => cut.FindAll(".todo-list-tabs .todo-list-pill").Should().HaveCount(2));

        // 1. Archive list2 -> floating success toast
        cut.Find($"[data-todo-list-id='{list2.Id}'] .todo-list-menu-btn").Click();
        cut.Find(".todo-list-archive-btn").Click();

        cut.WaitForAssertion(() =>
        {
            var toast = cut.Find(".todo-feedback.rb-feedback-success");
            toast.ClassList.Should().Contain("rb-feedback-floating");
            toast.TextContent.Should().Contain("Listan har arkiverats.");
        });

        // 2. Open archived section -> select archived list -> archived-list banner with restore button is preserved; restore -> floating success toast
        cut.Find("#todo-toggle-archived-btn").Click();
        cut.WaitForAssertion(() => cut.FindAll(".todo-archived-chips .todo-list-pill.is-archived").Should().ContainSingle());
        cut.Find(".todo-archived-chips .todo-list-pill.is-archived").Click();
        cut.WaitForAssertion(() => cut.FindAll(".todo-archived-banner").Should().ContainSingle());

        cut.Find("#todo-restore-list-btn").Click();
        cut.WaitForAssertion(() =>
        {
            var toast = cut.Find(".todo-feedback.rb-feedback-success");
            toast.ClassList.Should().Contain("rb-feedback-floating");
            toast.TextContent.Should().Contain("Listan har återställts.");
        });

        // 3. Delete list2 -> confirmation dialog preserved; confirm -> floating success toast
        cut.Find($"[data-todo-list-id='{list2.Id}'] .todo-list-menu-btn").Click();
        cut.Find(".todo-list-delete-btn").Click();
        cut.Find(".modal-dialog-custom").TextContent.Should().Contain("Helgfix");
        cut.Find(".modal-dialog-custom .btn-danger-custom").Click();

        cut.WaitForAssertion(() =>
        {
            var toast = cut.Find(".todo-feedback.rb-feedback-success");
            toast.ClassList.Should().Contain("rb-feedback-floating");
            toast.TextContent.Should().Contain("Helgfix");
            toast.TextContent.Should().Contain("togs bort.");
        });
    }

    [Fact]
    public async Task TodoPage_AllRepresentativeActions_ShouldInvokeHandlersCorrectly_EvenWhileFloatingFeedbackIsVisible()
    {
        var listA = await _service.CreateListAsync("Lista A");
        var listB = await _service.CreateListAsync("Lista B");
        await _service.AddItemAsync(listA.Id, "Uppgift A1");

        var cut = RenderComponent<TodoPage>();
        cut.WaitForAssertion(() => cut.FindAll(".todo-list-tabs .todo-list-pill").Should().HaveCount(2));

        // 1. Switch between list tabs
        cut.Find($"[data-todo-list-id='{listB.Id}'] .todo-list-pill-select").Click();
        cut.WaitForAssertion(() => cut.Find(".todo-active-list-title").TextContent.Should().Be("Lista B"));

        // 2. Create a task on Lista B
        cut.Find("#todo-quick-add-input").Input("Ny uppgift B1");
        cut.Find(".todo-quick-add-form").Submit();
        cut.WaitForAssertion(() => cut.FindAll(".todo-task-row").Should().ContainSingle());
        var taskTitle = cut.Find(".todo-task-title").TextContent;
        taskTitle.Should().Be("Ny uppgift B1");

        // 3. Open task contextual menu and edit task
        cut.Find(".todo-task-menu-btn").Click();
        cut.FindAll(".ingredient-row-menu-popover").Should().ContainSingle();
        cut.Find(".todo-edit-task-btn").Click();
        cut.Find(".todo-inline-edit-title").Input("Uppdaterad B1");
        cut.Find(".todo-edit-save-btn").Click();
        cut.WaitForAssertion(() => cut.Find(".todo-task-title").TextContent.Should().Be("Uppdaterad B1"));

        // 4. Check and uncheck task
        cut.Find(".todo-check-btn").Click();
        cut.WaitForAssertion(() => cut.Find(".todo-check-btn").ClassList.Should().Contain("is-checked"));
        cut.Find(".todo-check-btn").Click();
        cut.WaitForAssertion(() => cut.Find(".todo-check-btn").ClassList.Should().NotContain("is-checked"));

        // 5. Open list contextual menu and archive Lista B -> floating feedback becomes visible
        cut.Find($"[data-todo-list-id='{listB.Id}'] .todo-list-menu-btn").Click();
        cut.Find(".todo-list-archive-btn").Click();
        cut.WaitForAssertion(() =>
        {
            cut.Find(".todo-feedback.rb-feedback-floating").TextContent.Should().Contain("Listan har arkiverats.");
            cut.Find(".todo-active-list-title").TextContent.Should().Be("Lista A");
        });

        // 6. While floating feedback is visible, interactions on Lista A still work (no overlay/backdrop interception)
        cut.Find("#todo-new-list-toggle-btn").Click();
        cut.FindAll("#todo-new-list-input").Should().ContainSingle();
        cut.Find("#todo-new-list-toggle-btn").Click();
        cut.FindAll("#todo-new-list-input").Should().BeEmpty();

        cut.Find(".todo-check-btn").Click();
        cut.WaitForAssertion(() => cut.Find(".todo-check-btn").ClassList.Should().Contain("is-checked"));
    }
}
