namespace Receptbanken.Core.Models;

public class RecipeRating
{
    public const int MinStars = 1;
    public const int MaxStars = 5;

    public int Id { get; set; }
    public int RecipeId { get; set; }
    public int FamilyMemberId { get; set; }
    public int Stars { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    public Recipe Recipe { get; set; } = null!;
    public FamilyMember FamilyMember { get; set; } = null!;
}
