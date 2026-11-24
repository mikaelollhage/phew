using Microsoft.FluentUI.AspNetCore.Components;
using Microsoft.JSInterop;

namespace Phew.Web.Services;

public enum ThemePreference
{
    System = 0,
    Light = 1,
    Dark = 2
}

public sealed class AppThemeService
{
    public const string StorageKey = "phew.theme";

    public ThemePreference Preference { get; private set; } = ThemePreference.System;
    public string ResolvedTheme { get; private set; } = "dark";
    public bool IsInitialized { get; private set; }

    public DesignThemeModes FluentMode =>
        string.Equals(ResolvedTheme, "light", StringComparison.OrdinalIgnoreCase)
            ? DesignThemeModes.Light
            : DesignThemeModes.Dark;

    public event Action? OnChange;

    public static ThemePreference ParsePreference(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return ThemePreference.System;
        }

        return raw.Trim().ToLowerInvariant() switch
        {
            "light" => ThemePreference.Light,
            "dark" => ThemePreference.Dark,
            "system" => ThemePreference.System,
            _ => ThemePreference.System
        };
    }

    public static string ToPreferenceKey(ThemePreference preference) => preference switch
    {
        ThemePreference.Light => "light",
        ThemePreference.Dark => "dark",
        _ => "system"
    };

    public static string NormalizeResolvedTheme(string? rawResolved, ThemePreference preference)
    {
        if (preference == ThemePreference.Light)
        {
            return "light";
        }

        if (preference == ThemePreference.Dark)
        {
            return "dark";
        }

        return string.Equals(rawResolved?.Trim(), "light", StringComparison.OrdinalIgnoreCase)
            ? "light"
            : "dark";
    }

    public async Task InitializeAsync<T>(IJSRuntime jsRuntime, DotNetObjectReference<T>? dotNetRef = null) where T : class
    {
        try
        {
            ThemeStateJsDto? state;
            if (dotNetRef != null)
            {
                state = await jsRuntime.InvokeAsync<ThemeStateJsDto?>("phew.registerThemeListener", dotNetRef);
            }
            else
            {
                state = await jsRuntime.InvokeAsync<ThemeStateJsDto?>("phew.getThemeState");
            }

            if (state != null)
            {
                ApplyState(state.Preference, state.Resolved, isInitialized: true);
                return;
            }
        }
        catch (Exception ex) when (ex is JSDisconnectedException or TaskCanceledException or ObjectDisposedException or InvalidOperationException)
        {
            // Ignore when prerendering, disconnected, or running in unit tests without JS mock
        }

        if (!IsInitialized)
        {
            IsInitialized = true;
            NotifyStateChanged();
        }
    }

    public async Task SetPreferenceAsync(IJSRuntime jsRuntime, ThemePreference preference)
    {
        var prefKey = ToPreferenceKey(preference);
        string? resolved = null;

        try
        {
            var state = await jsRuntime.InvokeAsync<ThemeStateJsDto?>("phew.setThemePreference", prefKey);
            if (state != null)
            {
                ApplyState(state.Preference, state.Resolved, isInitialized: true);
                return;
            }
        }
        catch (Exception ex) when (ex is JSDisconnectedException or TaskCanceledException or ObjectDisposedException or InvalidOperationException)
        {
            // Fallback for test environments without full JS interop
        }

        resolved = preference switch
        {
            ThemePreference.Light => "light",
            ThemePreference.Dark => "dark",
            _ => ResolvedTheme
        };

        ApplyState(prefKey, resolved, isInitialized: true);
    }

    public void UpdateFromClient(string? preferenceRaw, string? resolvedRaw)
    {
        ApplyState(preferenceRaw, resolvedRaw, isInitialized: true);
    }

    private void ApplyState(string? preferenceRaw, string? resolvedRaw, bool isInitialized)
    {
        var newPref = ParsePreference(preferenceRaw);
        var newResolved = NormalizeResolvedTheme(resolvedRaw, newPref);

        var changed = Preference != newPref ||
                      !string.Equals(ResolvedTheme, newResolved, StringComparison.Ordinal) ||
                      IsInitialized != isInitialized;

        Preference = newPref;
        ResolvedTheme = newResolved;
        IsInitialized = isInitialized;

        if (changed)
        {
            NotifyStateChanged();
        }
    }

    private void NotifyStateChanged() => OnChange?.Invoke();

    public sealed class ThemeStateJsDto
    {
        public string? Preference { get; set; }
        public string? Resolved { get; set; }
    }
}
