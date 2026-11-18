using Bunit;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.FluentUI.AspNetCore.Components;
using Receptbanken.Core.Interfaces;
using Receptbanken.Core.Models;
using Receptbanken.Core.Services;
using Receptbanken.Infrastructure.Data;
using Receptbanken.Infrastructure.Push;
using Receptbanken.Infrastructure.Services;
using Receptbanken.Tests.Infrastructure;
using Receptbanken.Web.Components.Calendar;
using Receptbanken.Web.Components.Layout;
using Receptbanken.Web.Components.Pages;

namespace Receptbanken.Tests.Components;

public class SettingsPageTests : TestContext
{
    private sealed class StubVapidKeyProvider : IVapidKeyProvider
    {
        public Task<VapidKeyPair> GetOrCreateKeysAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new VapidKeyPair("BTestVapidPublicKey", "TestPrivateKey", VapidOptions.DefaultSubject));
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

    private sealed class StubLocalDateProvider(DateOnly today) : ILocalDateProvider
    {
        public DateOnly GetLocalToday() => today;
        public void SetClientLocalDate(DateOnly date) { }
    }

    private (TestDbContextFactory DbFactory, FamilyMemberNotifier Notifier, FamilyMemberService Service) SetupServices()
    {
        var dbFactory = new TestDbContextFactory();
        var notifier = new FamilyMemberNotifier();
        var service = new FamilyMemberService(dbFactory, notifier);

        Services.AddFluentUIComponents();
        Services.AddSingleton<IDbContextFactory<AppDbContext>>(dbFactory);
        Services.AddSingleton(notifier);
        Services.AddSingleton(service);
        Services.AddSingleton<IVapidKeyProvider>(new StubVapidKeyProvider());
        Services.AddSingleton<IWebPushClient>(new StubWebPushClient());
        Services.AddSingleton<PushSubscriptionService>();
        JSInterop.Mode = JSRuntimeMode.Loose;

        return (dbFactory, notifier, service);
    }

    [Fact]
    public void NavMenu_ShouldContainInstallningarLinkNearBackup()
    {
        Services.AddFluentUIComponents();
        JSInterop.Mode = JSRuntimeMode.Loose;

        var cut = RenderComponent<NavMenu>();

        var settingsLink = cut.FindAll("a.nav-link-custom")
            .FirstOrDefault(a => a.GetAttribute("href") == "installningar");
        settingsLink.Should().NotBeNull();
        settingsLink!.TextContent.Should().Contain("Inställningar");
    }

    [Fact]
    public void SettingsPage_ShouldRenderPushSettingsCardWithDeviceExplanation()
    {
        var (dbFactory, _, _) = SetupServices();
        using var _ = dbFactory;

        JSInterop.Setup<CalendarPushSettingsCard.PushEnvironmentState>("receptbankenPush.getEnvironmentState")
            .SetResult(new CalendarPushSettingsCard.PushEnvironmentState
            {
                IsSupported = true,
                IsIos = false,
                IsStandalone = false,
                Permission = "default"
            });

        var cut = RenderComponent<SettingsPage>();

        cut.WaitForAssertion(() =>
        {
            cut.FindComponent<CalendarPushSettingsCard>().Should().NotBeNull();
            cut.Markup.Should().Contain("den här enheten och webbläsaren");
            cut.Markup.Should().Contain("Familjemedlemmar");
        });
    }

