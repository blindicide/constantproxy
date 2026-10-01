using System.Globalization;
using System.Reflection;
using System.Text.Json;

namespace ConstantProxy.Core.Localization;

/// <summary>Language lookup used by everything that produces user-visible text (SPEC §29).</summary>
public interface ILocalizer
{
    /// <summary>Current language code: <c>en</c> or <c>ru</c>.</summary>
    string Language { get; }

    CultureInfo Culture { get; }

    string Get(string key);

    /// <summary>True when an English string exists for <paramref name="key"/> (English is the reference table).</summary>
    bool Has(string key);

    string Format(string key, params object[] args);

    /// <summary>Picks the plural form for <paramref name="count"/> (keys <c>name.one</c>, <c>name.few</c>, <c>name.many</c>, <c>name.other</c>) and formats it with the count.</summary>
    string Plural(string baseKey, long count);
}

public static class LanguageCodes
{
    public const string Auto = "auto";
    public const string English = "en";
    public const string Russian = "ru";

    public static readonly IReadOnlyList<string> Supported = new[] { English, Russian };

    /// <summary>
    /// First launch and "automatic" mode (SPEC §30): Russian when the Windows UI language is Russian, English otherwise.
    /// An explicit setting always wins.
    /// </summary>
    public static string Resolve(string? setting, CultureInfo osUiCulture)
    {
        var requested = setting?.Trim().ToLowerInvariant();
        if (requested is English or Russian)
        {
            return requested;
        }

        return string.Equals(osUiCulture.TwoLetterISOLanguageName, Russian, StringComparison.OrdinalIgnoreCase) ? Russian : English;
    }

    public static bool IsValidSetting(string? setting) =>
        setting is not null && (setting.Equals(Auto, StringComparison.OrdinalIgnoreCase) || Supported.Contains(setting.ToLowerInvariant()));
}

/// <summary>CLDR plural categories for the supported languages.</summary>
public static class PluralRules
{
    public static string Category(string language, long count)
    {
        var n = Math.Abs(count);
        if (language == LanguageCodes.Russian)
        {
            var mod10 = n % 10;
            var mod100 = n % 100;
            if (mod10 == 1 && mod100 != 11)
            {
                return "one";
            }

            if (mod10 is >= 2 and <= 4 && mod100 is not (>= 12 and <= 14))
            {
                return "few";
            }

            return "many";
        }

        return n == 1 ? "one" : "other";
    }
}

/// <summary>
/// Loads the embedded JSON string tables and serves them (SPEC §29). A missing Russian string falls back to English;
/// a missing key is shown as <c>[[key]]</c> so mistakes are visible instead of silently blank. The completeness tests keep
/// both tables identical in shape.
/// </summary>
public sealed class LocalizationService : ILocalizer
{
    private static readonly Lazy<IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>>> Tables = new(LoadAll);

    private string language = LanguageCodes.English;

    public LocalizationService(string language = LanguageCodes.English)
    {
        SetLanguage(language);
    }

    /// <summary>Raised after the language changed (so views can refresh).</summary>
    public event Action? LanguageChanged;

    public string Language => language;

    public CultureInfo Culture { get; private set; } = CultureInfo.GetCultureInfo("en-US");

    /// <summary>The raw string tables by language code, for validation and tooling.</summary>
    public static IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> AllTables => Tables.Value;

    public void SetLanguage(string code)
    {
        var resolved = LanguageCodes.Supported.Contains(code) ? code : LanguageCodes.English;
        var changed = resolved != language;
        language = resolved;
        Culture = CultureInfo.GetCultureInfo(resolved == LanguageCodes.Russian ? "ru-RU" : "en-US");
        if (changed)
        {
            LanguageChanged?.Invoke();
        }
    }

    public string Get(string key)
    {
        if (Tables.Value[language].TryGetValue(key, out var text))
        {
            return text;
        }

        return Tables.Value[LanguageCodes.English].TryGetValue(key, out var fallback) ? fallback : $"[[{key}]]";
    }

    public bool Has(string key) => Tables.Value[LanguageCodes.English].ContainsKey(key);

    public string Format(string key, params object[] args)
    {
        var template = Get(key);
        try
        {
            return args.Length == 0 ? template : string.Format(Culture, template, args);
        }
        catch (FormatException)
        {
            return template; // a broken template must never take the UI down; the completeness tests catch it
        }
    }

    public string Plural(string baseKey, long count)
    {
        var category = PluralRules.Category(language, count);
        var key = $"{baseKey}.{category}";
        return Format(Has(key) ? key : $"{baseKey}.other", count);
    }

    private static IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> LoadAll()
    {
        var result = new Dictionary<string, IReadOnlyDictionary<string, string>>();
        foreach (var code in LanguageCodes.Supported)
        {
            result[code] = Load(code);
        }

        return result;
    }

    private static IReadOnlyDictionary<string, string> Load(string code)
    {
        var assembly = typeof(LocalizationService).Assembly;
        var name = $"ConstantProxy.Core.Localization.Strings.{code}.json";
        using var stream = assembly.GetManifestResourceStream(name)
                           ?? throw new InvalidOperationException($"Missing embedded resource {name}.");
        using var document = JsonDocument.Parse(stream);
        var table = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var property in document.RootElement.EnumerateObject())
        {
            table[property.Name] = property.Value.GetString() ?? string.Empty;
        }

        return table;
    }
}
