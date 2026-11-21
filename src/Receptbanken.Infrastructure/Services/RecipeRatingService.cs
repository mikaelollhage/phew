using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Receptbanken.Core.Models;
using Receptbanken.Core.Services;
using Receptbanken.Infrastructure.Data;

namespace Receptbanken.Infrastructure.Services;

public sealed record FamilyMemberRatingRowDto(
    int FamilyMemberId,
    string FamilyMemberName,
    int? Stars);

public sealed record RecipeRatingModalDataDto(
    int RecipeId,
    RecipeRatingSummary Summary,
    IReadOnlyList<FamilyMemberRatingRowDto> MemberRows);

public class RecipeRatingService
{
    private static readonly SemaphoreSlim WriteLock = new(1, 1);

    private readonly IDbContextFactory<AppDbContext> _dbFactory;
    private readonly RecipeRatingNotifier _notifier;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<RecipeRatingService> _logger;

    public RecipeRatingService(
        IDbContextFactory<AppDbContext> dbFactory,
        RecipeRatingNotifier? notifier = null,
        TimeProvider? timeProvider = null,
        ILogger<RecipeRatingService>? logger = null)
    {
        _dbFactory = dbFactory;
        _notifier = notifier ?? new RecipeRatingNotifier();
        _timeProvider = timeProvider ?? TimeProvider.System;
        _logger = logger ?? NullLogger<RecipeRatingService>.Instance;
    }

    public RecipeRatingNotifier Notifier => _notifier;
    public long CurrentVersion => _notifier.Version;

    public event Action<long>? OnChanged
    {
        add => _notifier.OnChanged += value;
        remove => _notifier.OnChanged -= value;
    }

    public async Task<RecipeRatingSummary> GetSummaryAsync(
        int recipeId,
        CancellationToken cancellationToken = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var stars = await db.RecipeRatings
            .AsNoTracking()
            .Where(r => r.RecipeId == recipeId)
            .Select(r => r.Stars)
            .ToListAsync(cancellationToken);

        return SummarizeStars(stars);
    }

    public async Task<Dictionary<int, RecipeRatingSummary>> GetSummariesAsync(
        IEnumerable<int> recipeIds,
        CancellationToken cancellationToken = default)
    {
        var idList = recipeIds.Distinct().ToList();
        if (idList.Count == 0)
        {
            return new Dictionary<int, RecipeRatingSummary>();
        }

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var ratings = await db.RecipeRatings
            .AsNoTracking()
            .Where(r => idList.Contains(r.RecipeId))
            .Select(r => new { r.RecipeId, r.Stars })
            .ToListAsync(cancellationToken);

        return ratings
            .GroupBy(r => r.RecipeId)
            .ToDictionary(
                g => g.Key,
                g => SummarizeStars(g.Select(x => x.Stars)));
    }

    public async Task<RecipeRatingModalDataDto> GetModalDataAsync(
        int recipeId,
        CancellationToken cancellationToken = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);

        var activeMembers = await db.FamilyMembers
            .AsNoTracking()
            .Where(m => m.IsActive)
            .OrderBy(m => m.SortOrder)
            .ThenBy(m => m.Name)
            .ThenBy(m => m.Id)
            .ToListAsync(cancellationToken);

        var ratings = await db.RecipeRatings
            .AsNoTracking()
            .Where(r => r.RecipeId == recipeId)
            .ToListAsync(cancellationToken);

        var starsByMemberId = ratings.ToDictionary(r => r.FamilyMemberId, r => r.Stars);
        var rows = activeMembers
            .Select(m => new FamilyMemberRatingRowDto(
                m.Id,
                m.Name,
                starsByMemberId.TryGetValue(m.Id, out var stars) ? stars : null))
            .ToList();

        var summary = RecipeRatingSummary.FromRatings(ratings);
        return new RecipeRatingModalDataDto(recipeId, summary, rows);
    }

    public async Task<RecipeRatingSummary> SetRatingAsync(
        int recipeId,
        int familyMemberId,
        int stars,
        CancellationToken cancellationToken = default)
    {
        if (stars < RecipeRating.MinStars || stars > RecipeRating.MaxStars)
        {
            throw new ArgumentOutOfRangeException(
                nameof(stars),
                $"Betyget måste vara mellan {RecipeRating.MinStars} och {RecipeRating.MaxStars} stjärnor.");
        }

        await WriteLock.WaitAsync(cancellationToken);
        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);

            var recipeExists = await db.Recipes
                .AsNoTracking()
                .AnyAsync(r => r.Id == recipeId, cancellationToken);
            if (!recipeExists)
            {
                throw new KeyNotFoundException($"Recept med ID {recipeId} hittades inte.");
            }

            var memberExists = await db.FamilyMembers
                .AsNoTracking()
                .AnyAsync(m => m.Id == familyMemberId, cancellationToken);
            if (!memberExists)
            {
                throw new KeyNotFoundException($"Familjemedlem med ID {familyMemberId} hittades inte.");
            }

            var now = _timeProvider.GetUtcNow();
            var existing = await db.RecipeRatings
                .FirstOrDefaultAsync(
                    r => r.RecipeId == recipeId && r.FamilyMemberId == familyMemberId,
                    cancellationToken);

            if (existing != null)
            {
                existing.Stars = stars;
                existing.UpdatedAt = now;
            }
            else
            {
                db.RecipeRatings.Add(new RecipeRating
                {
                    RecipeId = recipeId,
                    FamilyMemberId = familyMemberId,
                    Stars = stars,
                    CreatedAt = now,
                    UpdatedAt = now
                });
            }

            await db.SaveChangesAsync(cancellationToken);

            var allStars = await db.RecipeRatings
                .AsNoTracking()
                .Where(r => r.RecipeId == recipeId)
                .Select(r => r.Stars)
                .ToListAsync(cancellationToken);

            var summary = SummarizeStars(allStars);
            _logger.LogInformation(
                "Sparade betyg {Stars} för recept {RecipeId} och familjemedlem {FamilyMemberId}",
                stars,
                recipeId,
                familyMemberId);

            _notifier.NotifyChanged();
            return summary;
        }
        finally
        {
            WriteLock.Release();
        }
    }

    private static RecipeRatingSummary SummarizeStars(IEnumerable<int> stars)
    {
        int count = 0;
        long sum = 0;
        foreach (var s in stars)
        {
            if (s >= RecipeRating.MinStars && s <= RecipeRating.MaxStars)
            {
                sum += s;
                count++;
            }
        }

        return count == 0
            ? RecipeRatingSummary.Empty
            : new RecipeRatingSummary((double)sum / count, count);
    }
}
