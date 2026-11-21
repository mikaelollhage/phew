using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Receptbanken.Core.Models;
using Receptbanken.Core.Services;
using Receptbanken.Infrastructure.Services;

namespace Receptbanken.Tests.Infrastructure;

public class FamilyMemberServiceTests : IDisposable
{
    private readonly TestDbContextFactory _dbFactory;
    private readonly FamilyMemberNotifier _notifier;
    private readonly FamilyMemberService _service;

    public FamilyMemberServiceTests()
    {
        _dbFactory = new TestDbContextFactory();
        _notifier = new FamilyMemberNotifier();
        _service = new FamilyMemberService(_dbFactory, _notifier);
    }

    public void Dispose()
    {
        _dbFactory.Dispose();
    }

    [Fact]
    public async Task CreateAndRename_ShouldTrimAndPreserveStableId()
    {
        var created = await _service.CreateAsync("  Ebba  ");
        created.Id.Should().BeGreaterThan(0);
        created.Name.Should().Be("Ebba");
        created.IsActive.Should().BeTrue();
        created.SortOrder.Should().Be(1);

        var renamed = await _service.RenameAsync(created.Id, "  Ebba K  ");
        renamed.Id.Should().Be(created.Id);
        renamed.Name.Should().Be("Ebba K");

        var all = await _service.GetAllAsync();
        all.Should().ContainSingle(m => m.Id == created.Id && m.Name == "Ebba K");
    }

    [Fact]
    public async Task CreateAsync_WhenDuplicateCaseInsensitive_ShouldThrowAndNotCreateDuplicate()
    {
        await _service.CreateAsync("Ebba");

        var actAscii = () => _service.CreateAsync("ebba");
        await actAscii.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*finns redan*");

        await _service.CreateAsync("Åsa");
        var actSwedish = () => _service.CreateAsync("åsa");
        await actSwedish.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*finns redan*");

        var all = await _service.GetAllAsync();
        all.Should().HaveCount(2);
    }

