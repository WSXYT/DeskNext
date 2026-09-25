using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Avalonia.Media;

namespace DeskNest.App.Localization;

public sealed class LanguageInfo
{
    public string Code { get; }
    public string NativeName { get; }
    public string EnglishName { get; }
    public bool IsRtl { get; }

    public LanguageInfo(string code, string nativeName, string englishName, bool isRtl = false)
    {
        Code = code;
        NativeName = nativeName;
        EnglishName = englishName;
        IsRtl = isRtl;
    }

    public string DisplayText => $"{NativeName} ({Code})";

    public override string ToString() => DisplayText;
}

public sealed class LocalizationManager : INotifyPropertyChanged
{
    private static readonly Lazy<LocalizationManager> _lazy = new(() => new LocalizationManager());
    public static LocalizationManager Instance => _lazy.Value;

    public static readonly IReadOnlyList<LanguageInfo> SupportedLanguages = new List<LanguageInfo>
    {
        new("zh-CN", "简体中文", "Chinese (Simplified)"),
        new("en-US", "English", "English (US)"),
        new("zh-TW", "繁體中文", "Chinese (Traditional)"),
        new("ja-JP", "日本語", "Japanese"),
        new("de-DE", "Deutsch", "German"),
        new("pt-BR", "Português (Brasil)", "Portuguese (Brazil)"),
        new("hi-IN", "हिन्दी", "Hindi"),
        new("es-ES", "Español", "Spanish"),
        new("fr-FR", "Français", "French"),
        new("ar-SA", "العربية", "Arabic (Saudi Arabia)", isRtl: true),
        new("bn-BD", "বাংলা", "Bengali"),
        new("ru-RU", "Русский", "Russian")
    };

    private readonly Dictionary<string, Dictionary<string, string>> _cache = new(StringComparer.OrdinalIgnoreCase);
    private string _currentLanguage = "zh-CN";

    public event PropertyChangedEventHandler? PropertyChanged;
    public event EventHandler<string>? LanguageChanged;

    private LocalizationManager()
    {
        // Pre-load all supported languages to guarantee instant zero-latency switching
        foreach (var lang in SupportedLanguages)
        {
            var dict = LoadLanguageDictionary(lang.Code);
            if (dict.Count == 0 || !dict.ContainsKey("App.Title"))
            {
                throw new InvalidOperationException(
                    $"[LocalizationManager] Failed to initialize language '{lang.Code}'. " +
                    $"Localization dictionary is empty or missing required key 'App.Title'.");
            }
            _cache[lang.Code] = dict;
        }
    }

    public string CurrentLanguage
    {
        get => _currentLanguage;
        set
        {
            if (string.IsNullOrWhiteSpace(value) || _currentLanguage.Equals(value, StringComparison.OrdinalIgnoreCase))
                return;

            _currentLanguage = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(CurrentLanguageInfo));
            OnPropertyChanged(nameof(IsRightToLeft));
            OnPropertyChanged(nameof(FlowDirection));
            OnPropertyChanged(nameof(FlowDirectionValue));
            OnPropertyChanged("Item[]");

            LanguageChanged?.Invoke(this, _currentLanguage);
        }
    }

    public LanguageInfo CurrentLanguageInfo
    {
        get
        {
            foreach (var lang in SupportedLanguages)
            {
                if (lang.Code.Equals(_currentLanguage, StringComparison.OrdinalIgnoreCase))
                    return lang;
            }
            return SupportedLanguages[0];
        }
    }

    public bool IsRightToLeft => CurrentLanguageInfo.IsRtl;

    public FlowDirection FlowDirectionValue => IsRightToLeft ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;

    public string this[string key] => GetString(key);

    public string GetString(string key, params object[] args)
    {
        if (string.IsNullOrEmpty(key))
            return string.Empty;

        string? value = null;

        // 1. Try current language
        if (_cache.TryGetValue(_currentLanguage, out var currentDict) && currentDict.TryGetValue(key, out var localized))
        {
            value = localized;
        }
        // 2. Fallback to en-US
        else if (_cache.TryGetValue("en-US", out var enDict) && enDict.TryGetValue(key, out var enVal))
        {
            value = enVal;
        }
        // 3. Fallback to zh-CN
        else if (_cache.TryGetValue("zh-CN", out var zhDict) && zhDict.TryGetValue(key, out var zhVal))
        {
            value = zhVal;
        }

        value ??= key;

        if (args != null && args.Length > 0)
        {
            try
            {
                return string.Format(CultureInfo.InvariantCulture, value, args);
            }
            catch
            {
                return value;
            }
        }

        return value;
    }

    public IReadOnlyDictionary<string, string> GetAllStringsForLanguage(string languageCode)
    {
        if (_cache.TryGetValue(languageCode, out var dict))
            return dict;

        return LoadLanguageDictionary(languageCode);
    }

    private Dictionary<string, string> LoadLanguageDictionary(string languageCode)
    {
        var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        // 1. Primary: load from assembly embedded resources
        var assembly = Assembly.GetExecutingAssembly();
        var resourceName = $"DeskNest.App.Localization.Strings.strings.{languageCode}.json";

        Stream? stream = assembly.GetManifestResourceStream(resourceName);
        if (stream == null)
        {
            // Case-insensitive or suffix-based manifest search fallback
            var suffix = $".strings.{languageCode}.json";
            foreach (var name in assembly.GetManifestResourceNames())
            {
                if (name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase) ||
                    name.Equals($"strings.{languageCode}.json", StringComparison.OrdinalIgnoreCase))
                {
                    stream = assembly.GetManifestResourceStream(name);
                    if (stream != null)
                        break;
                }
            }
        }

        if (stream != null)
        {
            using (stream)
            using (var reader = new StreamReader(stream))
            {
                var json = reader.ReadToEnd();
                var parsed = JsonSerializer.Deserialize<Dictionary<string, string>>(json);
                if (parsed != null && parsed.Count > 0)
                {
                    foreach (var kvp in parsed)
                    {
                        dict[kvp.Key] = kvp.Value;
                    }
                    return dict;
                }
            }
        }

        // 2. Secondary: direct file-system fallback for development / testing runs
        try
        {
            var basePath = AppContext.BaseDirectory;
            var candidatePaths = new[]
            {
                Path.Combine(basePath, "Localization", "Strings", $"strings.{languageCode}.json"),
                Path.Combine(basePath, "..", "..", "..", "Localization", "Strings", $"strings.{languageCode}.json")
            };

            foreach (var path in candidatePaths)
            {
                if (File.Exists(path))
                {
                    var json = File.ReadAllText(path);
                    var parsed = JsonSerializer.Deserialize<Dictionary<string, string>>(json);
                    if (parsed != null && parsed.Count > 0)
                    {
                        foreach (var kvp in parsed)
                        {
                            dict[kvp.Key] = kvp.Value;
                        }
                        return dict;
                    }
                }
            }
        }
        catch
        {
            // Resilient fallback: catch IO / JSON exceptions and proceed to fail-closed guard
        }

        // 3. Fail-closed: do not silently operate with an empty dictionary
        if (dict.Count == 0)
        {
            throw new InvalidOperationException(
                $"[LocalizationManager] Failed to load localization dictionary for language '{languageCode}'. " +
                $"Embedded resource '{resourceName}' was not found in assembly '{assembly.FullName}', " +
                $"and no valid filesystem fallback was available.");
        }

        return dict;
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
