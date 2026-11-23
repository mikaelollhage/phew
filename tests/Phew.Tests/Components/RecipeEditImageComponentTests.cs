using Bunit;
using FluentAssertions;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.FluentUI.AspNetCore.Components;
using Phew.Core.Interfaces;
using Phew.Core.Models;
using Phew.Infrastructure.Services;
using Phew.Tests.Infrastructure;
using Phew.Web.Components.Pages;
using Phew.Web.Services;

namespace Phew.Tests.Components;

public class RecipeEditImageComponentTests : TestContext
{
    private (RecipeService RecipeService, MockImageStorageService ImageStorage) RegisterServices(IImageStorageService? customImageStorage = null)
    {
        var dbFactory = new TestDbContextFactory();
        var imageStorage = customImageStorage as MockImageStorageService ?? new MockImageStorageService();
        var storageInterface = customImageStorage ?? imageStorage;
        var recipeService = new RecipeService(dbFactory, storageInterface, NullLogger<RecipeService>.Instance);
        var categoryService = new CategoryService(dbFactory, NullLogger<CategoryService>.Instance);

        Services.AddFluentUIComponents();
        Services.AddScoped<AppNotificationService>();
        Services.AddSingleton(storageInterface);
        Services.AddSingleton(recipeService);
        Services.AddSingleton(categoryService);
        Services.AddSingleton(dbFactory);
        JSInterop.Mode = JSRuntimeMode.Loose;

        return (recipeService, imageStorage);
    }

    private static async Task<Recipe> SeedRecipeWithImageAsync(RecipeService recipeService, string imagePath = "images/existing.webp")
    {
        return await recipeService.CreateRecipeAsync(new Recipe
        {
            Title = "Pannkakor",
            Servings = 4,
            ServingsUnit = "portioner",
            ImagePath = imagePath,
            Ingredients = [new Ingredient { Name = "Mjöl" }],
            Instructions = [new InstructionStep { InstructionText = "Stek" }]
        });
    }

    [Fact]
    public async Task RecipeEdit_WithExistingImage_RendersPreviewAndCompactActions_WithoutDropzone()
    {
        var (recipeService, _) = RegisterServices();
        var saved = await SeedRecipeWithImageAsync(recipeService);

        var cut = RenderComponent<RecipeEdit>(p => p.Add(x => x.Id, saved.Id));
        cut.WaitForAssertion(() => cut.FindAll(".recipe-image-preview-img").Should().ContainSingle());

        var img = cut.Find(".recipe-image-preview-img");
        img.GetAttribute("src").Should().Be("/images/existing.webp");

        // No large dropzone when image exists
        cut.FindAll("label.image-upload-dropzone").Should().BeEmpty();
        cut.Markup.Should().NotContain("datorn");

        // Compact action row with "Byt bild" and "Ta bort bild"
        var replaceLabel = cut.Find("label.recipe-image-replace-label");
        replaceLabel.GetAttribute("for").Should().Be("recipe-image-upload");
        replaceLabel.TextContent.Should().Contain("Byt bild");

        var fileInput = cut.Find("input[type='file']#recipe-image-upload");
        fileInput.GetAttribute("aria-label").Should().Be("Byt receptbild");

        var removeBtn = cut.Find("button.recipe-image-remove-btn");
        removeBtn.TextContent.Should().Contain("Ta bort bild");
        removeBtn.GetAttribute("aria-label").Should().Be("Ta bort bild");
    }

    [Fact]
    public void RecipeEdit_WithoutImage_RendersCompactEmptySelection_WithoutDesktopText()
    {
        RegisterServices();

        var cut = RenderComponent<RecipeEdit>();
        cut.WaitForAssertion(() => cut.FindAll("label.image-upload-dropzone").Should().ContainSingle());

        var dropzone = cut.Find("label.image-upload-dropzone");
        dropzone.GetAttribute("for").Should().Be("recipe-image-upload");
        dropzone.TextContent.Should().Contain("Lägg till bild");
        dropzone.TextContent.Should().NotContain("datorn");

        cut.FindAll(".recipe-image-preview").Should().BeEmpty();
        cut.FindAll("button.recipe-image-remove-btn").Should().BeEmpty();
    }

