using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Receptbanken.Core.Models;
using Receptbanken.Core.Services;
using Receptbanken.Infrastructure.Services;

namespace Receptbanken.Tests.Infrastructure;

public class CalendarServiceTests : IDisposable
{
    private readonly TestDbContextFactory _dbFactory;
    private readonly CalendarNotifier _notifier;
    private readonly FamilyMemberNotifier _familyMemberNotifier;
    private readonly FamilyMemberService _familyMemberService;
    private readonly CalendarService _service;

    public CalendarServiceTests()
    {
        _dbFactory = new TestDbContextFactory();
        _notifier = new CalendarNotifier();
        _familyMemberNotifier = new FamilyMemberNotifier();
        _familyMemberService = new FamilyMemberService(_dbFactory, _familyMemberNotifier, calendarNotifier: _notifier);
        _service = new CalendarService(_dbFactory, _notifier, NullLogger<CalendarService>.Instance);
    }

    public void Dispose()
    {
        _dbFactory.Dispose();
    }

    [Fact]
    public async Task CreateAndGetWeekOccurrences_OneTimeAndAllDayEvents_ShouldPersistAndNotify()
    {
        var anna = await _familyMemberService.CreateAsync("Anna");
        long? notifiedVersion = null;
        _notifier.OnChanged += v => notifiedVersion = v;

        var timed = await _service.CreateEventAsync(new CalendarEventInput
        {
            Title = "Läkarbesök",
            StartDate = new DateOnly(2026, 11, 18),
            IsAllDay = false,
            StartTime = new TimeOnly(10, 30),
            EndTime = new TimeOnly(11, 15),
            Location = "Vårdcentralen",
            Notes = "Ta med legitimation",
            ParticipantMemberIds = [anna.Id],
            ReminderMinutesBefore = CalendarReminderOptions.Hour1
        });

        var allDay = await _service.CreateEventAsync(new CalendarEventInput
        {
            Title = "Planeringsdag",
            StartDate = new DateOnly(2026, 11, 18),
            IsAllDay = true,
            ReminderMinutesBefore = CalendarReminderOptions.AllDayPreviousEvening1800
        });

        notifiedVersion.Should().Be(2);
        timed.Id.Should().BeGreaterThan(0);
        allDay.Id.Should().BeGreaterThan(0);

        var weekOccurrences = await _service.GetWeekOccurrencesAsync(new DateOnly(2026, 11, 18));
        weekOccurrences.Should().HaveCount(2);
        weekOccurrences[0].Title.Should().Be("Planeringsdag");
        weekOccurrences[0].IsAllDay.Should().BeTrue();
        weekOccurrences[0].ReminderMinutesBefore.Should().Be(CalendarReminderOptions.AllDayPreviousEvening1800);

        weekOccurrences[1].Title.Should().Be("Läkarbesök");
        weekOccurrences[1].StartTime.Should().Be(new TimeOnly(10, 30));
        weekOccurrences[1].EndTime.Should().Be(new TimeOnly(11, 15));
        weekOccurrences[1].Location.Should().Be("Vårdcentralen");
        weekOccurrences[1].FamilyMembers.Should().Equal("Anna");
        weekOccurrences[1].ReminderMinutesBefore.Should().Be(CalendarReminderOptions.Hour1);
    }

