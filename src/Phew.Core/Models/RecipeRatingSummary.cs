using System.Globalization;

namespace Phew.Core.Models;

public sealed record RecipeRatingSummary(double? AverageStars, int VoteCount)
{
    private static readonly CultureInfo SwedishCulture = CultureInfo.GetCultureInfo("sv-SE");

    public static readonly RecipeRatingSummary Empty = new(null, 0);

    public bool HasRatings => VoteCount > 0 && AverageStars.HasValue;

    public string FormatAverageSwedish() =>
        HasRatings ? AverageStars!.Value.ToString("0.0", SwedishCulture) : string.Empty;

    public string FormatVoteCountSwedish() =>
        VoteCount == 1 ? "1 röst" : $"{VoteCount} röster";

    public string FormatBadgeText() =>
        HasRatings ? $"{FormatAverageSwedish()} ({FormatVoteCountSwedish()})" : "Betygsätt";

    public static RecipeRatingSummary FromRatings(IEnumerable<RecipeRating>? ratings)
    {
        if (ratings == null)
        {
            return Empty;
        }

        int count = 0;
        long sum = 0;
        foreach (var rating in ratings)
        {
            if (rating.Stars >= RecipeRating.MinStars && rating.Stars <= RecipeRating.MaxStars)
            {
                sum += rating.Stars;
                count++;
            }
        }

        if (count == 0)
        {
            return Empty;
        }

        return new RecipeRatingSummary((double)sum / count, count);
    }
}
