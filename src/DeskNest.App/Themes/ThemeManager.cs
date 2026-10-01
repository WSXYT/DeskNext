using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Styling;
using Avalonia.Threading;

namespace DeskNest.App.Themes;

public enum AppThemeMode
{
    System,
    Dark,
    Light
}

public sealed class ThemeManager : INotifyPropertyChanged
{
    private static readonly Lazy<ThemeManager> _lazy = new(() => new ThemeManager());
    public static ThemeManager Instance => _lazy.Value;

    private AppThemeMode _currentThemeMode = AppThemeMode.Light;

    public event PropertyChangedEventHandler? PropertyChanged;
    public event EventHandler<AppThemeMode>? ThemeChanged;

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

    public bool IsDark => _currentThemeMode == AppThemeMode.Dark;

    public string ThemeModeName => _currentThemeMode.ToString();

    public void ApplyTheme(AppThemeMode mode)
    {
        if (Application.Current == null)
            return;

        var variant = mode switch
        {
            AppThemeMode.Light => ThemeVariant.Light,
            AppThemeMode.Dark => ThemeVariant.Dark,
            _ => ThemeVariant.Light
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
