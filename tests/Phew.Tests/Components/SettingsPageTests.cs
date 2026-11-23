using Bunit;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.FluentUI.AspNetCore.Components;
using Phew.Core.Interfaces;
using Phew.Core.Models;
using Phew.Core.Services;
using Phew.Infrastructure.Data;
using Phew.Infrastructure.Push;
using Phew.Infrastructure.Services;
using Phew.Tests.Infrastructure;
using Phew.Web.Components.Calendar;
using Phew.Web.Components.Layout;
using Phew.Web.Components.Pages;

namespace Phew.Tests.Components;

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

        JSInterop.Setup<CalendarPushSettingsCard.PushEnvironmentState>("phewPush.getEnvironmentState")
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
    public async Task SettingsPage_ShouldDisplayActiveMembersAlphabetically_WithoutReorderOrInactiveSection()
    {
        var (dbFactory, _, service) = SetupServices();
        using var _ = dbFactory;

        await service.CreateAsync("Örjan");
        await service.CreateAsync("Erik");
        await service.CreateAsync("Anna");
        await service.CreateAsync("Åsa");
        var inactive = await service.CreateAsync("InaktivPerson");
        await service.DeactivateAsync(inactive.Id);

        var cut = RenderComponent<SettingsPage>();

        cut.WaitForAssertion(() =>
        {
            var names = cut.FindAll(".settings-member-row .settings-member-name")
                .Select(el => el.TextContent.Trim())
                .ToList();
            names.Should().Equal("Anna", "Erik", "Åsa", "Örjan");
        });

        cut.FindAll(".settings-member-move-btn").Should().BeEmpty();
        cut.FindAll(".settings-inactive-section").Should().BeEmpty();
        cut.Markup.Should().NotContain("InaktivPerson");
    }

    [Fact]
    public async Task SettingsPage_AddRenameAndPermanentDelete_ShouldUseCompactControlsAndConfirmationDialog()
    {
        var (dbFactory, _, service) = SetupServices();
        using var _ = dbFactory;

        var cut = RenderComponent<SettingsPage>();
        cut.WaitForAssertion(() => cut.Find("#settings-new-member-input").Should().NotBeNull());

        // Add button has accessible Swedish label/tooltip and is disabled when input is blank
        var addButton = cut.Find("#settings-add-member-btn");
        addButton.GetAttribute("aria-label").Should().Be("Lägg till familjemedlem");
        addButton.GetAttribute("title").Should().Be("Lägg till familjemedlem");
        addButton.HasAttribute("disabled").Should().BeTrue();

        cut.Find("#settings-new-member-input").Input("   ");
        cut.Find("#settings-add-member-btn").HasAttribute("disabled").Should().BeTrue();

        // Add members
        cut.Find("#settings-new-member-input").Input("Erik");
        cut.Find("#settings-add-member-btn").HasAttribute("disabled").Should().BeFalse();
        cut.Find(".settings-add-member-form").Submit();

        cut.WaitForAssertion(() =>
        {
            cut.FindAll(".settings-member-row").Should().ContainSingle();
            cut.Find(".settings-member-row .settings-member-name").TextContent.Should().Be("Erik");
        });

        // Duplicate case-insensitive error stays inline next to input, not floating
        cut.Find("#settings-new-member-input").Input("erik");
        cut.Find(".settings-add-member-form").Submit();

        cut.WaitForAssertion(() =>
        {
            var inlineError = cut.Find(".settings-family-feedback");
            inlineError.TextContent.Should().Contain("finns redan");
            inlineError.ClassList.Should().NotContain("rb-feedback-floating");
        });

        // Compact row actions: edit and delete icon buttons with accessible labels/tooltips
        var editBtn = cut.Find(".settings-member-edit-btn");
        editBtn.GetAttribute("aria-label").Should().Contain("Erik");
        editBtn.GetAttribute("title").Should().Be("Byt namn");

        var deleteBtn = cut.Find(".settings-member-delete-btn");
        deleteBtn.GetAttribute("aria-label").Should().Contain("Erik");
        deleteBtn.GetAttribute("title").Should().Be("Ta bort");

        // Inline rename validation error stays inline in edit form, then valid rename succeeds
        editBtn.Click();
        await service.CreateAsync("Anna");
        cut.Find(".settings-member-edit-input").Input("anna");
        cut.Find(".settings-member-edit-form").Submit();

        cut.WaitForAssertion(() =>
        {
            cut.Find(".settings-inline-error").TextContent.Should().Contain("Anna");
        });

        cut.Find(".settings-member-edit-input").Input("Beatrice");
        cut.Find(".settings-member-edit-form").Submit();

        cut.WaitForAssertion(() =>
        {
            var names = cut.FindAll(".settings-member-row .settings-member-name")
                .Select(el => el.TextContent.Trim())
                .ToList();
            names.Should().Equal("Anna", "Beatrice");
        });

        // Permanent delete confirmation dialog: cancel preserves member, confirm deletes permanently
        cut.FindAll(".settings-member-delete-btn").Last().Click();
        cut.WaitForAssertion(() => cut.Find(".modal-dialog-custom").Should().NotBeNull());

        var dialogText = cut.Find(".modal-dialog-custom").TextContent;
        dialogText.Should().Contain("permanent");
        dialogText.Should().Contain("kalenderhändelser");
        dialogText.Should().Contain("receptbetyg");
        dialogText.Should().Contain("finns kvar");
        dialogText.Should().Contain("säkerhetskopia");

        // Cancel keeps Beatrice
        cut.Find(".modal-dialog-custom button.btn-secondary-custom").Click();
        cut.WaitForAssertion(() => cut.FindAll(".modal-dialog-custom").Should().BeEmpty());
        (await service.GetAllAsync(includeInactive: true)).Should().HaveCount(2);

        // Confirm permanently removes Beatrice
        cut.FindAll(".settings-member-delete-btn").Last().Click();
        cut.WaitForAssertion(() => cut.Find(".modal-dialog-custom button.btn-danger-custom").Should().NotBeNull());
        cut.Find(".modal-dialog-custom button.btn-danger-custom").Click();

        cut.WaitForAssertion(() =>
        {
            var names = cut.FindAll(".settings-member-row .settings-member-name")
                .Select(el => el.TextContent.Trim())
                .ToList();
            names.Should().Equal("Anna");
        });

        var remaining = await service.GetAllAsync(includeInactive: true);
        remaining.Should().ContainSingle(m => m.Name == "Anna");
    }

    [Fact]
    public void SettingsPage_SuccessFeedback_ShouldRenderAsFloatingToast_AndDismissManually()
    {
        var (dbFactory, _, _) = SetupServices();
        using var _ = dbFactory;

        var cut = RenderComponent<SettingsPage>();
        cut.WaitForAssertion(() => cut.Find("#settings-new-member-input").Should().NotBeNull());

        cut.Find("#settings-new-member-input").Input("Ebba");
        cut.Find(".settings-add-member-form").Submit();

        cut.WaitForAssertion(() =>
        {
            var toast = cut.Find(".settings-success-toast");
            toast.ClassList.Should().Contain("rb-feedback-floating");
            toast.ClassList.Should().Contain("rb-feedback-success");
            toast.GetAttribute("role").Should().Be("status");
            toast.GetAttribute("aria-live").Should().Be("polite");
            toast.TextContent.Should().Contain("Ebba");
            // Toast must not be inside the family card's normal flow
            cut.FindAll(".settings-family-card .settings-success-toast").Should().BeEmpty();
        });

        cut.Find(".settings-success-toast .rb-feedback-close").Click();
        cut.WaitForAssertion(() => cut.FindAll(".settings-success-toast").Should().BeEmpty());
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
        Services.AddSingleton(new FamilyMemberService(dbFactory, new FamilyMemberNotifier(), calendarNotifier: calendarNotifier));
        Services.AddSingleton(weeklyMenuService);
        JSInterop.Mode = JSRuntimeMode.Loose;

        var cut = RenderComponent<CalendarPage>();

        cut.WaitForAssertion(() => cut.Find(".calendar-new-event-btn").Should().NotBeNull());
        cut.FindAll(".calendar-notifications-btn").Should().BeEmpty();
        cut.FindAll(".calendar-push-dialog").Should().BeEmpty();
    }
}
