using Microsoft.EntityFrameworkCore;
using Phew.Core.Models;
using Phew.Core.Services;
using Phew.Infrastructure.Data;

namespace Phew.Infrastructure.Services;

public class TodoService
{
    private static readonly SemaphoreSlim WriteLock = new(1, 1);

    private readonly IDbContextFactory<AppDbContext> _dbFactory;
    private readonly TodoNotifier _notifier;
    private readonly TimeProvider _timeProvider;

    public TodoService(
        IDbContextFactory<AppDbContext> dbFactory,
        TodoNotifier? notifier = null,
        TimeProvider? timeProvider = null)
    {
        _dbFactory = dbFactory;
        _notifier = notifier ?? new TodoNotifier();
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public TodoNotifier Notifier => _notifier;
    public long CurrentVersion => _notifier.Version;

    public event Action<long>? OnChanged
    {
        add => _notifier.OnChanged += value;
        remove => _notifier.OnChanged -= value;
    }

    public async Task<List<TodoList>> GetAllListsAsync(
        bool includeArchived = true,
        CancellationToken cancellationToken = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);

        IQueryable<TodoList> query = db.TodoLists
            .AsNoTracking()
            .Include(l => l.Items.OrderBy(i => i.SortOrder).ThenBy(i => i.Id));

        if (!includeArchived)
        {
            query = query.Where(l => !l.IsArchived);
        }

        var lists = await query.ToListAsync(cancellationToken);

        return lists
            .OrderBy(l => l.IsArchived)
            .ThenBy(l => l.IsArchived ? 0 : l.SortOrder)
            .ThenByDescending(l => l.IsArchived ? (l.ArchivedAt ?? l.UpdatedAt) : DateTimeOffset.MinValue)
            .ThenBy(l => l.Id)
            .ToList();
    }

    public async Task<TodoList?> GetListByIdAsync(int listId, CancellationToken cancellationToken = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        return await db.TodoLists
            .AsNoTracking()
            .Include(l => l.Items.OrderBy(i => i.SortOrder).ThenBy(i => i.Id))
            .FirstOrDefaultAsync(l => l.Id == listId, cancellationToken);
    }

