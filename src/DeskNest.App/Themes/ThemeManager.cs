using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Styling;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Themes.Fluent;
using System.Linq;
using Avalonia.Threading;

namespace DeskNest.App.Themes;

public enum AppThemeMode
{
    System,
    Dark,
    Light
}

public sealed partial class ThemeManager : INotifyPropertyChanged
{
    private static readonly Lazy<ThemeManager> _lazy = new(() => new ThemeManager());
    public static ThemeManager Instance => _lazy.Value;

    private AppThemeMode _currentThemeMode = AppThemeMode.Light;

    public event PropertyChangedEventHandler? PropertyChanged;
    public event EventHandler<AppThemeMode>? ThemeChanged;

    private readonly ResourceDictionary _accentResources = new();
    private string? _accentColor;
    private bool _accentAttached;

    public string? AccentColor => _accentColor;

    public void ApplyAccent(string? hex)
    {
        if (!DeskNest.Core.Workspace.WorkspaceSettings.IsValidAccentColor(hex))
            throw new ArgumentException("Expected #RRGGBB.", nameof(hex));
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(() => ApplyAccent(hex));
            return;
        }
        if (Application.Current is not { } app) return;
        if (_accentAttached && _accentColor == hex) return;
        _accentColor = hex;
        if (!_accentAttached) { app.Resources.MergedDictionaries.Add(_accentResources); _accentAttached = true; }
        foreach (var variant in new[] { ThemeVariant.Light, ThemeVariant.Dark })
        {
            bool dark = variant == ThemeVariant.Dark;
            var accent = Color.Parse(hex ?? (dark ? "#80D5BA" : "#187B68"));
            var surface = Color.Parse(dark ? "#262826" : "#FFFFFF");
            var text = accent;
            for (int i = 0; i < 20 && Contrast(text, surface) < 4.5; i++)
                text = Mix(text, dark ? Colors.White : Colors.Black, 0.12);
            var fg = Contrast(accent, Colors.White) >= Contrast(accent, Colors.Black) ? Colors.White : Colors.Black;
            _accentResources.ThemeDictionaries[variant] = new ResourceDictionary
            {
                ["AccentPrimaryBrush"] = new SolidColorBrush(accent),
                ["AccentPrimaryFgBrush"] = new SolidColorBrush(fg),
                ["AccentHoverBrush"] = new SolidColorBrush(Mix(accent, fg == Colors.White ? Colors.Black : Colors.White, 0.08)),
                ["AccentPressedBrush"] = new SolidColorBrush(Mix(accent, fg == Colors.White ? Colors.Black : Colors.White, 0.16)),
                ["AccentTextBrush"] = new SolidColorBrush(text),
                ["AccentSubtleBrush"] = new SolidColorBrush(Mix(surface, accent, 0.10)),
                ["AccentBorderBrush"] = new SolidColorBrush(Mix(surface, accent, 0.30))
            };
            foreach (var fluent in app.Styles.OfType<FluentTheme>())
                fluent.Palettes[variant].Accent = accent;
        }
        OnPropertyChanged(nameof(AccentColor));
    }

    private static Color Mix(Color a, Color b, double amount) => Color.FromRgb(
        (byte)Math.Round(a.R + (b.R - a.R) * amount), (byte)Math.Round(a.G + (b.G - a.G) * amount),
        (byte)Math.Round(a.B + (b.B - a.B) * amount));

    internal static double Contrast(Color a, Color b)
    {
        static double Channel(byte c) { double n = c / 255.0; return n <= 0.04045 ? n / 12.92 : Math.Pow((n + 0.055) / 1.055, 2.4); }
        static double L(Color c) => 0.2126 * Channel(c.R) + 0.7152 * Channel(c.G) + 0.0722 * Channel(c.B);
        return (Math.Max(L(a), L(b)) + 0.05) / (Math.Min(L(a), L(b)) + 0.05);
    }

    private ThemeManager()
    {
    }

    public AppThemeMode CurrentThemeMode
    {
        get => _currentThemeMode;
        set
        {
            if (_currentThemeMode == value)
                return;

            _currentThemeMode = value;
            ApplyTheme(_currentThemeMode);
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsDark));
            OnPropertyChanged(nameof(ThemeModeName));

            ThemeChanged?.Invoke(this, _currentThemeMode);
        }
    }

    public bool IsDark => Application.Current?.ActualThemeVariant == ThemeVariant.Dark;

    public string ThemeModeName => _currentThemeMode.ToString();

    public void ApplyTheme(AppThemeMode mode)
    {
        if (Application.Current == null)
            return;

        var variant = mode switch
        {
            AppThemeMode.Light => ThemeVariant.Light,
            AppThemeMode.Dark => ThemeVariant.Dark,
            _ => ThemeVariant.Default
        };

        if (Dispatcher.UIThread.CheckAccess())
        {
            Application.Current.RequestedThemeVariant = variant;
        }
        else
        {
            Dispatcher.UIThread.Post(() =>
            {
                if (Application.Current != null)
                    Application.Current.RequestedThemeVariant = variant;
            });
        }
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