    [Fact]
    public async Task SingleOccurrenceEditMoveAndDelete_ShouldOnlyAffectTargetedOccurrence()
    {
        var erik = await _familyMemberService.CreateAsync("Erik");
        var mikael = await _familyMemberService.CreateAsync("Mikael");

        var series = await _service.CreateEventAsync(new CalendarEventInput
        {
            Title = "Fotbollsträning",
            StartDate = new DateOnly(2026, 11, 9), // Monday W46
            IsAllDay = false,
            StartTime = new TimeOnly(18, 0),
            EndTime = new TimeOnly(19, 30),
            Location = "IP Skogen",
            ParticipantMemberIds = [erik.Id],
            ReminderMinutesBefore = CalendarReminderOptions.Minutes30,
            RecurrenceIntervalWeeks = 1,
            RecurrenceDaysOfWeekMask = IsoWeekdayMask.Monday
        });

        // Move W47 occurrence (Nov 16) to Sunday W46 (Nov 15) with modified title & cleared reminder
        await _service.UpdateSingleOccurrenceAsync(
            series.Id,
            new DateOnly(2026, 11, 16),
            new CalendarOccurrenceOverrideInput
            {
                Title = "Match istället för träning",
                Date = new DateOnly(2026, 11, 15),
                IsAllDay = false,
                StartTime = new TimeOnly(14, 0),
                EndTime = new TimeOnly(16, 0),
                Location = "Bortaplan",
                Notes = "Samling 13:15",
                ParticipantMemberIds = [erik.Id, mikael.Id],
                ReminderMinutesBefore = null
            });

        // Cancel W48 occurrence (Nov 23)
        await _service.DeleteSingleOccurrenceAsync(series.Id, new DateOnly(2026, 11, 23));

        var occurrences = await _service.GetOccurrencesAsync(
            new DateOnly(2026, 11, 9),
            new DateOnly(2026, 12, 6));

        occurrences.Select(o => (o.Date, o.Title, o.IsMoved)).Should().Equal(
            (new DateOnly(2026, 11, 9), "Fotbollsträning", false),
            (new DateOnly(2026, 11, 15), "Match istället för träning", true),
            (new DateOnly(2026, 11, 30), "Fotbollsträning", false));

        var moved = occurrences[1];
        moved.OriginalDate.Should().Be(new DateOnly(2026, 11, 16));
        moved.ReminderMinutesBefore.Should().BeNull();
        moved.FamilyMembers.Should().Equal("Erik", "Mikael");
    }

    [Fact]
    public async Task UpdateEntireSeries_WhenRecurrenceRuleChanges_ShouldRemoveOrphanedExceptions()
    {
        var series = await _service.CreateEventAsync(new CalendarEventInput
        {
            Title = "Gympa",
            StartDate = new DateOnly(2026, 11, 9), // Monday
            IsAllDay = false,
            StartTime = new TimeOnly(17, 0),
            RecurrenceIntervalWeeks = 1,
            RecurrenceDaysOfWeekMask = IsoWeekdayMask.Monday | IsoWeekdayMask.Wednesday
        });

        // Exception on Monday 2026-11-16 and Wednesday 2026-11-18
        await _service.DeleteSingleOccurrenceAsync(series.Id, new DateOnly(2026, 11, 16));
        await _service.DeleteSingleOccurrenceAsync(series.Id, new DateOnly(2026, 11, 18));

        // Update series to Wednesdays only, every 2 weeks starting 2026-11-18
        await _service.UpdateEventAsync(series.Id, new CalendarEventInput
        {
            Title = "Gympa (uppdaterad)",
            StartDate = new DateOnly(2026, 11, 18),
            IsAllDay = false,
            StartTime = new TimeOnly(17, 30),
            RecurrenceIntervalWeeks = 2,
            RecurrenceDaysOfWeekMask = IsoWeekdayMask.Wednesday
        });

        var loaded = await _service.GetEventByIdAsync(series.Id);
        loaded.Should().NotBeNull();
        // Monday 2026-11-16 is no longer valid and must be removed; Wednesday 2026-11-18 remains valid.
        loaded!.Exceptions.Should().ContainSingle(ex => ex.OriginalDate == new DateOnly(2026, 11, 18));
    }

