using FluentAssertions;
using Phew.Core.Models;
using Phew.Core.Services;
using Phew.Infrastructure.Services;

namespace Phew.Tests.Infrastructure;

public class TodoServiceTests : IDisposable
{
    private readonly TestDbContextFactory _dbFactory;
    private readonly TodoNotifier _notifier;
    private readonly TodoService _service;

    public TodoServiceTests()
    {
        _dbFactory = new TestDbContextFactory();
        _notifier = new TodoNotifier();
        _service = new TodoService(_dbFactory, _notifier);
    }

    public void Dispose()
    {
        _dbFactory.Dispose();
    }

    [Fact]
    public async Task ListCrudAndReorder_ShouldMaintainDenseSortOrderAndNotify()
    {
        var notifications = 0;
        _notifier.OnChanged += _ => notifications++;

        var listA = await _service.CreateListAsync("Helgfix");
        var listB = await _service.CreateListAsync("Respackning");
        var listC = await _service.CreateListAsync("Trädgård");

        var lists = await _service.GetAllListsAsync();
        lists.Select(l => l.Name).Should().Equal("Helgfix", "Respackning", "Trädgård");
        lists.Select(l => l.SortOrder).Should().Equal(1, 2, 3);

        // Move Respackning earlier
        (await _service.MoveListAsync(listB.Id, -1)).Should().BeTrue();
        // Moving first list earlier is a no-op
        (await _service.MoveListAsync(listB.Id, -1)).Should().BeFalse();

        await _service.RenameListAsync(listA.Id, "Helgfix & Stora projekt");
        await _service.DeleteListAsync(listC.Id);

        lists = await _service.GetAllListsAsync();
        lists.Select(l => l.Name).Should().Equal("Respackning", "Helgfix & Stora projekt");
        lists.Select(l => l.SortOrder).Should().Equal(1, 2);
        notifications.Should().Be(6);
    }

    [Fact]
    public async Task TaskOrdering_SingleDenseSequence_ShouldReorderWithinGroupAndPreservePositionOnCompleteReopen()
    {
        var list = await _service.CreateListAsync("Vardag");
        var t1 = await _service.AddItemAsync(list.Id, "Uppgift 1");
        var t2 = await _service.AddItemAsync(list.Id, "Uppgift 2");
        var t3 = await _service.AddItemAsync(list.Id, "Uppgift 3");

        // Complete middle task t2 -> SortOrder stays 2
        await _service.SetItemCompletedAsync(t2.Id, true);

        // Move open task t3 up within the open group (swaps with t1, skipping completed t2)
        (await _service.MoveItemAsync(t3.Id, -1)).Should().BeTrue();
        // t3 is now first open task -> moving up again is false
        (await _service.MoveItemAsync(t3.Id, -1)).Should().BeFalse();

        var loaded = await _service.GetListByIdAsync(list.Id);
        loaded.Should().NotBeNull();
        loaded!.Items.OrderBy(i => i.SortOrder).Select(i => (i.Title, i.SortOrder, i.IsCompleted))
            .Should().Equal(
                ("Uppgift 3", 1, false),
                ("Uppgift 2", 2, true),
                ("Uppgift 1", 3, false));

        // Reopen t2 -> returns to its preserved SortOrder (between Uppgift 3 and Uppgift 1)
        await _service.SetItemCompletedAsync(t2.Id, false);
        loaded = await _service.GetListByIdAsync(list.Id);
        loaded!.Items.Where(i => !i.IsCompleted).OrderBy(i => i.SortOrder).Select(i => i.Title)
            .Should().Equal("Uppgift 3", "Uppgift 2", "Uppgift 1");
    }

    [Fact]
    public async Task ArchivedList_ShouldBeReadOnlyUntilRestored()
    {
        var list = await _service.CreateListAsync("Sommarstugan");
        var item = await _service.AddItemAsync(list.Id, "Måla altanen", "Vit färg");

        await _service.ArchiveListAsync(list.Id);

        await Assert.ThrowsAsync<InvalidOperationException>(() => _service.RenameListAsync(list.Id, "Nytt namn"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => _service.MoveListAsync(list.Id, -1));
        await Assert.ThrowsAsync<InvalidOperationException>(() => _service.AddItemAsync(list.Id, "Ny uppgift"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => _service.UpdateItemAsync(item.Id, "Ändrad"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => _service.SetItemCompletedAsync(item.Id, true));
        await Assert.ThrowsAsync<InvalidOperationException>(() => _service.MoveItemAsync(item.Id, -1));
        await Assert.ThrowsAsync<InvalidOperationException>(() => _service.DeleteItemAsync(item.Id));
        await Assert.ThrowsAsync<InvalidOperationException>(() => _service.ClearCompletedItemsAsync(list.Id));

        // Restore makes it writable again
        var restored = await _service.RestoreListAsync(list.Id);
        restored.IsArchived.Should().BeFalse();
        restored.ArchivedAt.Should().BeNull();

        await _service.SetItemCompletedAsync(item.Id, true);
        var loaded = await _service.GetListByIdAsync(list.Id);
        loaded!.Items.Should().ContainSingle(i => i.IsCompleted);
    }
}
