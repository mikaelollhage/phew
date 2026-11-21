using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Receptbanken.Core.Models;
using Receptbanken.Core.Services;
using Receptbanken.Infrastructure.Data;
using Receptbanken.Infrastructure.Services;

namespace Receptbanken.Tests.Infrastructure;

public class RecipeRatingServiceTests : IDisposable
{
    private readonly TestDbContextFactory _dbFactory;
    private readonly RecipeRatingNotifier _ratingNotifier;
    private readonly FamilyMemberNotifier _memberNotifier;
    private readonly RecipeRatingService _ratingService;
    private readonly FamilyMemberService _memberService;
    private readonly RecipeService _recipeService;

    public RecipeRatingServiceTests()
    {
        _dbFactory = new TestDbContextFactory();
        using var db = _dbFactory.CreateDbContext();
        DbInitializer.InitializeAsync(db, NullLogger.Instance).GetAwaiter().GetResult();

        _ratingNotifier = new RecipeRatingNotifier();
        _memberNotifier = new FamilyMemberNotifier();
        _ratingService = new RecipeRatingService(_dbFactory, _ratingNotifier, logger: NullLogger<RecipeRatingService>.Instance);
        _memberService = new FamilyMemberService(_dbFactory, _memberNotifier, recipeRatingNotifier: _ratingNotifier);
        _recipeService = new RecipeService(_dbFactory, new MockImageStorageService(), NullLogger<RecipeService>.Instance);
    }

    public void Dispose() => _dbFactory.Dispose();

    private async Task<Recipe> CreateRecipeAsync(string title = "Testrecept")
    {
        return await _recipeService.CreateRecipeAsync(new Recipe { Title = title, Servings = 4 });
    }

    [Fact]
    public async Task SetRatingAsync_ShouldCreateAndUpdateRating_AndEnforceOneRatingPerMemberPerRecipe()
    {
        var recipe = await CreateRecipeAsync("Pannkakor");
        var anna = await _memberService.CreateAsync("Anna");
        var erik = await _memberService.CreateAsync("Erik");

        var s1 = await _ratingService.SetRatingAsync(recipe.Id, anna.Id, 4);
        s1.VoteCount.Should().Be(1);
        s1.AverageStars.Should().Be(4.0);

        // Updating Anna's rating should overwrite rather than add a second row
        var s2 = await _ratingService.SetRatingAsync(recipe.Id, anna.Id, 5);
        s2.VoteCount.Should().Be(1);
        s2.AverageStars.Should().Be(5.0);

        // Adding Erik's rating should not affect Anna's rating
        var s3 = await _ratingService.SetRatingAsync(recipe.Id, erik.Id, 3);
        s3.VoteCount.Should().Be(2);
        s3.AverageStars.Should().Be(4.0);

        await using var db = _dbFactory.CreateDbContext();
        var rows = await db.RecipeRatings.Where(r => r.RecipeId == recipe.Id).ToListAsync();
        rows.Should().HaveCount(2);
        rows.Single(r => r.FamilyMemberId == anna.Id).Stars.Should().Be(5);
        rows.Single(r => r.FamilyMemberId == erik.Id).Stars.Should().Be(3);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(6)]
    public async Task SetRatingAsync_InvalidStars_ShouldThrowArgumentOutOfRangeException(int invalidStars)
    {
        var recipe = await CreateRecipeAsync("Soppa");
        var anna = await _memberService.CreateAsync("Anna");

        var act = async () => await _ratingService.SetRatingAsync(recipe.Id, anna.Id, invalidStars);
        await act.Should().ThrowAsync<ArgumentOutOfRangeException>();
    }

    [Fact]
    public async Task GetSummaryAsync_UnratedRecipe_ShouldReturnEmptySummary()
    {
        var recipe = await CreateRecipeAsync("Obetygsatt");
        var summary = await _ratingService.GetSummaryAsync(recipe.Id);

        summary.HasRatings.Should().BeFalse();
        summary.VoteCount.Should().Be(0);
        summary.AverageStars.Should().BeNull();
        summary.FormatBadgeText().Should().Be("Betygsätt");
    }