    [Fact]
    public async Task DeleteEntireSeries_ShouldCascadeDeleteExceptions()
    {
        var series = await _service.CreateEventAsync(new CalendarEventInput
        {
            Title = "Körsång",
            StartDate = new DateOnly(2026, 11, 12),
            IsAllDay = false,
            StartTime = new TimeOnly(19, 0),
            RecurrenceIntervalWeeks = 1,
            RecurrenceDaysOfWeekMask = IsoWeekdayMask.Thursday
        });

        await _service.DeleteSingleOccurrenceAsync(series.Id, new DateOnly(2026, 11, 19));

        var deleted = await _service.DeleteEventAsync(series.Id);
        deleted.Should().BeTrue();

        await using var db = _dbFactory.CreateDbContext();
        (await db.CalendarEvents.CountAsync()).Should().Be(0);
        (await db.CalendarEventExceptions.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task Participants_ZeroOneMultiple_Rename_PermanentDelete_AndInvalidId_ShouldBehaveCorrectly()
    {
        var anna = await _familyMemberService.CreateAsync("Anna");
        var erik = await _familyMemberService.CreateAsync("Erik");

        // Zero participants
        var evZero = await _service.CreateEventAsync(new CalendarEventInput
        {
            Title = "Ingen deltagare",
            StartDate = new DateOnly(2026, 11, 10),
            IsAllDay = true,
            ParticipantMemberIds = []
        });

        // Multiple participants (with duplicate ID in input)
        var evMulti = await _service.CreateEventAsync(new CalendarEventInput
        {
            Title = "Flera deltagare",
            StartDate = new DateOnly(2026, 11, 11),
            IsAllDay = true,
            ParticipantMemberIds = [anna.Id, erik.Id, anna.Id]
        });

        // Invalid participant ID should throw
        await Assert.ThrowsAsync<ArgumentException>(() => _service.CreateEventAsync(new CalendarEventInput
        {
            Title = "Ogiltig",
            StartDate = new DateOnly(2026, 11, 12),
            IsAllDay = true,
            ParticipantMemberIds = [9999]
        }));

        var initial = await _service.GetWeekOccurrencesAsync(new DateOnly(2026, 11, 10));
        initial.Single(o => o.CalendarEventId == evZero.Id).FamilyMembers.Should().BeEmpty();
        initial.Single(o => o.CalendarEventId == evMulti.Id).FamilyMembers.Should().Equal("Anna", "Erik");
        initial.Single(o => o.CalendarEventId == evMulti.Id).ParticipantMemberIds.Should().Equal(anna.Id, erik.Id);

        // Rename Anna -> Annika without touching calendar events
        await _familyMemberService.RenameAsync(anna.Id, "Annika");
        var afterRename = await _service.GetWeekOccurrencesAsync(new DateOnly(2026, 11, 10));
        afterRename.Single(o => o.CalendarEventId == evMulti.Id).FamilyMembers.Should().Equal("Annika", "Erik");

        // Permanently delete Erik -> removes association, keeps event & Annika
        await _familyMemberService.DeleteAsync(erik.Id);
        var afterDelete = await _service.GetWeekOccurrencesAsync(new DateOnly(2026, 11, 10));
        var multiOccurrence = afterDelete.Single(o => o.CalendarEventId == evMulti.Id);
        multiOccurrence.FamilyMembers.Should().Equal("Annika");
        multiOccurrence.ParticipantMemberIds.Should().Equal(anna.Id);
    }

    [Fact]
    public async Task RecurringSeries_ParticipantInheritance_And_ExplicitZeroOverride_ShouldWork()
    {
        var anna = await _familyMemberService.CreateAsync("Anna");
        var erik = await _familyMemberService.CreateAsync("Erik");

        var series = await _service.CreateEventAsync(new CalendarEventInput
        {
            Title = "Veckomöte",
            StartDate = new DateOnly(2026, 11, 9),
            IsAllDay = true,
            ParticipantMemberIds = [anna.Id],
            RecurrenceIntervalWeeks = 1,
            RecurrenceDaysOfWeekMask = IsoWeekdayMask.Monday
        });

        // Override W47 (Nov 16) to explicitly have 0 participants
        await _service.UpdateSingleOccurrenceAsync(
            series.Id,
            new DateOnly(2026, 11, 16),
            new CalendarOccurrenceOverrideInput
            {
                Title = "Veckomöte (ingen deltagare)",
                Date = new DateOnly(2026, 11, 16),
                IsAllDay = true,
                ParticipantMemberIds = []
            });

        // Update series participants to [anna, erik] -> W46 and W48 inherit, W47 stays empty
        await _service.UpdateEventAsync(series.Id, new CalendarEventInput
        {
            Title = "Veckomöte",
            StartDate = new DateOnly(2026, 11, 9),
            IsAllDay = true,
            ParticipantMemberIds = [anna.Id, erik.Id],
            RecurrenceIntervalWeeks = 1,
            RecurrenceDaysOfWeekMask = IsoWeekdayMask.Monday
        });

        var occurrences = await _service.GetOccurrencesAsync(new DateOnly(2026, 11, 9), new DateOnly(2026, 11, 23));
        occurrences.Should().HaveCount(3);
        occurrences[0].FamilyMembers.Should().Equal("Anna", "Erik");
        occurrences[1].FamilyMembers.Should().BeEmpty();
        occurrences[1].ParticipantMemberIds.Should().BeEmpty();
        occurrences[2].FamilyMembers.Should().Equal("Anna", "Erik");
    }

    [Fact]
    public async Task ValidationAndFailedOperations_ShouldThrowAndNotNotify()
    {
        long notificationCount = 0;
        _notifier.OnChanged += _ => notificationCount++;

        // Empty title
        await Assert.ThrowsAsync<ArgumentException>(() => _service.CreateEventAsync(new CalendarEventInput
        {
            Title = "   ",
            StartDate = new DateOnly(2026, 11, 10),
            IsAllDay = true
        }));

        // All-day with StartTime
        await Assert.ThrowsAsync<ArgumentException>(() => _service.CreateEventAsync(new CalendarEventInput
        {
            Title = "Ogiltig heldag",
            StartDate = new DateOnly(2026, 11, 10),
            IsAllDay = true,
            StartTime = new TimeOnly(9, 0)
        }));

        // Timed without StartTime
        await Assert.ThrowsAsync<ArgumentException>(() => _service.CreateEventAsync(new CalendarEventInput
        {
            Title = "Saknar starttid",
            StartDate = new DateOnly(2026, 11, 10),
            IsAllDay = false
        }));

        // EndTime <= StartTime
        await Assert.ThrowsAsync<ArgumentException>(() => _service.CreateEventAsync(new CalendarEventInput
        {
            Title = "Fel sluttid",
            StartDate = new DateOnly(2026, 11, 10),
            IsAllDay = false,
            StartTime = new TimeOnly(18, 0),
            EndTime = new TimeOnly(17, 0)
        }));

        // Invalid all-day reminder value (15 min is only for timed)
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => _service.CreateEventAsync(new CalendarEventInput
        {
            Title = "Fel påminnelse",
            StartDate = new DateOnly(2026, 11, 10),
            IsAllDay = true,
            ReminderMinutesBefore = 15
        }));

        // Recurrence interval > 52
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => _service.CreateEventAsync(new CalendarEventInput
        {
            Title = "Ogiltigt intervall",
            StartDate = new DateOnly(2026, 11, 10),
            IsAllDay = true,
            RecurrenceIntervalWeeks = 53,
            RecurrenceDaysOfWeekMask = IsoWeekdayMask.Tuesday
        }));

        // Recurring without weekday mask
        await Assert.ThrowsAsync<ArgumentException>(() => _service.CreateEventAsync(new CalendarEventInput
        {
            Title = "Saknar veckodag",
            StartDate = new DateOnly(2026, 11, 10),
            IsAllDay = true,
            RecurrenceIntervalWeeks = 1,
            RecurrenceDaysOfWeekMask = IsoWeekdayMask.None
        }));

        // RecurrenceEndDate < StartDate
        await Assert.ThrowsAsync<ArgumentException>(() => _service.CreateEventAsync(new CalendarEventInput
        {
            Title = "Slutdatum före start",
            StartDate = new DateOnly(2026, 11, 10),
            IsAllDay = true,
            RecurrenceIntervalWeeks = 1,
            RecurrenceDaysOfWeekMask = IsoWeekdayMask.Tuesday,
            RecurrenceEndDate = new DateOnly(2026, 11, 9)
        }));

        // Delete non-existent event
        var deleted = await _service.DeleteEventAsync(9999);
        deleted.Should().BeFalse();

        // Update non-existent event
        await Assert.ThrowsAsync<KeyNotFoundException>(() => _service.UpdateEventAsync(9999, new CalendarEventInput
        {
            Title = "Finns ej",
            StartDate = new DateOnly(2026, 11, 10),
            IsAllDay = true
        }));

        notificationCount.Should().Be(0);
    }