    [Fact]
    public async Task DatabaseConstraint_ShouldEnforceCaseInsensitiveUniqueness()
    {
        await _service.CreateAsync("Ebba");

        await using var db = _dbFactory.CreateDbContext();
        db.FamilyMembers.Add(new FamilyMember
        {
            Name = "EBBA",
            IsActive = true,
            SortOrder = 2,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        });

        Func<Task> act = () => db.SaveChangesAsync();
        await act.Should().ThrowAsync<DbUpdateException>();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t\n")]
    public async Task CreateAndRename_WhenEmptyOrWhitespace_ShouldThrowArgumentException(string invalidName)
    {
        var createAct = () => _service.CreateAsync(invalidName);
        await createAct.Should().ThrowAsync<ArgumentException>();

        var member = await _service.CreateAsync("Erik");
        var renameAct = () => _service.RenameAsync(member.Id, invalidName);
        await renameAct.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task CreateAsync_WhenExceedsMaxLength_ShouldThrowArgumentException()
    {
        var tooLong = new string('a', FamilyMember.MaxNameLength + 1);
        var act = () => _service.CreateAsync(tooLong);
        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task RenameAsync_AllowCaseOnlyChangeForSameMember()
    {
        var created = await _service.CreateAsync("ebba");
        var renamed = await _service.RenameAsync(created.Id, "Ebba");

        renamed.Id.Should().Be(created.Id);
        renamed.Name.Should().Be("Ebba");
    }

    [Fact]
    public async Task DeactivateAndReactivate_ShouldPreserveRecordAndSuggestReactivationOnCreate()
    {
        var member = await _service.CreateAsync("Anna");
        var deactivated = await _service.DeactivateAsync(member.Id);

        deactivated.Id.Should().Be(member.Id);
        deactivated.IsActive.Should().BeFalse();

        var activeOnly = await _service.GetAllAsync(includeInactive: false);
        activeOnly.Should().BeEmpty();

        var all = await _service.GetAllAsync(includeInactive: true);
        all.Should().ContainSingle(m => m.Id == member.Id && !m.IsActive);

        var createSameName = () => _service.CreateAsync("anna");
        await createSameName.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*inaktiv*Återaktivera*");

        var reactivated = await _service.ReactivateAsync(member.Id);
        reactivated.Id.Should().Be(member.Id);
        reactivated.IsActive.Should().BeTrue();

        (await _service.GetAllAsync(includeInactive: false))
            .Should().ContainSingle(m => m.Id == member.Id && m.IsActive);
    }

    [Fact]
    public async Task MoveAsync_ShouldReorderActiveMembersPredictably()
    {
        var first = await _service.CreateAsync("Anna");
        var second = await _service.CreateAsync("Erik");
        var third = await _service.CreateAsync("Ebba");

        var moved = await _service.MoveAsync(third.Id, -1);
        moved.Should().BeTrue();

        var active = await _service.GetAllAsync(includeInactive: false);
        active.Select(m => m.Name).Should().Equal("Anna", "Ebba", "Erik");
        active.Select(m => m.SortOrder).Should().Equal(1, 2, 3);
    }

    [Fact]
    public async Task Notifier_ShouldIncrementAndRaiseOnChangesOnly()
    {
        var receivedVersions = new List<long>();
        _notifier.OnChanged += v => receivedVersions.Add(v);

        var member = await _service.CreateAsync("Anna");
        await _service.RenameAsync(member.Id, "Anna-Karin");
        await _service.DeactivateAsync(member.Id);
        await _service.ReactivateAsync(member.Id);

        // No-op rename with identical casing/name should not notify
        await _service.RenameAsync(member.Id, "Anna-Karin");

        receivedVersions.Should().Equal(1, 2, 3, 4);
        _notifier.Version.Should().Be(4);
    }

    [Fact]
    public async Task DeleteAsync_ShouldPermanentlyRemoveActiveMember_AndAllowRecreatingName()
    {
        var member = await _service.CreateAsync("Anna");
        var versionBefore = _notifier.Version;

        var deleted = await _service.DeleteAsync(member.Id);

        deleted.Id.Should().Be(member.Id);
        deleted.Name.Should().Be("Anna");
        _notifier.Version.Should().Be(versionBefore + 1);

        var all = await _service.GetAllAsync(includeInactive: true);
        all.Should().BeEmpty();

        var recreated = await _service.CreateAsync("Anna");
        recreated.Id.Should().NotBe(member.Id);
        recreated.Name.Should().Be("Anna");
    }

    [Fact]
    public async Task DeleteAsync_WhenMemberIsInactive_ShouldPermanentlyRemoveRecord()
    {
        var member = await _service.CreateAsync("Erik");
        await _service.DeactivateAsync(member.Id);

        var deleted = await _service.DeleteAsync(member.Id);

        deleted.Id.Should().Be(member.Id);
        deleted.IsActive.Should().BeFalse();
        (await _service.GetAllAsync(includeInactive: true)).Should().BeEmpty();
    }

    [Fact]
    public async Task DeleteAsync_WhenMemberDoesNotExist_ShouldThrowKeyNotFoundAndNotNotify()
    {
        var versionBefore = _notifier.Version;

        var act = () => _service.DeleteAsync(9999);

        await act.Should().ThrowAsync<KeyNotFoundException>();
        _notifier.Version.Should().Be(versionBefore);
    }

    [Fact]
    public async Task DeleteAsync_ShouldLeaveCalendarEventsAndFreeTextNamesIntact()
    {
        var anna = await _service.CreateAsync("Anna");
        await using (var db = _dbFactory.CreateDbContext())
        {
            db.CalendarEvents.Add(new CalendarEvent
            {
                Title = "Fotboll",
                StartDate = new DateOnly(2026, 11, 21),
                IsAllDay = true,
                FamilyMembersCsv = "Anna, Erik",
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow
            });
            await db.SaveChangesAsync();
        }

        await _service.DeleteAsync(anna.Id);

        await using var verifyDb = _dbFactory.CreateDbContext();
        var ev = await verifyDb.CalendarEvents.SingleAsync();
        ev.Title.Should().Be("Fotboll");
        ev.FamilyMembersCsv.Should().Be("Anna, Erik");
    }
}