    [Fact]
    public async Task RecipeEdit_ReplacingImage_UpdatesPreview_AllowsReplacingAgain_AndRespectsCancel()
    {
        var (recipeService, _) = RegisterServices();
        var saved = await SeedRecipeWithImageAsync(recipeService, "images/original.webp");

        var cut = RenderComponent<RecipeEdit>(p => p.Add(x => x.Id, saved.Id));
        cut.WaitForAssertion(() => cut.FindAll(".recipe-image-preview-img").Should().ContainSingle());

        // First replacement
        var inputFile = cut.FindComponent<InputFile>();
        var firstReplacement = new TestBrowserFile("new1.jpg", "image/jpeg", [1, 2, 3]);
        await cut.InvokeAsync(() => inputFile.Instance.OnChange.InvokeAsync(new InputFileChangeEventArgs([firstReplacement])));

        cut.WaitForAssertion(() =>
        {
            var img = cut.Find(".recipe-image-preview-img");
            img.GetAttribute("src").Should().StartWith("/images/recipes/");
            var successBanner = cut.Find(".image-upload-feedback.rb-feedback-success");
            successBanner.ClassList.Should().Contain("rb-feedback-floating");
            successBanner.TextContent.Should().Contain("Bilden har laddats upp och sparats!");
        });

        var firstSrc = cut.Find(".recipe-image-preview-img").GetAttribute("src");

        // Second replacement
        inputFile = cut.FindComponent<InputFile>();
        var secondReplacement = new TestBrowserFile("new2.png", "image/png", [4, 5, 6]);
        await cut.InvokeAsync(() => inputFile.Instance.OnChange.InvokeAsync(new InputFileChangeEventArgs([secondReplacement])));

        cut.WaitForAssertion(() =>
        {
            var img = cut.Find(".recipe-image-preview-img");
            img.GetAttribute("src").Should().NotBe(firstSrc);
        });

        // Cancel without saving -> persisted recipe keeps original image
        cut.FindAll("button").First(b => b.TextContent.Trim() == "Avbryt").Click();

        var reloaded = await recipeService.GetRecipeByIdAsync(saved.Id);
        reloaded!.ImagePath.Should().Be("images/original.webp");
    }

    [Fact]
    public async Task RecipeEdit_EmptySelectionEvent_DoesNotRemoveExistingImage()
    {
        var (recipeService, _) = RegisterServices();
        var saved = await SeedRecipeWithImageAsync(recipeService, "images/keep.webp");

        var cut = RenderComponent<RecipeEdit>(p => p.Add(x => x.Id, saved.Id));
        cut.WaitForAssertion(() => cut.FindAll(".recipe-image-preview-img").Should().ContainSingle());

        var inputFile = cut.FindComponent<InputFile>();
        await cut.InvokeAsync(() => inputFile.Instance.OnChange.InvokeAsync(new InputFileChangeEventArgs(Array.Empty<IBrowserFile>())));

        cut.Find(".recipe-image-preview-img").GetAttribute("src").Should().Be("/images/keep.webp");
    }

    [Fact]
    public async Task RecipeEdit_RemovingImage_FollowsSaveAndCancelSemantics()
    {
        var (recipeService, _) = RegisterServices();
        var saved = await SeedRecipeWithImageAsync(recipeService, "images/to-remove.webp");

        // Case 1: Remove then Cancel -> image remains persisted
        var cutCancel = RenderComponent<RecipeEdit>(p => p.Add(x => x.Id, saved.Id));
        cutCancel.WaitForAssertion(() => cutCancel.FindAll("button.recipe-image-remove-btn").Should().ContainSingle());

        cutCancel.Find("button.recipe-image-remove-btn").Click();

        cutCancel.FindAll(".recipe-image-preview").Should().BeEmpty();
        cutCancel.FindAll("label.image-upload-dropzone").Should().ContainSingle();

        cutCancel.FindAll("button").First(b => b.TextContent.Trim() == "Avbryt").Click();
        var afterCancel = await recipeService.GetRecipeByIdAsync(saved.Id);
        afterCancel!.ImagePath.Should().Be("images/to-remove.webp");

        // Case 2: Remove then Save -> image removal is persisted
        var cutSave = RenderComponent<RecipeEdit>(p => p.Add(x => x.Id, saved.Id));
        cutSave.WaitForAssertion(() => cutSave.FindAll("button.recipe-image-remove-btn").Should().ContainSingle());

        cutSave.Find("button.recipe-image-remove-btn").Click();
        cutSave.FindAll("button").First(b => b.TextContent.Contains("Spara ändringar")).Click();

        cutSave.WaitForAssertion(async () =>
        {
            var afterSave = await recipeService.GetRecipeByIdAsync(saved.Id);
            afterSave!.ImagePath.Should().BeNull();
        });
    }