    [Fact]
    public async Task DatabaseConstraints_ShouldRejectInvalidIntervalAndDuplicateExceptionOriginalDate()
    {
        await using var db = _dbFactory.CreateDbContext();

        db.CalendarEvents.Add(new CalendarEvent
        {
            Title = "Ogiltig i DB",
            StartDate = new DateOnly(2026, 11, 10),
            IsAllDay = true,
            RecurrenceIntervalWeeks = 99
        });

        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        db.ChangeTracker.Clear();

        var validEvent = new CalendarEvent
        {
            Title = "Giltig serie",
            StartDate = new DateOnly(2026, 11, 10),
            IsAllDay = true,
            RecurrenceIntervalWeeks = 1,
            RecurrenceDaysOfWeekMask = (int)IsoWeekdayMask.Tuesday
        };
        db.CalendarEvents.Add(validEvent);
        await db.SaveChangesAsync();

        db.CalendarEventExceptions.Add(new CalendarEventException
        {
            CalendarEventId = validEvent.Id,
            OriginalDate = new DateOnly(2026, 11, 17),
            IsCancelled = true
        });
        await db.SaveChangesAsync();

        db.CalendarEventExceptions.Add(new CalendarEventException
        {
            CalendarEventId = validEvent.Id,
            OriginalDate = new DateOnly(2026, 11, 17),
            IsCancelled = false,
            OverrideTitle = "Dubblett"
        });

        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }

