using Microsoft.EntityFrameworkCore;
using Receptbanken.Core.Models;
using Receptbanken.Core.Services;
using Receptbanken.Infrastructure.Data;

namespace Receptbanken.Infrastructure.Services;

/// <summary>
/// Manages persistent family members, enforcing trimmed non-empty names, case-insensitive
/// uniqueness, soft deactivation, predictable ordering, and cross-client change notifications.
/// </summary>
public class FamilyMemberService
{
    private static readonly SemaphoreSlim WriteLock = new(1, 1);

    private readonly IDbContextFactory<AppDbContext> _dbFactory;
    private readonly FamilyMemberNotifier _notifier;
    private readonly CalendarNotifier? _calendarNotifier;
    private readonly RecipeRatingNotifier? _recipeRatingNotifier;
    private readonly TimeProvider _timeProvider;

    public FamilyMemberService(
        IDbContextFactory<AppDbContext> dbFactory,
        FamilyMemberNotifier? notifier = null,
        TimeProvider? timeProvider = null,
        CalendarNotifier? calendarNotifier = null,
        RecipeRatingNotifier? recipeRatingNotifier = null)
    {
        _dbFactory = dbFactory;
        _notifier = notifier ?? new FamilyMemberNotifier();
        _calendarNotifier = calendarNotifier;
        _recipeRatingNotifier = recipeRatingNotifier;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public FamilyMemberNotifier Notifier => _notifier;
    public long CurrentVersion => _notifier.Version;

    public event Action<long>? OnChanged
    {
        add => _notifier.OnChanged += value;
        remove => _notifier.OnChanged -= value;
    }

    public async Task<List<FamilyMember>> GetAllAsync(
        bool includeInactive = true,
        CancellationToken cancellationToken = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);

        IQueryable<FamilyMember> query = db.FamilyMembers.AsNoTracking();
        if (!includeInactive)
        {
            query = query.Where(m => m.IsActive);
        }

        var members = await query.ToListAsync(cancellationToken);

        return members
            .OrderBy(m => !m.IsActive)
            .ThenBy(m => m.SortOrder)
            .ThenBy(m => m.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(m => m.Id)
            .ToList();
    }

    public async Task<FamilyMember> CreateAsync(string name, CancellationToken cancellationToken = default)
    {
        var normalizedName = NormalizeName(name);

        await WriteLock.WaitAsync(cancellationToken);
        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
            var existingMembers = await db.FamilyMembers.ToListAsync(cancellationToken);

            var duplicate = existingMembers.FirstOrDefault(
                m => string.Equals(m.Name, normalizedName, StringComparison.OrdinalIgnoreCase));

            if (duplicate != null)
            {
                if (!duplicate.IsActive)
                {
                    throw new InvalidOperationException(
                        $"Familjemedlemmen \"{duplicate.Name}\" finns redan som inaktiv. Återaktivera den i stället för att skapa en ny.");
                }

                throw new InvalidOperationException(
                    $"Familjemedlemmen \"{duplicate.Name}\" finns redan.");
            }

            var activeMembers = existingMembers
                .Where(m => m.IsActive)
                .OrderBy(m => m.SortOrder)
                .ThenBy(m => m.Id)
                .ToList();

            var now = _timeProvider.GetUtcNow();
            var member = new FamilyMember
            {
                Name = normalizedName,
                IsActive = true,
                SortOrder = activeMembers.Count + 1,
                CreatedAt = now,
                UpdatedAt = now
            };

            activeMembers.Add(member);
            NormalizeActiveSortOrders(activeMembers);
            db.FamilyMembers.Add(member);

            await SaveWithUniqueConstraintTranslationAsync(db, normalizedName, cancellationToken);
            _notifier.NotifyChanged();
            return member;
        }
        finally
        {
            WriteLock.Release();
        }
    }

