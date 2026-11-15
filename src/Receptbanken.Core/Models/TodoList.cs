namespace Receptbanken.Core.Models;

public class TodoList
{
    public const int MaxNameLength = 100;

    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public int SortOrder { get; set; }
    public bool IsArchived { get; set; }
    public DateTimeOffset? ArchivedAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }

    public List<TodoItem> Items { get; set; } = new();
}
