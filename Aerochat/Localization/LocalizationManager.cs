using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace Aerochat.Localization;

/// <summary>
/// Process-local, read-only locale lookup for WPF bindings and markup extensions.
/// Locale JSON files are read from the application's Locales directory; language
/// changes never persist settings or restart the process.
/// </summary>
public sealed class LocalizationManager : INotifyPropertyChanged
{
    private const string DefaultLanguage = "en-US";
    private const string EnglishFallbackLanguage = "en";

    public static readonly LocalizationManager Instance = new();

    private Dictionary<string, string> _current = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<string, string> _fallback = new(StringComparer.OrdinalIgnoreCase);
    private string _languageCode = DefaultLanguage;

    public LocalizationManager()
    {
        LoadLanguage(DefaultLanguage);
    }

    public string LanguageCode => _languageCode;

    /// <summary>
    /// Path to the folder that contains locale JSON files at runtime.
    /// </summary>
    public static string LocalesDirectory => Path.Combine(AppContext.BaseDirectory, "Locales");

    /// <summary>
    /// Indexer used by WPF bindings and the <see cref="LocExtension"/> markup extension.
    /// </summary>
    public string this[string key] => Get(key);

    /// <summary>
    /// Returns the current translation, then English, then the lookup key.
    /// </summary>
    public string Get(string key)
    {
        if (_current.TryGetValue(key, out string? value) && !string.IsNullOrEmpty(value))
            return value;

        if (_fallback.TryGetValue(key, out string? fallback) && !string.IsNullOrEmpty(fallback))
            return fallback;

        return key;
    }

    /// <summary>
    /// Loads a locale into this process only. Missing locales use the English fallback.
    /// </summary>
    public void LoadLanguage(string code)
    {
        string requestedCode = string.IsNullOrWhiteSpace(code) ? DefaultLanguage : code.Trim();
        _fallback = LoadFile(DefaultLanguage)
            ?? LoadFile(EnglishFallbackLanguage)
            ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        _languageCode = requestedCode;
        _current = LoadFile(requestedCode)
            ?? (requestedCode.StartsWith("en-", StringComparison.OrdinalIgnoreCase)
                ? LoadFile(EnglishFallbackLanguage)
                : null)
            ?? _fallback;

        OnPropertyChanged("Item[]");
    }

    /// <summary>
    /// Returns every language available in the Locales directory.
    /// </summary>
    public List<(string Code, string Name)> GetAvailableLanguages()
    {
        var languages = new List<(string Code, string Name)>();
        if (!Directory.Exists(LocalesDirectory))
            return languages;

        foreach (string file in Directory.GetFiles(LocalesDirectory, "*.json"))
        {
            string code = Path.GetFileNameWithoutExtension(file);
            if (string.IsNullOrEmpty(code))
                continue;

            try
            {
                string json = File.ReadAllText(file);
                Dictionary<string, string>? dictionary =
                    JsonSerializer.Deserialize<Dictionary<string, string>>(json);
                string name = dictionary?.GetValueOrDefault("_meta_language") ?? code;
                languages.Add((code, name));
            }
            catch
            {
                languages.Add((code, code));
            }
        }

        languages.Sort((left, right) =>
        {
            if (left.Code.Equals(EnglishFallbackLanguage, StringComparison.OrdinalIgnoreCase)) return -1;
            if (right.Code.Equals(EnglishFallbackLanguage, StringComparison.OrdinalIgnoreCase)) return 1;
            return string.Compare(left.Name, right.Name, StringComparison.OrdinalIgnoreCase);
        });
        return languages;
    }

    /// <summary>
    /// Finds a key by its English value and returns the current-locale translation.
    /// </summary>
    public string GetByEnglishValue(string englishValue)
    {
        foreach (KeyValuePair<string, string> pair in _fallback)
        {
            if (pair.Value == englishValue)
                return Get(pair.Key);
        }

        return englishValue;
    }

    private static Dictionary<string, string>? LoadFile(string code)
    {
        string path = Path.Combine(LocalesDirectory, $"{code}.json");
        if (!File.Exists(path))
            return null;

        try
        {
            string json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<Dictionary<string, string>>(json)
                ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }
        catch
        {
            return null;
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