    [Fact]
    public void SettingsPage_AddRenameDeactivateAndReactivateMember_ShouldUpdateUi()
    {
        var (dbFactory, _, _) = SetupServices();
        using var _ = dbFactory;

        var cut = RenderComponent<SettingsPage>();
        cut.WaitForAssertion(() => cut.Find("#settings-new-member-input").Should().NotBeNull());

        // Add member
        cut.Find("#settings-new-member-input").Input("Ebba");
        cut.Find(".settings-add-member-form").Submit();

        cut.WaitForAssertion(() =>
        {
            cut.FindAll(".settings-member-row:not(.is-inactive)").Should().ContainSingle();
            cut.Markup.Should().Contain("Ebba");
        });

        // Duplicate case-insensitive error
        cut.Find("#settings-new-member-input").Input("ebba");
        cut.Find(".settings-add-member-form").Submit();

        cut.WaitForAssertion(() =>
        {
            cut.Find(".settings-family-feedback").TextContent.Should().Contain("finns redan");
        });

        // Inline rename
        cut.Find(".settings-member-edit-btn").Click();
        cut.Find(".settings-member-edit-input").Input("Ebba K");
        cut.Find(".settings-member-edit-form").Submit();

        cut.WaitForAssertion(() =>
        {
            cut.Find(".settings-member-row:not(.is-inactive) .settings-member-name").TextContent.Should().Be("Ebba K");
        });

        // Deactivate requires confirmation
        cut.Find(".settings-member-deactivate-btn").Click();
        cut.WaitForAssertion(() => cut.Find(".modal-dialog-custom button.btn-danger-custom").Should().NotBeNull());
        cut.Find(".modal-dialog-custom button.btn-danger-custom").Click();

        cut.WaitForAssertion(() =>
        {
            cut.FindAll(".settings-member-row:not(.is-inactive)").Should().BeEmpty();
            cut.Find(".settings-inactive-section").TextContent.Should().Contain("Ebba K");
        });

        // Reactivate
        cut.Find(".settings-member-reactivate-btn").Click();
        cut.WaitForAssertion(() =>
        {
            cut.FindAll(".settings-member-row:not(.is-inactive)").Should().ContainSingle();
            cut.FindAll(".settings-inactive-section").Should().BeEmpty();
        });
    }

    [Fact]
    public async Task SettingsPage_WhenExternalClientChangesMembers_ShouldRefreshAutomatically()
    {
        var (dbFactory, notifier, _) = SetupServices();
        using var _ = dbFactory;

        var cut = RenderComponent<SettingsPage>();
        cut.WaitForAssertion(() => cut.Find("#settings-new-member-input").Should().NotBeNull());

        var otherClientService = new FamilyMemberService(dbFactory, notifier);
        await otherClientService.CreateAsync("FrånAnnanKlient");

        cut.WaitForAssertion(() =>
        {
            cut.Markup.Should().Contain("FrånAnnanKlient");
        });
    }

    [Fact]
    public void CalendarPage_ShouldNotRenderNotificationsButtonOrPushModal()
    {
        var dbFactory = new TestDbContextFactory();
        using var _ = dbFactory;

        var calendarNotifier = new CalendarNotifier();
        var calendarService = new CalendarService(dbFactory, calendarNotifier, NullLogger<CalendarService>.Instance);
        var pantryMatcher = new PantryIngredientMatcher();
        var generator = new WeeklyMenuGenerator(new SystemRandomProvider(), pantryMatcher);
        var weeklyMenuService = new WeeklyMenuService(dbFactory, generator, pantryMatcher, NullLogger<WeeklyMenuService>.Instance);

        Services.AddFluentUIComponents();
        Services.AddSingleton<IDbContextFactory<AppDbContext>>(dbFactory);
        Services.AddSingleton<ILocalDateProvider>(new StubLocalDateProvider(new DateOnly(2026, 11, 18)));
        Services.AddSingleton(calendarNotifier);
        Services.AddSingleton(calendarService);
        Services.AddSingleton(weeklyMenuService);
        JSInterop.Mode = JSRuntimeMode.Loose;

        var cut = RenderComponent<CalendarPage>();

        cut.WaitForAssertion(() => cut.Find(".calendar-new-event-btn").Should().NotBeNull());
        cut.FindAll(".calendar-notifications-btn").Should().BeEmpty();
        cut.FindAll(".calendar-push-dialog").Should().BeEmpty();
    }
}