    [Fact]
    public async Task GetOccurrencesAsync_MovedOccurrencePastRecurrenceEndDate_ShouldBeReturnedInTargetRangeOnly()
    {
        var series = await _service.CreateEventAsync(new CalendarEventInput
        {
            Title = "Bokklubb",
            StartDate = new DateOnly(2026, 11, 11), // Wednesday
            IsAllDay = true,
            RecurrenceIntervalWeeks = 1,
            RecurrenceDaysOfWeekMask = IsoWeekdayMask.Wednesday,
            RecurrenceEndDate = new DateOnly(2026, 11, 25)
        });

        // Move the final occurrence (Nov 25) past RecurrenceEndDate into December (Dec 2)
        await _service.UpdateSingleOccurrenceAsync(
            series.Id,
            new DateOnly(2026, 11, 25),
            new CalendarOccurrenceOverrideInput
            {
                Title = "Bokklubb (flyttad)",
                Date = new DateOnly(2026, 12, 2),
                IsAllDay = true
            });

        var novemberEndWeek = await _service.GetOccurrencesAsync(
            new DateOnly(2026, 11, 23),
            new DateOnly(2026, 11, 29));
        novemberEndWeek.Should().BeEmpty();

        var decemberFirstWeek = await _service.GetOccurrencesAsync(
            new DateOnly(2026, 11, 30),
            new DateOnly(2026, 12, 6));
        decemberFirstWeek.Should().ContainSingle();
        decemberFirstWeek[0].Date.Should().Be(new DateOnly(2026, 12, 2));
        decemberFirstWeek[0].OriginalDate.Should().Be(new DateOnly(2026, 11, 25));
        decemberFirstWeek[0].IsMoved.Should().BeTrue();
        decemberFirstWeek[0].Title.Should().Be("Bokklubb (flyttad)");
    }
}