    [Fact]
    public async Task RecipeEdit_InvalidExtensionOrTooLargeFile_ShowsErrorAndPreservesExistingImage()
    {
        var (recipeService, _) = RegisterServices(new ValidatingImageStorageService());
        var saved = await SeedRecipeWithImageAsync(recipeService, "images/safe.webp");

        var cut = RenderComponent<RecipeEdit>(p => p.Add(x => x.Id, saved.Id));
        cut.WaitForAssertion(() => cut.FindAll(".recipe-image-preview-img").Should().ContainSingle());

        // Invalid extension
        var inputFile = cut.FindComponent<InputFile>();
        var badExt = new TestBrowserFile("anim.gif", "image/gif", [1, 2, 3]);
        await cut.InvokeAsync(() => inputFile.Instance.OnChange.InvokeAsync(new InputFileChangeEventArgs([badExt])));

        cut.WaitForAssertion(() =>
        {
            var formatError = cut.Find(".image-upload-feedback.rb-feedback-error");
        formatError.ClassList.Should().NotContain("rb-feedback-floating");
        formatError.TextContent.Should().Contain("Ogiltigt bildformat");
            cut.Find(".recipe-image-preview-img").GetAttribute("src").Should().Be("/images/safe.webp");
        });

        // File > 10 MB
        inputFile = cut.FindComponent<InputFile>();
        var oversized = new TestBrowserFile("huge.jpg", "image/jpeg", [1, 2, 3], 10 * 1024 * 1024 + 1);
        await cut.InvokeAsync(() => inputFile.Instance.OnChange.InvokeAsync(new InputFileChangeEventArgs([oversized])));

        cut.WaitForAssertion(() =>
        {
            cut.Find(".image-upload-feedback.rb-feedback-error").TextContent.Should().Contain("Max 10 MB");
            cut.Find(".recipe-image-preview-img").GetAttribute("src").Should().Be("/images/safe.webp");
        });
    }

    [Fact]
    public async Task RecipeEdit_UploadFailure_ShowsErrorAndPreservesExistingImage()
    {
        var failingStorage = new FailingImageStorageService();
        var (recipeService, _) = RegisterServices(failingStorage);
        var saved = await SeedRecipeWithImageAsync(recipeService, "images/safe.webp");

        var cut = RenderComponent<RecipeEdit>(p => p.Add(x => x.Id, saved.Id));
        cut.WaitForAssertion(() => cut.FindAll(".recipe-image-preview-img").Should().ContainSingle());

        var inputFile = cut.FindComponent<InputFile>();
        var validFile = new TestBrowserFile("photo.jpg", "image/jpeg", [1, 2, 3]);
        await cut.InvokeAsync(() => inputFile.Instance.OnChange.InvokeAsync(new InputFileChangeEventArgs([validFile])));

        cut.WaitForAssertion(() =>
        {
            cut.Find(".image-upload-feedback.rb-feedback-error").TextContent.Should().Contain("Bilden kunde inte laddas upp");
            cut.Find(".recipe-image-preview-img").GetAttribute("src").Should().Be("/images/safe.webp");
        });
    }

    private sealed class TestBrowserFile(string name, string contentType, byte[] data, long? sizeOverride = null) : IBrowserFile
    {
        public string Name { get; } = name;
        public DateTimeOffset LastModified => DateTimeOffset.UtcNow;
        public long Size { get; } = sizeOverride ?? data.Length;
        public string ContentType { get; } = contentType;
        public Stream OpenReadStream(long maxAllowedSize = 512000, CancellationToken cancellationToken = default)
            => new MemoryStream(data);
    }

    private sealed class ValidatingImageStorageService : IImageStorageService
    {
        private static readonly HashSet<string> Allowed = new(StringComparer.OrdinalIgnoreCase)
            { ".jpg", ".jpeg", ".png", ".webp" };

        public bool IsValidImageExtension(string fileName)
            => Allowed.Contains(Path.GetExtension(fileName));

        public bool ValidateImageBytes(ReadOnlySpan<byte> headerBytes) => true;

        public Task<string> SaveImageAsync(Stream stream, string originalFileName, CancellationToken cancellationToken = default)
            => Task.FromResult($"images/recipes/{Guid.NewGuid():N}.webp");

        public Task DeleteImageAsync(string? imagePath, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
    }

    private sealed class FailingImageStorageService : IImageStorageService
    {
        public bool IsValidImageExtension(string fileName) => true;

        public bool ValidateImageBytes(ReadOnlySpan<byte> headerBytes) => true;

        public Task<string> SaveImageAsync(Stream stream, string originalFileName, CancellationToken cancellationToken = default)
            => throw new IOException("Disk full");

        public Task DeleteImageAsync(string? imagePath, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
    }
}
