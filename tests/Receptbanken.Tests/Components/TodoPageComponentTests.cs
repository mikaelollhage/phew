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

        // Archive via UI
        cut.Find("#todo-archive-list-btn").Click();
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
}
