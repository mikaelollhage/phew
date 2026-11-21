using Bunit;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.FluentUI.AspNetCore.Components;
using Microsoft.JSInterop;
using Receptbanken.Core.Interfaces;
using Receptbanken.Core.Models;
using Receptbanken.Core.Services;
using Receptbanken.Infrastructure.Data;
using Receptbanken.Infrastructure.Push;
using Receptbanken.Infrastructure.Services;
using Receptbanken.Tests.Infrastructure;
using Receptbanken.Web.Components.Pages;
using Receptbanken.Web.Services;

namespace Receptbanken.Tests.Components;

public class CalendarPageAndHomeTests : TestContext
{
    private sealed class FixedLocalDateProvider : ILocalDateProvider
    {
        private DateOnly _today;
        public FixedLocalDateProvider(DateOnly today) => _today = today;
        public DateOnly GetLocalToday() => _today;
        public void SetClientLocalDate(DateOnly clientDate) => _today = clientDate;
    }

    private sealed class StubVapidKeyProvider : IVapidKeyProvider
    {
        public Task<VapidKeyPair> GetOrCreateKeysAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new VapidKeyPair("BTestPublicKey", "TestPrivateKey", VapidOptions.DefaultSubject));
    }

    private sealed class StubWebPushClient : IWebPushClient
    {
        public Task<WebPushSendResult> SendAsync(
            PushSubscriptionEntity subscription,
            string payloadJson,
            int ttlSeconds = 900,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new WebPushSendResult(true, 201, null, false, false));
    }

    private sealed class ThrowingJsRuntime : IJSRuntime
    {
        private readonly Exception _exception;
        public ThrowingJsRuntime(Exception exception) => _exception = exception;

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) =>
            ValueTask.FromException<TValue>(_exception);

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args) =>
            ValueTask.FromException<TValue>(_exception);
    }

    private sealed class TestBundle : IDisposable
    {
        public required TestDbContextFactory DbFactory { get; init; }
        public required CalendarNotifier Notifier { get; init; }
        public required CalendarService CalendarService { get; init; }
        public required WeeklyMenuService WeeklyMenuService { get; init; }
        public required RecipeService RecipeService { get; init; }
        public required FixedLocalDateProvider DateProvider { get; init; }

        public void Dispose() => DbFactory.Dispose();
    }

    private TestBundle SetupServices(DateOnly localToday, CalendarNotifier? sharedNotifier = null, TestDbContextFactory? sharedDb = null)
    {
        var dbFactory = sharedDb ?? new TestDbContextFactory();
        if (sharedDb == null)
        {
            using var db = dbFactory.CreateDbContext();
            DbInitializer.InitializeAsync(db, NullLogger.Instance).GetAwaiter().GetResult();
        }

        var notifier = sharedNotifier ?? new CalendarNotifier();
        var calendarService = new CalendarService(dbFactory, notifier, NullLogger<CalendarService>.Instance);
        var imageStorage = new MockImageStorageService();
        var recipeService = new RecipeService(dbFactory, imageStorage, NullLogger<RecipeService>.Instance);
        var categoryService = new CategoryService(dbFactory, NullLogger<CategoryService>.Instance);
        var pantryMatcher = new PantryIngredientMatcher();
        var generator = new WeeklyMenuGenerator(new SeededRandomProvider(42), pantryMatcher);
        var weeklyMenuService = new WeeklyMenuService(dbFactory, generator, pantryMatcher, NullLogger<WeeklyMenuService>.Instance);
        var dateProvider = new FixedLocalDateProvider(localToday);

        Services.AddFluentUIComponents();
        Services.AddScoped<AppNotificationService>();
        Services.AddSingleton<IDbContextFactory<AppDbContext>>(dbFactory);
        Services.AddSingleton<IImageStorageService>(imageStorage);
        Services.AddSingleton<ILocalDateProvider>(dateProvider);
        Services.AddSingleton(notifier);
        Services.AddSingleton(calendarService);
        Services.AddSingleton(recipeService);
        Services.AddSingleton(categoryService);
        Services.AddSingleton(weeklyMenuService);
        Services.AddSingleton<IVapidKeyProvider>(new StubVapidKeyProvider());
        Services.AddSingleton<IWebPushClient>(new StubWebPushClient());
        Services.AddSingleton<PushSubscriptionService>();
        JSInterop.Mode = JSRuntimeMode.Loose;

        return new TestBundle
        {
            DbFactory = dbFactory,
            Notifier = notifier,
            CalendarService = calendarService,
            WeeklyMenuService = weeklyMenuService,
            RecipeService = recipeService,
            DateProvider = dateProvider
        };
    }

    [Fact]
    public void CalendarPage_WeekNavigationAndDateQuery_ShouldDisplayRequestedWeekAndNavigate()
    {
        var today = new DateOnly(2026, 11, 11); // Wednesday, Week 46
        using var bundle = SetupServices(today);

        var nav = Services.GetRequiredService<Microsoft.AspNetCore.Components.NavigationManager>();
        nav.NavigateTo("http://localhost/kalender?datum=2026-11-25"); // Week 48

        var cut = RenderComponent<CalendarPage>();

        cut.WaitForAssertion(() =>
        {
            cut.Markup.Should().Contain("Vecka 48, 2026");
            cut.FindAll(".calendar-day-card").Should().HaveCount(7);
            cut.Find(".calendar-today-week-btn").Should().NotBeNull();
        });

        // Navigate to previous week (Week 47)
        cut.Find(".calendar-prev-week-btn").Click();
        cut.WaitForAssertion(() => cut.Markup.Should().Contain("Vecka 47, 2026"));

        // Navigate to next week (Week 48)
        cut.Find(".calendar-next-week-btn").Click();
        cut.WaitForAssertion(() => cut.Markup.Should().Contain("Vecka 48, 2026"));

        // Return to current week (Week 46) and verify today highlight
        cut.Find(".calendar-today-week-btn").Click();
        cut.WaitForAssertion(() =>
        {
            cut.Markup.Should().Contain("Vecka 46, 2026");
            cut.Find(".calendar-day-card[data-date='2026-11-11']").ClassList.Should().Contain("is-today");
            cut.FindAll(".calendar-today-week-btn").Should().BeEmpty();
        });
    }

    [Fact]
    public async Task CalendarPage_ShouldSortAllDayBeforeTimedAndRenderMetadataAndEmptyDays()
    {
        var today = new DateOnly(2026, 11, 9); // Monday, Week 46
        using var bundle = SetupServices(today);

        await bundle.CalendarService.CreateEventAsync(new CalendarEventInput
        {
            Title = "Kvällsträning",
            StartDate = today,
            IsAllDay = false,
            StartTime = new TimeOnly(18, 0),
            EndTime = new TimeOnly(19, 30),
            Location = "Hallen",
            FamilyMembers = ["Erik", "Anna"],
            ReminderMinutesBefore = CalendarReminderOptions.Minutes30
        });

        await bundle.CalendarService.CreateEventAsync(new CalendarEventInput
        {
            Title = "Födelsedag",
            StartDate = today,
            IsAllDay = true,
            ReminderMinutesBefore = CalendarReminderOptions.AllDayPreviousEvening1800,
            RecurrenceIntervalWeeks = 1,
            RecurrenceDaysOfWeekMask = IsoWeekdayMask.Monday
        });

        await bundle.CalendarService.CreateEventAsync(new CalendarEventInput
        {
            Title = "Morgonmöte",
            StartDate = today,
            IsAllDay = false,
            StartTime = new TimeOnly(8, 30)
        });

        var cut = RenderComponent<CalendarPage>();

        cut.WaitForAssertion(() =>
        {
            var mondayCard = cut.Find(".calendar-day-card[data-date='2026-11-09']");
            var titles = mondayCard.QuerySelectorAll(".calendar-event-title").Select(e => e.TextContent.Trim()).ToList();
            titles.Should().Equal("Födelsedag", "Morgonmöte", "Kvällsträning");

            mondayCard.TextContent.Should().Contain("Heldag");
            mondayCard.TextContent.Should().Contain("08:30");
            mondayCard.TextContent.Should().Contain("18:00–19:30");
            // Collapsed state hides location and shows compact person count for 2+ people
            mondayCard.TextContent.Should().NotContain("Hallen");
            mondayCard.QuerySelector(".calendar-event-person-badge.is-count")!.TextContent.Trim().Should().Be("2");
            mondayCard.QuerySelectorAll(".calendar-indicator-badge").Should().NotBeEmpty();
            mondayCard.QuerySelector(".calendar-recurring-indicator svg").Should().NotBeNull();
            mondayCard.TextContent.Should().NotContain("🔁");

            var tuesdayCard = cut.Find(".calendar-day-card[data-date='2026-11-10']");
            tuesdayCard.TextContent.Should().Contain("Inga händelser planerade.");
        });

        // Expanding Kvällsträning reveals location and full member names
        cut.FindAll(".calendar-day-card[data-date='2026-11-09'] .calendar-event-summary-btn").ElementAt(2).Click();
        cut.WaitForAssertion(() =>
        {
            var mondayCard = cut.Find(".calendar-day-card[data-date='2026-11-09']");
            mondayCard.TextContent.Should().Contain("Hallen");
            mondayCard.TextContent.Should().Contain("Erik");
            mondayCard.TextContent.Should().Contain("Anna");
        });
    }

    [Fact]
    public void CalendarPage_CreateEditDeleteOneTimeEvent_AndFormValidation()
    {
        var today = new DateOnly(2026, 11, 10); // Tuesday, Week 46
        using var bundle = SetupServices(today);

        var cut = RenderComponent<CalendarPage>();
        cut.WaitForAssertion(() => cut.FindAll(".calendar-day-card").Should().HaveCount(7));

        // Open quick-add on Tuesday
        cut.Find(".calendar-day-card[data-date='2026-11-10'] .calendar-day-add-btn").Click();
        cut.Find(".calendar-form-dialog").Should().NotBeNull();

        // Submit empty title -> Swedish validation message
        cut.Find("form.calendar-form-body").Submit();
        cut.WaitForAssertion(() =>
            cut.Find(".calendar-form-error").TextContent.Should().Contain("Rubrik måste anges."));

        // Fill valid event with new family member and all-day reminder
        cut.Find("#calendar-event-title").Change("Utflykt");
        cut.Find("#calendar-event-allday").Change(true);
        cut.Find("#calendar-event-location").Change("Skogen");
        cut.Find("#calendar-event-member-input").Input("Lisa");
        cut.Find(".calendar-member-add-btn").Click();
        cut.Find("#calendar-event-reminder").Change(CalendarReminderOptions.AllDaySameDay0900.ToString());
        cut.Find("form.calendar-form-body").Submit();

        cut.WaitForAssertion(() =>
        {
            cut.FindAll(".calendar-form-dialog").Should().BeEmpty();
            var tuesday = cut.Find(".calendar-day-card[data-date='2026-11-10']");
            tuesday.TextContent.Should().Contain("Utflykt");
            tuesday.TextContent.Should().Contain("Lisa");
            tuesday.TextContent.Should().NotContain("Skogen");
        });

        // Expand event to view location and Edit/Delete actions
        cut.Find(".calendar-day-card[data-date='2026-11-10'] .calendar-event-summary-btn").Click();
        cut.WaitForAssertion(() =>
            cut.Find(".calendar-day-card[data-date='2026-11-10']").TextContent.Should().Contain("Skogen"));

        // Edit one-time event from expanded details
        cut.Find(".calendar-day-card[data-date='2026-11-10'] .calendar-event-edit-btn").Click();
        cut.WaitForAssertion(() => cut.Find("#calendar-event-title").GetAttribute("value").Should().Be("Utflykt"));
        cut.Find("#calendar-event-title").Change("Skogsutflykt");
        cut.Find("form.calendar-form-body").Submit();

        cut.WaitForAssertion(() =>
            cut.Find(".calendar-day-card[data-date='2026-11-10']").TextContent.Should().Contain("Skogsutflykt"));

        // Delete one-time event from expanded details (remains expanded across same-week edit)
        cut.Find(".calendar-day-card[data-date='2026-11-10'] .calendar-event-delete-btn").Click();
        cut.WaitForAssertion(() => cut.Find(".modal-dialog-custom").TextContent.Should().Contain("Radera händelse?"));
        cut.Find(".modal-dialog-custom .btn-danger-custom").Click();

        cut.WaitForAssertion(() =>
            cut.Find(".calendar-day-card[data-date='2026-11-10']").TextContent.Should().Contain("Inga händelser planerade."));
    }

    [Fact]
    public async Task CalendarPage_RecurringEvent_SingleOccurrenceMoveAndSeriesEditAndDelete()
    {
        var monday = new DateOnly(2026, 11, 9);
        using var bundle = SetupServices(monday);

        await bundle.CalendarService.CreateEventAsync(new CalendarEventInput
        {
            Title = "Pianolektion",
            StartDate = monday,
            IsAllDay = false,
            StartTime = new TimeOnly(16, 0),
            EndTime = new TimeOnly(16, 45),
            FamilyMembers = ["Maya"],
            RecurrenceIntervalWeeks = 1,
            RecurrenceDaysOfWeekMask = IsoWeekdayMask.Monday | IsoWeekdayMask.Wednesday
        });

        var cut = RenderComponent<CalendarPage>();
        cut.WaitForAssertion(() =>
        {
            cut.Find(".calendar-day-card[data-date='2026-11-09']").TextContent.Should().Contain("Pianolektion");
            cut.Find(".calendar-day-card[data-date='2026-11-11']").TextContent.Should().Contain("Pianolektion");
        });

        // Edit Monday occurrence -> choose "Endast detta tillfälle" and move to Tuesday 2026-11-10
        cut.Find(".calendar-day-card[data-date='2026-11-09'] .calendar-event-summary-btn").Click();
        cut.Find(".calendar-day-card[data-date='2026-11-09'] .calendar-event-edit-btn").Click();
        cut.WaitForAssertion(() => cut.Find(".calendar-scope-dialog").Should().NotBeNull());
        cut.Find(".calendar-scope-single-btn").Click();

        cut.WaitForAssertion(() => cut.Find(".calendar-form-dialog").Should().NotBeNull());
        cut.Find("#calendar-event-title").Change("Pianolektion (flyttad)");
        cut.Find("#calendar-event-date").Change("2026-11-10");
        cut.Find("form.calendar-form-body").Submit();

        cut.WaitForAssertion(() =>
        {
            cut.Find(".calendar-day-card[data-date='2026-11-09']").TextContent.Should().Contain("Inga händelser planerade.");
            cut.Find(".calendar-day-card[data-date='2026-11-10']").TextContent.Should().Contain("Pianolektion (flyttad)");
            cut.Find(".calendar-day-card[data-date='2026-11-11']").TextContent.Should().Contain("Pianolektion");
        });

        // Delete Wednesday occurrence -> choose "Endast detta tillfälle"
        cut.Find(".calendar-day-card[data-date='2026-11-11'] .calendar-event-summary-btn").Click();
        cut.Find(".calendar-day-card[data-date='2026-11-11'] .calendar-event-delete-btn").Click();
        cut.WaitForAssertion(() => cut.Find(".calendar-scope-dialog").Should().NotBeNull());
        cut.Find(".calendar-scope-single-btn").Click();

        cut.WaitForAssertion(() =>
        {
            cut.Find(".calendar-day-card[data-date='2026-11-11']").TextContent.Should().Contain("Inga händelser planerade.");
            cut.Find(".calendar-day-card[data-date='2026-11-10']").TextContent.Should().Contain("Pianolektion (flyttad)");
        });

        // Delete remaining occurrence -> choose "Hela serien"
        var tuesdaySummaryBtn = cut.Find(".calendar-day-card[data-date='2026-11-10'] .calendar-event-summary-btn");
        if (tuesdaySummaryBtn.GetAttribute("aria-expanded") != "true")
        {
            tuesdaySummaryBtn.Click();
        }
        cut.Find(".calendar-day-card[data-date='2026-11-10'] .calendar-event-delete-btn").Click();
        cut.WaitForAssertion(() => cut.Find(".calendar-scope-dialog").Should().NotBeNull());
        cut.Find(".calendar-scope-series-btn").Click();

        cut.WaitForAssertion(() =>
            cut.Find(".calendar-day-card[data-date='2026-11-10']").TextContent.Should().Contain("Inga händelser planerade."));
    }

    [Fact]
    public async Task CalendarPage_ShouldNotRenderDinnerPreviews_EvenWhenWeeklyMenuIsConfirmed()
    {
        var monday = new DateOnly(2026, 11, 9);
        using var bundle = SetupServices(monday);

        var recipe = await bundle.RecipeService.CreateRecipeAsync(new Recipe
        {
            Title = "Korv stroganoff",
            Servings = 4,
            ServingsUnit = "portioner"
        });

        var menu = await bundle.WeeklyMenuService.GetOrCreateWeekMenuAsync(monday);
        menu.Days[0].RecipeId = recipe.Id;
        await bundle.WeeklyMenuService.ConfirmMenuAsync(menu);

        var cut = RenderComponent<CalendarPage>();

        cut.WaitForAssertion(() =>
        {
            var mondayCard = cut.Find(".calendar-day-card[data-date='2026-11-09']");
            mondayCard.QuerySelectorAll(".calendar-dinner-row").Should().BeEmpty();
            mondayCard.TextContent.Should().NotContain("Middag:");
            mondayCard.TextContent.Should().NotContain("Korv stroganoff");
            mondayCard.TextContent.Should().NotContain("Veckomeny");
            mondayCard.QuerySelector($"a[href='/recept/{recipe.Id}']").Should().BeNull();
            mondayCard.QuerySelector("a[href='/veckomeny?datum=2026-11-09']").Should().BeNull();
            mondayCard.TextContent.Should().Contain("Inga händelser planerade.");

            var tuesdayCard = cut.Find(".calendar-day-card[data-date='2026-11-10']");
            tuesdayCard.QuerySelectorAll(".calendar-dinner-row").Should().BeEmpty();
            tuesdayCard.TextContent.Should().Contain("Inga händelser planerade.");
        });
    }

    [Fact]
    public async Task Home_WithAndWithoutCalendarEvents_RendersTodaysDinnerAndCalendarSummary()
    {
        var monday = new DateOnly(2026, 11, 9);
        using var bundle = SetupServices(monday);

        var recipe = await bundle.RecipeService.CreateRecipeAsync(new Recipe
        {
            Title = "Köttfärssås",
            Servings = 4,
            ServingsUnit = "portioner"
        });

        var menu = await bundle.WeeklyMenuService.GetOrCreateWeekMenuAsync(monday);
        menu.Days[0].RecipeId = recipe.Id;
        await bundle.WeeklyMenuService.ConfirmMenuAsync(menu);

        var cut = RenderComponent<Home>();
        cut.WaitForAssertion(() =>
        {
            cut.Markup.Should().Contain("Köttfärssås");
            cut.Find(".home-calendar-section").TextContent.Should().Contain("Inga händelser idag.");
            cut.Find(".home-calendar-section").TextContent.Should().Contain("Inga fler händelser denna vecka.");
            cut.Find("a.home-calendar-open-link").GetAttribute("href").Should().Be("/kalender");
        });

        // Add events for today and later this week -> Home should update via CalendarNotifier
        await bundle.CalendarService.CreateEventAsync(new CalendarEventInput
        {
            Title = "Föräldramöte",
            StartDate = monday,
            IsAllDay = false,
            StartTime = new TimeOnly(18, 30)
        });

        await bundle.CalendarService.CreateEventAsync(new CalendarEventInput
        {
            Title = "Fredagsmys",
            StartDate = monday.AddDays(4),
            IsAllDay = true
        });

        cut.WaitForAssertion(() =>
        {
            var calSection = cut.Find(".home-calendar-section");
            calSection.TextContent.Should().Contain("Föräldramöte");
            calSection.TextContent.Should().Contain("Fredagsmys");
        });
    }

    [Fact]
    public async Task CalendarPage_RealtimeNotifierAndDisposal_UpdatesFromOtherSessionAndUnsubscribesCleanly()
    {
        var monday = new DateOnly(2026, 11, 9);
        using var sharedDb = new TestDbContextFactory();
        await using (var db = sharedDb.CreateDbContext())
        {
            await DbInitializer.InitializeAsync(db, NullLogger.Instance);
        }

        var sharedNotifier = new CalendarNotifier();
        using var bundle = SetupServices(monday, sharedNotifier, sharedDb);

        var cut = RenderComponent<CalendarPage>();
        cut.WaitForAssertion(() => cut.FindAll(".calendar-day-card").Should().HaveCount(7));

        // Simulate another Blazor session creating an event
        var secondService = new CalendarService(sharedDb, sharedNotifier, NullLogger<CalendarService>.Instance);
        await secondService.CreateEventAsync(new CalendarEventInput
        {
            Title = "Synkad händelse",
            StartDate = monday,
            IsAllDay = true
        });

        cut.WaitForAssertion(() =>
            cut.Find(".calendar-day-card[data-date='2026-11-09']").TextContent.Should().Contain("Synkad händelse"));

        // Dispose component and verify subsequent notifications do not throw
        await cut.Instance.DisposeAsync();
        await secondService.CreateEventAsync(new CalendarEventInput
        {
            Title = "Efter dispose",
            StartDate = monday,
            IsAllDay = true
        });
    }

    [Fact]
    public void CalendarPage_WhenJsInteropThrowsDisconnected_ShouldNotCrash()
    {
        var monday = new DateOnly(2026, 11, 9);
        using var bundle = SetupServices(monday);
        Services.AddSingleton<IJSRuntime>(new ThrowingJsRuntime(new JSDisconnectedException("Disconnected")));

        var cut = RenderComponent<CalendarPage>();
        cut.WaitForAssertion(() => cut.Markup.Should().Contain("Familjekalender"));
    }

    [Fact]
    public async Task CalendarPage_ExpandableEventCards_ShouldSupportMultipleExpandedPersonBadgesAndWeekReset()
    {
        var monday = new DateOnly(2026, 11, 9);
        using var bundle = SetupServices(monday);

        await bundle.CalendarService.CreateEventAsync(new CalendarEventInput
        {
            Title = "Innebandy",
            StartDate = monday,
            IsAllDay = false,
            StartTime = new TimeOnly(16, 45),
            EndTime = new TimeOnly(18, 0),
            Notes = "Glöm inte klubba"
        });

        await bundle.CalendarService.CreateEventAsync(new CalendarEventInput
        {
            Title = "Simträning",
            StartDate = monday,
            IsAllDay = false,
            StartTime = new TimeOnly(18, 30),
            EndTime = new TimeOnly(19, 30),
            Location = "Badhuset",
            FamilyMembers = ["Leo"]
        });

        await bundle.CalendarService.CreateEventAsync(new CalendarEventInput
        {
            Title = "Familjemiddag",
            StartDate = monday,
            IsAllDay = false,
            StartTime = new TimeOnly(19, 45),
            FamilyMembers = ["Leo", "Maya", "Erik"],
            RecurrenceIntervalWeeks = 1,
            RecurrenceDaysOfWeekMask = IsoWeekdayMask.Monday | IsoWeekdayMask.Wednesday
        });

        var cut = RenderComponent<CalendarPage>();
        cut.WaitForAssertion(() => cut.FindAll(".calendar-event-item").Should().HaveCount(4));

        var mondayItems = cut.FindAll(".calendar-day-card[data-date='2026-11-09'] .calendar-event-item").ToList();
        mondayItems.Should().HaveCount(3);

        // 0 people -> no person badge; 1 person -> name badge; 3 people -> icon + count
        mondayItems[0].QuerySelectorAll(".calendar-event-person-badge").Should().BeEmpty();
        mondayItems[1].QuerySelector(".calendar-event-person-badge")!.TextContent.Trim().Should().Be("Leo");
        var multiBadge = mondayItems[2].QuerySelector(".calendar-event-person-badge.is-count")!;
        multiBadge.TextContent.Trim().Should().Be("3");
        multiBadge.GetAttribute("title").Should().Be("Leo, Maya, Erik");
        multiBadge.QuerySelector("svg").Should().NotBeNull();

        // All collapsed initially; no legacy expand button, floating menus, or permanently visible edit/delete buttons
        cut.FindAll(".calendar-event-expand-btn").Should().BeEmpty();
        cut.FindAll(".calendar-event-details").Should().BeEmpty();
        cut.FindAll(".calendar-event-edit-btn").Should().BeEmpty();
        cut.FindAll(".calendar-event-delete-btn").Should().BeEmpty();
        cut.FindAll(".calendar-event-menu-popover").Should().BeEmpty();

        // Summary row is a native button with aria-expanded="false" and no nested interactive elements
        var firstSummaryBtn = mondayItems[0].QuerySelector("button.calendar-event-summary-btn[type='button']")!;
        firstSummaryBtn.Should().NotBeNull();
        firstSummaryBtn.GetAttribute("aria-expanded").Should().Be("false");
        firstSummaryBtn.QuerySelectorAll("button, a, input, select, textarea").Should().BeEmpty();
        firstSummaryBtn.QuerySelector(".calendar-event-chevron")!.ClassList.Should().NotContain("is-expanded");

        // Expand first and third event simultaneously by clicking summary row
        cut.FindAll(".calendar-day-card[data-date='2026-11-09'] .calendar-event-summary-btn").ElementAt(0).Click();
        cut.FindAll(".calendar-day-card[data-date='2026-11-09'] .calendar-event-summary-btn").ElementAt(2).Click();

        cut.WaitForAssertion(() =>
        {
            var expanded = cut.FindAll(".calendar-event-item.is-expanded").ToList();
            expanded.Should().HaveCount(2);
            expanded[0].QuerySelector(".calendar-event-summary-btn")!.GetAttribute("aria-expanded").Should().Be("true");
            expanded[0].QuerySelector(".calendar-event-chevron")!.ClassList.Should().Contain("is-expanded");
            expanded[0].TextContent.Should().Contain("Glöm inte klubba");
            expanded[0].QuerySelectorAll(".calendar-event-members").Should().BeEmpty();
            expanded[0].QuerySelector(".calendar-event-edit-btn").Should().NotBeNull();
            expanded[0].QuerySelector(".calendar-event-delete-btn").Should().NotBeNull();

            var multiMembersSection = expanded[1].QuerySelector(".calendar-event-members")!;
            multiMembersSection.Should().NotBeNull();
            multiMembersSection.TextContent.Should().Contain("Leo");
            multiMembersSection.TextContent.Should().Contain("Maya");
            multiMembersSection.TextContent.Should().Contain("Erik");

            // Wednesday occurrence of the same recurring event remains collapsed
            var wednesdayItem = cut.Find(".calendar-day-card[data-date='2026-11-11'] .calendar-event-item");
            wednesdayItem.ClassList.Should().NotContain("is-expanded");
            wednesdayItem.QuerySelector(".calendar-event-summary-btn")!.GetAttribute("aria-expanded").Should().Be("false");
        });

        // Collapse first event via summary row; third stays expanded
        cut.FindAll(".calendar-day-card[data-date='2026-11-09'] .calendar-event-summary-btn").ElementAt(0).Click();
        cut.WaitForAssertion(() =>
        {
            cut.FindAll(".calendar-event-item.is-expanded").Should().HaveCount(1);
            cut.FindAll(".calendar-day-card[data-date='2026-11-09'] .calendar-event-summary-btn").ElementAt(0)
                .GetAttribute("aria-expanded").Should().Be("false");
        });

        // Navigating to another week and back clears expanded state
        cut.Find(".calendar-next-week-btn").Click();
        cut.WaitForAssertion(() => cut.Markup.Should().Contain("Vecka 47, 2026"));
        cut.FindAll(".calendar-event-item.is-expanded").Should().BeEmpty();

        cut.Find(".calendar-prev-week-btn").Click();
        cut.WaitForAssertion(() => cut.Markup.Should().Contain("Vecka 46, 2026"));
        cut.FindAll(".calendar-event-item.is-expanded").Should().BeEmpty();
    }

    [Fact]
    public async Task CalendarPage_SingleParticipantEvent_ShouldShowNameInSummaryOnlyAndNotDuplicateInExpandedDetails()
    {
        var monday = new DateOnly(2026, 11, 9);
        using var bundle = SetupServices(monday);

        await bundle.CalendarService.CreateEventAsync(new CalendarEventInput
        {
            Title = "Simträning",
            StartDate = monday,
            IsAllDay = false,
            StartTime = new TimeOnly(17, 0),
            Location = "Badhuset",
            FamilyMembers = ["Leo"]
        });

        await bundle.CalendarService.CreateEventAsync(new CalendarEventInput
        {
            Title = "Läxläsning",
            StartDate = monday,
            IsAllDay = false,
            StartTime = new TimeOnly(18, 30),
            FamilyMembers = ["Maya"]
        });

        var cut = RenderComponent<CalendarPage>();
        cut.WaitForAssertion(() =>
            cut.FindAll(".calendar-day-card[data-date='2026-11-09'] .calendar-event-item").Should().HaveCount(2));

        // Expand both single-participant events
        cut.FindAll(".calendar-day-card[data-date='2026-11-09'] .calendar-event-summary-btn").ElementAt(0).Click();
        cut.FindAll(".calendar-day-card[data-date='2026-11-09'] .calendar-event-summary-btn").ElementAt(1).Click();

        cut.WaitForAssertion(() =>
        {
            var items = cut.FindAll(".calendar-day-card[data-date='2026-11-09'] .calendar-event-item.is-expanded").ToList();
            items.Should().HaveCount(2);

            // Event with location + 1 participant: name in summary, location in details, no participant section in details
            items[0].QuerySelector(".calendar-event-summary-btn .calendar-event-person-badge")!.TextContent.Trim().Should().Be("Leo");
            var firstDetails = items[0].QuerySelector(".calendar-event-details")!;
            firstDetails.QuerySelector(".calendar-event-location")!.TextContent.Should().Contain("Badhuset");
            firstDetails.QuerySelectorAll(".calendar-event-members").Should().BeEmpty();
            firstDetails.TextContent.Should().NotContain("Leo");
            firstDetails.QuerySelector(".calendar-event-edit-btn").Should().NotBeNull();
            firstDetails.QuerySelector(".calendar-event-delete-btn").Should().NotBeNull();

            // Event with no location/notes + 1 participant: name in summary, no empty meta container in details
            items[1].QuerySelector(".calendar-event-summary-btn .calendar-event-person-badge")!.TextContent.Trim().Should().Be("Maya");
            var secondDetails = items[1].QuerySelector(".calendar-event-details")!;
            secondDetails.QuerySelectorAll(".calendar-event-details-meta").Should().BeEmpty();
            secondDetails.QuerySelectorAll(".calendar-event-members").Should().BeEmpty();
            secondDetails.TextContent.Should().NotContain("Maya");
            secondDetails.QuerySelector(".calendar-event-edit-btn").Should().NotBeNull();
            secondDetails.QuerySelector(".calendar-event-delete-btn").Should().NotBeNull();
        });
    }
}