    public async Task<FamilyMember> RenameAsync(int id, string newName, CancellationToken cancellationToken = default)
    {
        var normalizedName = NormalizeName(newName);

        await WriteLock.WaitAsync(cancellationToken);
        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
            var allMembers = await db.FamilyMembers.ToListAsync(cancellationToken);

            var member = allMembers.FirstOrDefault(m => m.Id == id)
                ?? throw new KeyNotFoundException($"Familjemedlem med ID {id} hittades inte.");

            var duplicate = allMembers.FirstOrDefault(
                m => m.Id != id && string.Equals(m.Name, normalizedName, StringComparison.OrdinalIgnoreCase));

            if (duplicate != null)
            {
                if (!duplicate.IsActive)
                {
                    throw new InvalidOperationException(
                        $"Det finns redan en inaktiv familjemedlem som heter \"{duplicate.Name}\".");
                }

                throw new InvalidOperationException(
                    $"Det finns redan en familjemedlem som heter \"{duplicate.Name}\".");
            }

            if (string.Equals(member.Name, normalizedName, StringComparison.Ordinal))
            {
                return member;
            }

            member.Name = normalizedName;
            member.UpdatedAt = _timeProvider.GetUtcNow();

            await SaveWithUniqueConstraintTranslationAsync(db, normalizedName, cancellationToken);
            _notifier.NotifyChanged();
            _calendarNotifier?.NotifyChanged();
            return member;
        }
        finally
        {
            WriteLock.Release();
        }
    }

    public async Task<FamilyMember> DeactivateAsync(int id, CancellationToken cancellationToken = default)
    {
        await WriteLock.WaitAsync(cancellationToken);
        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
            var member = await db.FamilyMembers.FirstOrDefaultAsync(m => m.Id == id, cancellationToken)
                ?? throw new KeyNotFoundException($"Familjemedlem med ID {id} hittades inte.");

            if (!member.IsActive)
            {
                return member;
            }

            member.IsActive = false;
            member.UpdatedAt = _timeProvider.GetUtcNow();

            var remainingActive = await db.FamilyMembers
                .Where(m => m.IsActive && m.Id != id)
                .OrderBy(m => m.SortOrder)
                .ThenBy(m => m.Id)
                .ToListAsync(cancellationToken);

            NormalizeActiveSortOrders(remainingActive);
            await db.SaveChangesAsync(cancellationToken);

            _notifier.NotifyChanged();
            return member;
        }
        finally
        {
            WriteLock.Release();
        }
    }

    public async Task<FamilyMember> ReactivateAsync(int id, CancellationToken cancellationToken = default)
    {
        await WriteLock.WaitAsync(cancellationToken);
        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
            var member = await db.FamilyMembers.FirstOrDefaultAsync(m => m.Id == id, cancellationToken)
                ?? throw new KeyNotFoundException($"Familjemedlem med ID {id} hittades inte.");

            if (member.IsActive)
            {
                return member;
            }

            var activeMembers = await db.FamilyMembers
                .Where(m => m.IsActive)
                .OrderBy(m => m.SortOrder)
                .ThenBy(m => m.Id)
                .ToListAsync(cancellationToken);

            var now = _timeProvider.GetUtcNow();
            member.IsActive = true;
            member.UpdatedAt = now;

            activeMembers.Add(member);
            NormalizeActiveSortOrders(activeMembers);
            await db.SaveChangesAsync(cancellationToken);

            _notifier.NotifyChanged();
            return member;
        }
        finally
        {
            WriteLock.Release();
        }
    }

    public async Task<bool> MoveAsync(int id, int direction, CancellationToken cancellationToken = default)
    {
        if (direction == 0)
        {
            return false;
        }

        await WriteLock.WaitAsync(cancellationToken);
        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
            var activeMembers = await db.FamilyMembers
                .Where(m => m.IsActive)
                .OrderBy(m => m.SortOrder)
                .ThenBy(m => m.Id)
                .ToListAsync(cancellationToken);

            var index = activeMembers.FindIndex(m => m.Id == id);
            if (index < 0)
            {
                var exists = await db.FamilyMembers
                    .AsNoTracking()
                    .AnyAsync(m => m.Id == id, cancellationToken);
                if (!exists)
                {
                    throw new KeyNotFoundException($"Familjemedlem med ID {id} hittades inte.");
                }

                return false;
            }

            var targetIndex = direction < 0 ? index - 1 : index + 1;
            if (targetIndex < 0 || targetIndex >= activeMembers.Count)
            {
                return false;
            }

            (activeMembers[index], activeMembers[targetIndex]) = (activeMembers[targetIndex], activeMembers[index]);
            var now = _timeProvider.GetUtcNow();
            activeMembers[index].UpdatedAt = now;
            activeMembers[targetIndex].UpdatedAt = now;
            NormalizeActiveSortOrders(activeMembers);

            await db.SaveChangesAsync(cancellationToken);
            _notifier.NotifyChanged();
            return true;
        }
        finally
        {
            WriteLock.Release();
        }
    }

    /// <summary>
    /// Permanently deletes a family member (active or inactive).
    /// Future FAM-1C (explicit calendar member associations) and RATE-1B (individual recipe ratings)
    /// must remove those explicit associations and ratings inside this transaction without deleting
    /// calendar events or recipes.
    /// </summary>
    public async Task<FamilyMember> DeleteAsync(int id, CancellationToken cancellationToken = default)
    {
        await WriteLock.WaitAsync(cancellationToken);
        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
            await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

            var member = await db.FamilyMembers.FirstOrDefaultAsync(m => m.Id == id, cancellationToken)
                ?? throw new KeyNotFoundException($"Familjemedlem med ID {id} hittades inte.");

            await db.CalendarEventParticipants
                .Where(p => p.FamilyMemberId == id)
                .ExecuteDeleteAsync(cancellationToken);

            await db.CalendarEventExceptionParticipants
                .Where(p => p.FamilyMemberId == id)
                .ExecuteDeleteAsync(cancellationToken);

            await db.RecipeRatings
                .Where(r => r.FamilyMemberId == id)
                .ExecuteDeleteAsync(cancellationToken);

            db.FamilyMembers.Remove(member);
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);

            _notifier.NotifyChanged();
            _calendarNotifier?.NotifyChanged();
            _recipeRatingNotifier?.NotifyChanged();
            return member;
        }
        finally
        {
            WriteLock.Release();
        }
    }

    private static string NormalizeName(string? name)
    {
        var trimmed = (name ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(trimmed))
        {
            throw new ArgumentException("Ange ett namn på familjemedlemmen.", nameof(name));
        }

        if (trimmed.Length > FamilyMember.MaxNameLength)
        {
            throw new ArgumentException(
                $"Namnet får vara högst {FamilyMember.MaxNameLength} tecken.",
                nameof(name));
        }

        return trimmed;
    }

    private static void NormalizeActiveSortOrders(List<FamilyMember> activeMembers)
    {
        for (var i = 0; i < activeMembers.Count; i++)
        {
            activeMembers[i].SortOrder = i + 1;
        }
    }

    private static async Task SaveWithUniqueConstraintTranslationAsync(
        AppDbContext db,
        string normalizedName,
        CancellationToken cancellationToken)
    {
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex)
        {
            throw new InvalidOperationException(
                $"Familjemedlemmen \"{normalizedName}\" finns redan.",
                ex);
        }
    }
}