    [Fact]
    public void RecipeRatingSummary_ShouldFormatSwedishAverageAndSingularPluralVotes()
    {
        var oneVote = RecipeRatingSummary.FromRatings([new RecipeRating { Stars = 5 }]);
        oneVote.FormatBadgeText().Should().Be("5,0 (1 röst)");

        var threeVotes = RecipeRatingSummary.FromRatings([
            new RecipeRating { Stars = 5 },
            new RecipeRating { Stars = 4 },
            new RecipeRating { Stars = 4 }
        ]);
        threeVotes.FormatAverageSwedish().Should().Be("4,3");
        threeVotes.FormatBadgeText().Should().Be("4,3 (3 röster)");
    }

    [Fact]
    public async Task GetModalDataAsync_ShouldReturnAllActiveMembersInOrder_WithUnratedMembersHavingNullStars()
    {
        var recipe = await CreateRecipeAsync("Lasagne");
        var anna = await _memberService.CreateAsync("Anna");
        var erik = await _memberService.CreateAsync("Erik");
        var lisa = await _memberService.CreateAsync("Lisa");
        await _memberService.DeactivateAsync(lisa.Id);

        await _ratingService.SetRatingAsync(recipe.Id, anna.Id, 5);

        var modalData = await _ratingService.GetModalDataAsync(recipe.Id);
        modalData.Summary.VoteCount.Should().Be(1);
        modalData.Summary.AverageStars.Should().Be(5.0);
        modalData.MemberRows.Should().HaveCount(2);
        modalData.MemberRows[0].FamilyMemberName.Should().Be("Anna");
        modalData.MemberRows[0].Stars.Should().Be(5);
        modalData.MemberRows[1].FamilyMemberName.Should().Be("Erik");
        modalData.MemberRows[1].Stars.Should().BeNull();
    }

    [Fact]
    public async Task RenameFamilyMember_ShouldPreserveRatings()
    {
        var recipe = await CreateRecipeAsync("Tacos");
        var anna = await _memberService.CreateAsync("Anna");
        await _ratingService.SetRatingAsync(recipe.Id, anna.Id, 4);

        await _memberService.RenameAsync(anna.Id, "Anna-Karin");

        var modalData = await _ratingService.GetModalDataAsync(recipe.Id);
        modalData.Summary.VoteCount.Should().Be(1);
        modalData.Summary.AverageStars.Should().Be(4.0);
        modalData.MemberRows.Should().ContainSingle(r => r.FamilyMemberName == "Anna-Karin" && r.Stars == 4);
    }

    [Fact]
    public async Task DeleteFamilyMember_ShouldRemoveTheirRatings_WithoutDeletingRecipes()
    {
        var recipe = await CreateRecipeAsync("Köttbullar");
        var anna = await _memberService.CreateAsync("Anna");
        var erik = await _memberService.CreateAsync("Erik");
        await _ratingService.SetRatingAsync(recipe.Id, anna.Id, 5);
        await _ratingService.SetRatingAsync(recipe.Id, erik.Id, 3);

        await _memberService.DeleteAsync(anna.Id);

        var loadedRecipe = await _recipeService.GetRecipeByIdAsync(recipe.Id);
        loadedRecipe.Should().NotBeNull();
        loadedRecipe!.Ratings.Should().ContainSingle(r => r.FamilyMemberId == erik.Id && r.Stars == 3);

        var summary = await _ratingService.GetSummaryAsync(recipe.Id);
        summary.VoteCount.Should().Be(1);
        summary.AverageStars.Should().Be(3.0);
    }

    [Fact]
    public async Task DeleteRecipe_ShouldRemoveItsRatings_WithoutDeletingFamilyMembers()
    {
        var recipe = await CreateRecipeAsync("Wok");
        var anna = await _memberService.CreateAsync("Anna");
        await _ratingService.SetRatingAsync(recipe.Id, anna.Id, 5);

        await _recipeService.DeleteRecipeAsync(recipe.Id);

        await using var db = _dbFactory.CreateDbContext();
        (await db.RecipeRatings.Where(r => r.RecipeId == recipe.Id).CountAsync()).Should().Be(0);
        (await db.FamilyMembers.AnyAsync(m => m.Id == anna.Id)).Should().BeTrue();
    }
}