    public async Task<TodoList> CreateListAsync(string name, CancellationToken cancellationToken = default)
    {
        var normalizedName = NormalizeListName(name);

        await WriteLock.WaitAsync(cancellationToken);
        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
            var now = _timeProvider.GetUtcNow();

            var activeLists = await db.TodoLists
                .Where(l => !l.IsArchived)
                .OrderBy(l => l.SortOrder)
                .ThenBy(l => l.Id)
                .ToListAsync(cancellationToken);

            var list = new TodoList
            {
                Name = normalizedName,
                SortOrder = activeLists.Count + 1,
                IsArchived = false,
                CreatedAt = now,
                UpdatedAt = now
            };

            activeLists.Add(list);
            NormalizeListSortOrders(activeLists);
            db.TodoLists.Add(list);
            await db.SaveChangesAsync(cancellationToken);

            _notifier.NotifyChanged();
            return list;
        }
        finally
        {
            WriteLock.Release();
        }
    }

    public async Task<TodoList> RenameListAsync(int listId, string name, CancellationToken cancellationToken = default)
    {
        var normalizedName = NormalizeListName(name);

        await WriteLock.WaitAsync(cancellationToken);
        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
            var list = await db.TodoLists
                .Include(l => l.Items.OrderBy(i => i.SortOrder).ThenBy(i => i.Id))
                .FirstOrDefaultAsync(l => l.Id == listId, cancellationToken)
                ?? throw new KeyNotFoundException($"Att göra-lista med ID {listId} hittades inte.");

            EnsureListIsWritable(list);

            list.Name = normalizedName;
            list.UpdatedAt = _timeProvider.GetUtcNow();
            await db.SaveChangesAsync(cancellationToken);

            _notifier.NotifyChanged();
            return list;
        }
        finally
        {
            WriteLock.Release();
        }
    }

    public async Task<bool> MoveListAsync(int listId, int direction, CancellationToken cancellationToken = default)
    {
        if (direction == 0)
            return false;

        await WriteLock.WaitAsync(cancellationToken);
        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
            var activeLists = await db.TodoLists
                .Where(l => !l.IsArchived)
                .OrderBy(l => l.SortOrder)
                .ThenBy(l => l.Id)
                .ToListAsync(cancellationToken);

            var index = activeLists.FindIndex(l => l.Id == listId);
            if (index < 0)
            {
                var archivedOrMissing = await db.TodoLists
                    .AsNoTracking()
                    .FirstOrDefaultAsync(l => l.Id == listId, cancellationToken)
                    ?? throw new KeyNotFoundException($"Att göra-lista med ID {listId} hittades inte.");

                EnsureListIsWritable(archivedOrMissing);
                return false;
            }

            var targetIndex = direction < 0 ? index - 1 : index + 1;
            if (targetIndex < 0 || targetIndex >= activeLists.Count)
                return false;

            (activeLists[index], activeLists[targetIndex]) = (activeLists[targetIndex], activeLists[index]);
            var now = _timeProvider.GetUtcNow();
            activeLists[index].UpdatedAt = now;
            activeLists[targetIndex].UpdatedAt = now;
            NormalizeListSortOrders(activeLists);

            await db.SaveChangesAsync(cancellationToken);
            _notifier.NotifyChanged();
            return true;
        }
        finally
        {
            WriteLock.Release();
        }
    }

    public async Task<TodoList> ArchiveListAsync(int listId, CancellationToken cancellationToken = default)
    {
        await WriteLock.WaitAsync(cancellationToken);
        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
            var list = await db.TodoLists
                .Include(l => l.Items.OrderBy(i => i.SortOrder).ThenBy(i => i.Id))
                .FirstOrDefaultAsync(l => l.Id == listId, cancellationToken)
                ?? throw new KeyNotFoundException($"Att göra-lista med ID {listId} hittades inte.");

            if (list.IsArchived)
                return list;

            var now = _timeProvider.GetUtcNow();
            list.IsArchived = true;
            list.ArchivedAt = now;
            list.UpdatedAt = now;
            list.SortOrder = 0;

            var remainingActive = await db.TodoLists
                .Where(l => !l.IsArchived && l.Id != listId)
                .OrderBy(l => l.SortOrder)
                .ThenBy(l => l.Id)
                .ToListAsync(cancellationToken);

            NormalizeListSortOrders(remainingActive);
            await db.SaveChangesAsync(cancellationToken);

            _notifier.NotifyChanged();
            return list;
        }
        finally
        {
            WriteLock.Release();
        }
    }

    public async Task<TodoList> RestoreListAsync(int listId, CancellationToken cancellationToken = default)
    {
        await WriteLock.WaitAsync(cancellationToken);
        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
            var list = await db.TodoLists
                .Include(l => l.Items.OrderBy(i => i.SortOrder).ThenBy(i => i.Id))
                .FirstOrDefaultAsync(l => l.Id == listId, cancellationToken)
                ?? throw new KeyNotFoundException($"Att göra-lista med ID {listId} hittades inte.");

            if (!list.IsArchived)
                return list;

            var activeLists = await db.TodoLists
                .Where(l => !l.IsArchived)
                .OrderBy(l => l.SortOrder)
                .ThenBy(l => l.Id)
                .ToListAsync(cancellationToken);

            var now = _timeProvider.GetUtcNow();
            list.IsArchived = false;
            list.ArchivedAt = null;
            list.UpdatedAt = now;

            activeLists.Add(list);
            NormalizeListSortOrders(activeLists);
            await db.SaveChangesAsync(cancellationToken);

            _notifier.NotifyChanged();
            return list;
        }
        finally
        {
            WriteLock.Release();
        }
    }

    public async Task<bool> DeleteListAsync(int listId, CancellationToken cancellationToken = default)
    {
        await WriteLock.WaitAsync(cancellationToken);
        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
            var list = await db.TodoLists.FirstOrDefaultAsync(l => l.Id == listId, cancellationToken);
            if (list == null)
                return false;

            var wasActive = !list.IsArchived;
            db.TodoLists.Remove(list);

            if (wasActive)
            {
                var remainingActive = await db.TodoLists
                    .Where(l => !l.IsArchived && l.Id != listId)
                    .OrderBy(l => l.SortOrder)
                    .ThenBy(l => l.Id)
                    .ToListAsync(cancellationToken);

                NormalizeListSortOrders(remainingActive);
            }

            await db.SaveChangesAsync(cancellationToken);
            _notifier.NotifyChanged();
            return true;
        }
        finally
        {
            WriteLock.Release();
        }
    }

    public async Task<TodoItem> AddItemAsync(
        int listId,
        string title,
        string? notes = null,
        CancellationToken cancellationToken = default)
    {
        var normalizedTitle = NormalizeItemTitle(title);
        var normalizedNotes = NormalizeItemNotes(notes);

        await WriteLock.WaitAsync(cancellationToken);
        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
            var list = await db.TodoLists
                .Include(l => l.Items)
                .FirstOrDefaultAsync(l => l.Id == listId, cancellationToken)
                ?? throw new KeyNotFoundException($"Att göra-lista med ID {listId} hittades inte.");

            EnsureListIsWritable(list);

            var now = _timeProvider.GetUtcNow();
            var orderedItems = list.Items
                .OrderBy(i => i.SortOrder)
                .ThenBy(i => i.Id)
                .ToList();

            var newItem = new TodoItem
            {
                TodoListId = list.Id,
                Title = normalizedTitle,
                Notes = normalizedNotes,
                IsCompleted = false,
                CompletedAt = null,
                CreatedAt = now,
                UpdatedAt = now
            };

            // Insert after the last open item so new tasks appear at the end of "Att göra"
            // while maintaining one single dense SortOrder sequence per list.
            var lastOpenIndex = orderedItems.FindLastIndex(i => !i.IsCompleted);
            var insertIndex = lastOpenIndex >= 0 ? lastOpenIndex + 1 : 0;
            orderedItems.Insert(insertIndex, newItem);

            NormalizeItemSortOrders(orderedItems);
            list.Items.Add(newItem);
            list.UpdatedAt = now;

            await db.SaveChangesAsync(cancellationToken);
            _notifier.NotifyChanged();
            return newItem;
        }
        finally
        {
            WriteLock.Release();
        }
    }

    public async Task<TodoItem> UpdateItemAsync(
        int itemId,
        string title,
        string? notes = null,
        CancellationToken cancellationToken = default)
    {
        var normalizedTitle = NormalizeItemTitle(title);
        var normalizedNotes = NormalizeItemNotes(notes);

        await WriteLock.WaitAsync(cancellationToken);
        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
            var item = await db.TodoItems
                .Include(i => i.TodoList)
                .FirstOrDefaultAsync(i => i.Id == itemId, cancellationToken)
                ?? throw new KeyNotFoundException($"Uppgift med ID {itemId} hittades inte.");

            EnsureListIsWritable(item.TodoList);

            var now = _timeProvider.GetUtcNow();
            item.Title = normalizedTitle;
            item.Notes = normalizedNotes;
            item.UpdatedAt = now;
            item.TodoList.UpdatedAt = now;

            await db.SaveChangesAsync(cancellationToken);
            _notifier.NotifyChanged();
            return item;
        }
        finally
        {
            WriteLock.Release();
        }
    }

    public async Task<TodoItem> SetItemCompletedAsync(
        int itemId,
        bool isCompleted,
        CancellationToken cancellationToken = default)
    {
        await WriteLock.WaitAsync(cancellationToken);
        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
            var item = await db.TodoItems
                .Include(i => i.TodoList)
                .FirstOrDefaultAsync(i => i.Id == itemId, cancellationToken)
                ?? throw new KeyNotFoundException($"Uppgift med ID {itemId} hittades inte.");

            EnsureListIsWritable(item.TodoList);

            if (item.IsCompleted == isCompleted)
                return item;

            var now = _timeProvider.GetUtcNow();
            item.IsCompleted = isCompleted;
            item.CompletedAt = isCompleted ? now : null;
            item.UpdatedAt = now;
            item.TodoList.UpdatedAt = now;

            // Preserve SortOrder so completing/reopening keeps sensible relative positioning.
            await db.SaveChangesAsync(cancellationToken);
            _notifier.NotifyChanged();
            return item;
        }
        finally
        {
            WriteLock.Release();
        }
    }

    public async Task<bool> MoveItemAsync(int itemId, int direction, CancellationToken cancellationToken = default)
    {
        if (direction == 0)
            return false;

        await WriteLock.WaitAsync(cancellationToken);
        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
            var target = await db.TodoItems
                .Include(i => i.TodoList)
                    .ThenInclude(l => l.Items)
                .FirstOrDefaultAsync(i => i.Id == itemId, cancellationToken)
                ?? throw new KeyNotFoundException($"Uppgift med ID {itemId} hittades inte.");

            EnsureListIsWritable(target.TodoList);

            var allOrdered = target.TodoList.Items
                .OrderBy(i => i.SortOrder)
                .ThenBy(i => i.Id)
                .ToList();

            var groupItems = allOrdered
                .Where(i => i.IsCompleted == target.IsCompleted)
                .ToList();

            var groupIndex = groupItems.FindIndex(i => i.Id == itemId);
            if (groupIndex < 0)
                return false;

            var neighborGroupIndex = direction < 0 ? groupIndex - 1 : groupIndex + 1;
            if (neighborGroupIndex < 0 || neighborGroupIndex >= groupItems.Count)
                return false;

            var neighbor = groupItems[neighborGroupIndex];
            var targetIndexInAll = allOrdered.IndexOf(target);
            var neighborIndexInAll = allOrdered.IndexOf(neighbor);

            (allOrdered[targetIndexInAll], allOrdered[neighborIndexInAll]) =
                (allOrdered[neighborIndexInAll], allOrdered[targetIndexInAll]);

            var now = _timeProvider.GetUtcNow();
            target.UpdatedAt = now;
            neighbor.UpdatedAt = now;
            target.TodoList.UpdatedAt = now;

            NormalizeItemSortOrders(allOrdered);
            await db.SaveChangesAsync(cancellationToken);

            _notifier.NotifyChanged();
            return true;
        }
        finally
        {
            WriteLock.Release();
        }
    }

    public async Task<bool> DeleteItemAsync(int itemId, CancellationToken cancellationToken = default)
    {
        await WriteLock.WaitAsync(cancellationToken);
        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
            var item = await db.TodoItems
                .Include(i => i.TodoList)
                    .ThenInclude(l => l.Items)
                .FirstOrDefaultAsync(i => i.Id == itemId, cancellationToken);

            if (item == null)
                return false;

            EnsureListIsWritable(item.TodoList);

            var remaining = item.TodoList.Items
                .Where(i => i.Id != itemId)
                .OrderBy(i => i.SortOrder)
                .ThenBy(i => i.Id)
                .ToList();

            item.TodoList.UpdatedAt = _timeProvider.GetUtcNow();
            db.TodoItems.Remove(item);
            NormalizeItemSortOrders(remaining);

            await db.SaveChangesAsync(cancellationToken);
            _notifier.NotifyChanged();
            return true;
        }
        finally
        {
            WriteLock.Release();
        }
    }

    public async Task<int> ClearCompletedItemsAsync(int listId, CancellationToken cancellationToken = default)
    {
        await WriteLock.WaitAsync(cancellationToken);
        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
            var list = await db.TodoLists
                .Include(l => l.Items)
                .FirstOrDefaultAsync(l => l.Id == listId, cancellationToken)
                ?? throw new KeyNotFoundException($"Att göra-lista med ID {listId} hittades inte.");

            EnsureListIsWritable(list);

            var completed = list.Items.Where(i => i.IsCompleted).ToList();
            if (completed.Count == 0)
                return 0;

            var remaining = list.Items
                .Where(i => !i.IsCompleted)
                .OrderBy(i => i.SortOrder)
                .ThenBy(i => i.Id)
                .ToList();

            list.UpdatedAt = _timeProvider.GetUtcNow();
            db.TodoItems.RemoveRange(completed);
            NormalizeItemSortOrders(remaining);

            await db.SaveChangesAsync(cancellationToken);
            _notifier.NotifyChanged();
            return completed.Count;
        }
        finally
        {
            WriteLock.Release();
        }
    }

    private static void EnsureListIsWritable(TodoList list)
    {
        if (list.IsArchived)
        {
            throw new InvalidOperationException("Arkiverade listor är skrivskyddade tills de återställs.");
        }
    }

    private static void NormalizeListSortOrders(List<TodoList> activeLists)
    {
        for (var i = 0; i < activeLists.Count; i++)
        {
            activeLists[i].SortOrder = i + 1;
        }
    }

    private static void NormalizeItemSortOrders(List<TodoItem> orderedItems)
    {
        for (var i = 0; i < orderedItems.Count; i++)
        {
            orderedItems[i].SortOrder = i + 1;
        }
    }

    private static string NormalizeListName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("Ange ett namn på listan.", nameof(name));

        var trimmed = name.Trim();
        if (trimmed.Length > TodoList.MaxNameLength)
            throw new ArgumentException($"Listnamnet får vara högst {TodoList.MaxNameLength} tecken.", nameof(name));

        return trimmed;
    }

    private static string NormalizeItemTitle(string title)
    {
        if (string.IsNullOrWhiteSpace(title))
            throw new ArgumentException("Ange en titel på uppgiften.", nameof(title));

        var trimmed = title.Trim();
        if (trimmed.Length > TodoItem.MaxTitleLength)
            throw new ArgumentException($"Uppgiftens titel får vara högst {TodoItem.MaxTitleLength} tecken.", nameof(title));

        return trimmed;
    }

    private static string? NormalizeItemNotes(string? notes)
    {
        if (string.IsNullOrWhiteSpace(notes))
            return null;

        var trimmed = notes.Trim();
        if (trimmed.Length > TodoItem.MaxNotesLength)
            throw new ArgumentException($"Anteckningen får vara högst {TodoItem.MaxNotesLength} tecken.", nameof(notes));

        return trimmed;
    }
}
