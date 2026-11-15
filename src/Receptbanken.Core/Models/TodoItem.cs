namespace Receptbanken.Core.Models;

public class TodoItem
{
    public const int MaxTitleLength = 200;
    public const int MaxNotesLength = 1000;

    public int Id { get; set; }
    public int TodoListId { get; set; }
    public TodoList TodoList { get; set; } = null!;
    public string Title { get; set; } = string.Empty;
    public string? Notes { get; set; }
    public bool IsCompleted { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public int SortOrder { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}
