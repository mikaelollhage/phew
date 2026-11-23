using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using Phew.Core.Models;

namespace Phew.Infrastructure.Data;

public class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options)
    {
    }

    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
    {
        base.OnConfiguring(optionsBuilder);
        optionsBuilder.ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning));
    }

    public DbSet<Recipe> Recipes => Set<Recipe>();
    public DbSet<Ingredient> Ingredients => Set<Ingredient>();
    public DbSet<InstructionStep> Instructions => Set<InstructionStep>();
    public DbSet<Category> Categories => Set<Category>();
    public DbSet<RecipeCategory> RecipeCategories => Set<RecipeCategory>();
    public DbSet<ShoppingListItem> ShoppingListItems => Set<ShoppingListItem>();
    public DbSet<WeeklyMenu> WeeklyMenus => Set<WeeklyMenu>();
    public DbSet<WeeklyMenuDay> WeeklyMenuDays => Set<WeeklyMenuDay>();
    public DbSet<WeeklyMenuDayCategory> WeeklyMenuDayCategories => Set<WeeklyMenuDayCategory>();
    public DbSet<WeeklyMenuPantryItem> WeeklyMenuPantryItems => Set<WeeklyMenuPantryItem>();
    public DbSet<WeeklyMenuShoppingContribution> WeeklyMenuShoppingContributions => Set<WeeklyMenuShoppingContribution>();
    public DbSet<CalendarEvent> CalendarEvents => Set<CalendarEvent>();
    public DbSet<CalendarEventException> CalendarEventExceptions => Set<CalendarEventException>();
    public DbSet<CalendarEventParticipant> CalendarEventParticipants => Set<CalendarEventParticipant>();
    public DbSet<CalendarEventExceptionParticipant> CalendarEventExceptionParticipants => Set<CalendarEventExceptionParticipant>();
    public DbSet<PushSubscriptionEntity> PushSubscriptions => Set<PushSubscriptionEntity>();
    public DbSet<CalendarReminderDelivery> CalendarReminderDeliveries => Set<CalendarReminderDelivery>();
    public DbSet<CalendarReminderSystemState> CalendarReminderSystemStates => Set<CalendarReminderSystemState>();
    public DbSet<TodoList> TodoLists => Set<TodoList>();
    public DbSet<TodoItem> TodoItems => Set<TodoItem>();
    public DbSet<FamilyMember> FamilyMembers => Set<FamilyMember>();
    public DbSet<RecipeRating> RecipeRatings => Set<RecipeRating>();

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        // SQLite cannot ORDER BY DateTimeOffset. Store as UTC ticks (INTEGER) so that
        // sorting and comparisons are translated server-side and remain correct.
        configurationBuilder.Properties<DateTimeOffset>()
            .HaveConversion<UtcTicksDateTimeOffsetConverter>();
    }

    private sealed class UtcTicksDateTimeOffsetConverter : ValueConverter<DateTimeOffset, long>
    {
        public UtcTicksDateTimeOffsetConverter()
            : base(
                v => v.UtcTicks,
                v => new DateTimeOffset(v, TimeSpan.Zero))
        {
        }
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        // Recipe
        modelBuilder.Entity<Recipe>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Title).IsRequired().HasMaxLength(200);
            entity.Property(e => e.SourceUrl).HasMaxLength(2000);
            entity.Property(e => e.SourceHost).HasMaxLength(100);
            entity.Property(e => e.ImagePath).HasMaxLength(500);
            entity.Property(e => e.ServingsUnit).IsRequired().HasMaxLength(50);
            entity.HasIndex(e => e.Title);
            entity.HasIndex(e => e.IsFavorite);

            entity.HasMany(e => e.Ingredients)
                .WithOne(e => e.Recipe)
                .HasForeignKey(e => e.RecipeId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasMany(e => e.Instructions)
                .WithOne(e => e.Recipe)
                .HasForeignKey(e => e.RecipeId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        // Ingredient
        modelBuilder.Entity<Ingredient>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.RawText).IsRequired();
            entity.Property(e => e.Name).IsRequired().HasMaxLength(200);
            entity.Property(e => e.Unit).HasMaxLength(30);
            entity.Property(e => e.Comment).HasMaxLength(200);
            entity.Property(e => e.Preparation).HasMaxLength(200);
            entity.Property(e => e.GroupHeader).HasMaxLength(100);
            entity.Property(e => e.Quantity).HasPrecision(10, 2);
            entity.Property(e => e.QuantityMax).HasPrecision(10, 2);
        });

        // InstructionStep
        modelBuilder.Entity<InstructionStep>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.InstructionText).IsRequired();
            entity.Property(e => e.GroupHeader).HasMaxLength(100);
        });

        // Category
        modelBuilder.Entity<Category>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Name).IsRequired().HasMaxLength(100);
            entity.Property(e => e.ColorHex).HasMaxLength(9);
            entity.HasIndex(e => e.Name).IsUnique();
        });

        // RecipeCategory (Join Table)
        modelBuilder.Entity<RecipeCategory>(entity =>
        {
            entity.HasKey(rc => new { rc.RecipeId, rc.CategoryId });

            entity.HasOne(rc => rc.Recipe)
                .WithMany(r => r.RecipeCategories)
                .HasForeignKey(rc => rc.RecipeId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne(rc => rc.Category)
                .WithMany(c => c.RecipeCategories)
                .HasForeignKey(rc => rc.CategoryId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        // ShoppingListItem
        modelBuilder.Entity<ShoppingListItem>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.RawText).IsRequired();
            entity.Property(e => e.Name).IsRequired().HasMaxLength(200);
            entity.Property(e => e.Unit).HasMaxLength(30);
            entity.Property(e => e.Comment).HasMaxLength(500);
            entity.Property(e => e.Department).HasMaxLength(100);
            entity.Property(e => e.Quantity).HasPrecision(10, 2);
            entity.Property(e => e.QuantityMax).HasPrecision(10, 2);
            entity.Property(e => e.SortOrder).HasDefaultValue(0);

            entity.HasOne(e => e.Recipe)
                .WithMany()
                .HasForeignKey(e => e.RecipeId)
                .OnDelete(DeleteBehavior.SetNull);
        });

        // WeeklyMenu
        modelBuilder.Entity<WeeklyMenu>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => e.WeekStartDate).IsUnique();
            entity.HasIndex(e => new { e.IsoYear, e.IsoWeek }).IsUnique();
            entity.HasIndex(e => e.Status);

            entity.HasMany(e => e.Days)
                .WithOne(d => d.WeeklyMenu)
                .HasForeignKey(d => d.WeeklyMenuId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasMany(e => e.PantryItems)
                .WithOne(p => p.WeeklyMenu)
                .HasForeignKey(p => p.WeeklyMenuId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasMany(e => e.ShoppingContributions)
                .WithOne(c => c.WeeklyMenu)
                .HasForeignKey(c => c.WeeklyMenuId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        // WeeklyMenuDay
        modelBuilder.Entity<WeeklyMenuDay>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => new { e.WeeklyMenuId, e.DayIndex }).IsUnique();
            entity.HasIndex(e => new { e.WeeklyMenuId, e.Date }).IsUnique();

            entity.HasOne(e => e.Recipe)
                .WithMany()
                .HasForeignKey(e => e.RecipeId)
                .OnDelete(DeleteBehavior.SetNull);
        });

        // WeeklyMenuDayCategory (Join Table)
        modelBuilder.Entity<WeeklyMenuDayCategory>(entity =>
        {
            entity.HasKey(dc => new { dc.WeeklyMenuDayId, dc.CategoryId });

            entity.HasOne(dc => dc.WeeklyMenuDay)
                .WithMany(d => d.SelectedCategories)
                .HasForeignKey(dc => dc.WeeklyMenuDayId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne(dc => dc.Category)
                .WithMany()
                .HasForeignKey(dc => dc.CategoryId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        // WeeklyMenuPantryItem
        modelBuilder.Entity<WeeklyMenuPantryItem>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.IngredientName).IsRequired().HasMaxLength(150);
        });

        // WeeklyMenuShoppingContribution
        modelBuilder.Entity<WeeklyMenuShoppingContribution>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.ContributionKey).IsRequired().HasMaxLength(250);
            entity.Property(e => e.Unit).HasMaxLength(30);
            entity.Property(e => e.AddedQuantity).HasPrecision(10, 2);
            entity.Property(e => e.AddedQuantityMax).HasPrecision(10, 2);
            entity.HasIndex(e => new { e.WeeklyMenuId, e.DayIndex, e.RecipeId, e.ContributionKey }).IsUnique();
            entity.HasIndex(e => e.WeekStartDate);
        });

        // CalendarEvent
        modelBuilder.Entity<CalendarEvent>(entity =>
        {
            entity.ToTable(t =>
            {
                t.HasCheckConstraint(
                    "CK_CalendarEvents_RecurrenceIntervalWeeks",
                    "RecurrenceIntervalWeeks >= 0 AND RecurrenceIntervalWeeks <= 52");
                t.HasCheckConstraint(
                    "CK_CalendarEvents_AllDayTimes",
                    "(IsAllDay = 1 AND StartTime IS NULL AND EndTime IS NULL) OR (IsAllDay = 0 AND StartTime IS NOT NULL)");
            });

            entity.HasKey(e => e.Id);
            entity.Property(e => e.Title).IsRequired().HasMaxLength(CalendarEvent.MaxTitleLength);
            entity.Property(e => e.Location).HasMaxLength(CalendarEvent.MaxLocationLength);
            entity.Property(e => e.Notes).HasMaxLength(CalendarEvent.MaxNotesLength);
            entity.Property(e => e.FamilyMembersCsv).HasMaxLength(CalendarEvent.MaxFamilyMembersCsvLength);
            entity.Property(e => e.IsAllDay).HasDefaultValue(false);
            entity.Property(e => e.RecurrenceIntervalWeeks).HasDefaultValue(0);
            entity.Property(e => e.RecurrenceDaysOfWeekMask).HasDefaultValue(0);

            entity.HasIndex(e => new { e.StartDate, e.RecurrenceEndDate })
                .HasDatabaseName("IX_CalendarEvents_StartDate_RecurrenceEndDate");
            entity.HasIndex(e => e.RecurrenceIntervalWeeks)
                .HasDatabaseName("IX_CalendarEvents_RecurrenceIntervalWeeks");

            entity.HasMany(e => e.Exceptions)
                .WithOne(ex => ex.CalendarEvent)
                .HasForeignKey(ex => ex.CalendarEventId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        // CalendarEventException
        modelBuilder.Entity<CalendarEventException>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.OverrideTitle).HasMaxLength(CalendarEvent.MaxTitleLength);
            entity.Property(e => e.OverrideLocation).HasMaxLength(CalendarEvent.MaxLocationLength);
            entity.Property(e => e.OverrideNotes).HasMaxLength(CalendarEvent.MaxNotesLength);
            entity.Property(e => e.OverrideFamilyMembersCsv).HasMaxLength(CalendarEvent.MaxFamilyMembersCsvLength);
            entity.Property(e => e.IsCancelled).HasDefaultValue(false);
            entity.Property(e => e.OverrideReminderCleared).HasDefaultValue(false);
            entity.Property(e => e.OverrideParticipants).HasDefaultValue(false);

            entity.HasIndex(e => new { e.CalendarEventId, e.OriginalDate })
                .IsUnique()
                .HasDatabaseName("IX_CalendarEventExceptions_CalendarEventId_OriginalDate");
            entity.HasIndex(e => e.OverrideDate)
                .HasDatabaseName("IX_CalendarEventExceptions_OverrideDate");
        });

        // CalendarEventParticipant (Join Table)
        modelBuilder.Entity<CalendarEventParticipant>(entity =>
        {
            entity.HasKey(p => new { p.CalendarEventId, p.FamilyMemberId });

            entity.HasIndex(p => p.FamilyMemberId)
                .HasDatabaseName("IX_CalendarEventParticipants_FamilyMemberId");

            entity.HasOne(p => p.CalendarEvent)
                .WithMany(e => e.Participants)
                .HasForeignKey(p => p.CalendarEventId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne(p => p.FamilyMember)
                .WithMany()
                .HasForeignKey(p => p.FamilyMemberId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        // CalendarEventExceptionParticipant (Join Table)
        modelBuilder.Entity<CalendarEventExceptionParticipant>(entity =>
        {
            entity.HasKey(p => new { p.CalendarEventExceptionId, p.FamilyMemberId });

            entity.HasIndex(p => p.FamilyMemberId)
                .HasDatabaseName("IX_CalendarEventExceptionParticipants_FamilyMemberId");

            entity.HasOne(p => p.CalendarEventException)
                .WithMany(ex => ex.Participants)
                .HasForeignKey(p => p.CalendarEventExceptionId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne(p => p.FamilyMember)
                .WithMany()
                .HasForeignKey(p => p.FamilyMemberId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        // PushSubscriptionEntity
        modelBuilder.Entity<PushSubscriptionEntity>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Endpoint).IsRequired().HasMaxLength(PushSubscriptionEntity.MaxEndpointLength);
            entity.Property(e => e.P256dh).IsRequired().HasMaxLength(PushSubscriptionEntity.MaxP256dhLength);
            entity.Property(e => e.Auth).IsRequired().HasMaxLength(PushSubscriptionEntity.MaxAuthLength);
            entity.Property(e => e.DeviceLabel).HasMaxLength(PushSubscriptionEntity.MaxDeviceLabelLength);
            entity.Property(e => e.IsActive).HasDefaultValue(true);
            entity.Property(e => e.ConsecutiveFailures).HasDefaultValue(0);

            entity.HasIndex(e => e.Endpoint)
                .IsUnique()
                .HasDatabaseName("IX_PushSubscriptions_Endpoint");
            entity.HasIndex(e => e.IsActive)
                .HasDatabaseName("IX_PushSubscriptions_IsActive");
        });

        // CalendarReminderDelivery
        modelBuilder.Entity<CalendarReminderDelivery>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Status).IsRequired();
            entity.Property(e => e.AttemptCount).HasDefaultValue(0);
            entity.Property(e => e.LeaseToken).HasMaxLength(CalendarReminderDelivery.MaxLeaseTokenLength);
            entity.Property(e => e.LastError).HasMaxLength(CalendarReminderDelivery.MaxLastErrorLength);

            entity.HasIndex(e => new
            {
                e.CalendarEventId,
                e.OccurrenceOriginalDate,
                e.ScheduledTriggerUtc,
                e.PushSubscriptionId
            })
                .IsUnique()
                .HasDatabaseName("IX_CalendarReminderDeliveries_UniqueOccurrenceDevice");

            entity.HasIndex(e => new { e.Status, e.NextAttemptAtUtc, e.LockedUntilUtc })
                .HasDatabaseName("IX_CalendarReminderDeliveries_WorkQueue");

            entity.HasIndex(e => new { e.Status, e.ExpiresAtUtc })
                .HasDatabaseName("IX_CalendarReminderDeliveries_Expiry");

            entity.HasIndex(e => e.CreatedAt)
                .HasDatabaseName("IX_CalendarReminderDeliveries_CreatedAt");

            entity.HasOne(e => e.CalendarEvent)
                .WithMany()
                .HasForeignKey(e => e.CalendarEventId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne(e => e.PushSubscription)
                .WithMany()
                .HasForeignKey(e => e.PushSubscriptionId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        // CalendarReminderSystemState
        modelBuilder.Entity<CalendarReminderSystemState>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).ValueGeneratedNever();
        });

        // TodoList
        modelBuilder.Entity<TodoList>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Name).IsRequired().HasMaxLength(TodoList.MaxNameLength);
            entity.Property(e => e.SortOrder).HasDefaultValue(0);
            entity.Property(e => e.IsArchived).HasDefaultValue(false);

            entity.HasIndex(e => new { e.IsArchived, e.SortOrder })
                .HasDatabaseName("IX_TodoLists_IsArchived_SortOrder");

            entity.HasMany(e => e.Items)
                .WithOne(i => i.TodoList)
                .HasForeignKey(i => i.TodoListId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        // TodoItem
        modelBuilder.Entity<TodoItem>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Title).IsRequired().HasMaxLength(TodoItem.MaxTitleLength);
            entity.Property(e => e.Notes).HasMaxLength(TodoItem.MaxNotesLength);
            entity.Property(e => e.IsCompleted).HasDefaultValue(false);
            entity.Property(e => e.SortOrder).HasDefaultValue(0);

            entity.HasIndex(e => new { e.TodoListId, e.SortOrder })
                .HasDatabaseName("IX_TodoItems_TodoListId_SortOrder");
        });

        // FamilyMember
        modelBuilder.Entity<FamilyMember>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Name)
                .IsRequired()
                .HasMaxLength(FamilyMember.MaxNameLength)
                .UseCollation("NOCASE");
            entity.Property(e => e.IsActive).HasDefaultValue(true);
            entity.Property(e => e.SortOrder).HasDefaultValue(0);

            entity.HasIndex(e => e.Name)
                .IsUnique()
                .HasDatabaseName("IX_FamilyMembers_Name");

            entity.HasIndex(e => new { e.IsActive, e.SortOrder })
                .HasDatabaseName("IX_FamilyMembers_IsActive_SortOrder");
        });

        // RecipeRating
        modelBuilder.Entity<RecipeRating>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Stars).IsRequired();

            entity.ToTable(t => t.HasCheckConstraint(
                "CK_RecipeRatings_Stars",
                $"Stars >= {RecipeRating.MinStars} AND Stars <= {RecipeRating.MaxStars}"));

            entity.HasIndex(e => new { e.RecipeId, e.FamilyMemberId })
                .IsUnique()
                .HasDatabaseName("IX_RecipeRatings_RecipeId_FamilyMemberId");

            entity.HasIndex(e => e.FamilyMemberId)
                .HasDatabaseName("IX_RecipeRatings_FamilyMemberId");

            entity.HasOne(e => e.Recipe)
                .WithMany(r => r.Ratings)
                .HasForeignKey(e => e.RecipeId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne(e => e.FamilyMember)
                .WithMany()
                .HasForeignKey(e => e.FamilyMemberId)
                .OnDelete(DeleteBehavior.Cascade);
        });
    }
}

