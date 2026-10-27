using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using WindowsLosslessSwitcher.Models;

namespace WindowsLosslessSwitcher.Services;

/// <summary>
/// UI-only language resources. Internal statuses, catalog regions and numeric culture are
/// deliberately independent of this service. English source text is the fallback resource.
/// </summary>
public sealed class LocalizationService : INotifyPropertyChanged
{
    public const string EnglishLanguage = "en";
    public const string TraditionalChineseLanguage = "zh-TW";
    private readonly IReadOnlyDictionary<string, string> _traditionalChinese;
    private readonly List<TemplateTranslation> _templates;
    private readonly KeyValuePair<string, string>[] _diagnosticPrefixes;
    private string _language = EnglishLanguage;

    private static readonly Regex Placeholder = new(@"\{(?<index>\d+)\}", RegexOptions.CultureInvariant,
        TimeSpan.FromMilliseconds(50));

    public static LocalizationService Current { get; } = new();

    public LocalizationService()
        : this(LoadTraditionalChineseResources())
    {
    }

    public LocalizationService(IReadOnlyDictionary<string, string> traditionalChinese)
    {
        _traditionalChinese = traditionalChinese;
        _templates = traditionalChinese
            .Where(pair => Placeholder.IsMatch(pair.Key))
            .Select(pair => TemplateTranslation.Create(pair.Key, pair.Value))
            .OrderByDescending(template => template.Specificity)
            .ToList();
        _diagnosticPrefixes = traditionalChinese.Where(pair => pair.Key.EndsWith(".", StringComparison.Ordinal) &&
                                                               !Placeholder.IsMatch(pair.Key))
            .OrderByDescending(pair => pair.Key.Length).ToArray();
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    public event EventHandler? LanguageChanged;

    public string Language => _language;
    public string this[string english] => Translate(english);

    public static string NormalizeLanguage(string? language) => AppSettings.NormalizeUiLanguage(language);

    /// <summary>
    /// Reads only the saved UI preference before the normal app/helper starts. Missing or
    /// unreadable settings keep English; this never creates, migrates, or saves app data.
    /// </summary>
    public static string ReadSavedLanguage(string settingsPath)
    {
        try
        {
            if (!File.Exists(settingsPath)) return EnglishLanguage;
            using var document = JsonDocument.Parse(File.ReadAllText(settingsPath));
            return document.RootElement.ValueKind == JsonValueKind.Object &&
                   document.RootElement.TryGetProperty("uiLanguage", out var language) &&
                   language.ValueKind == JsonValueKind.String
                ? NormalizeLanguage(language.GetString())
                : EnglishLanguage;
        }
        catch
        {
            // A preference read must never repair a file or block startup/recovery.
            return EnglishLanguage;
        }
    }

    public void SetLanguage(string? language)
    {
        var normalized = NormalizeLanguage(language);
        if (normalized == _language) return;
        _language = normalized;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Language)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Item[]"));
        LanguageChanged?.Invoke(this, EventArgs.Empty);
    }

    public string Translate(string english)
    {
        if (_language != TraditionalChineseLanguage || string.IsNullOrEmpty(english)) return english;
        if (_traditionalChinese.TryGetValue(english, out var translated)) return translated;

        // Some existing presentation APIs supply already formatted English. Match only
        // resource-defined, anchored templates; raw transport/status values remain untouched.
        foreach (var template in _templates)
        {
            var result = template.TryTranslate(english);
            if (result is not null) return result;
        }
        return english;
    }

    public string Format(string english, params object?[] arguments) =>
        string.Format(CultureInfo.CurrentCulture,
            _language == TraditionalChineseLanguage && _traditionalChinese.TryGetValue(english, out var translated)
                ? translated : english,
            arguments);

    /// <summary>Localizes composed application diagnostics while retaining unknown OS/player details.</summary>
    public string TranslateDiagnostic(string english)
    {
        if (_language != TraditionalChineseLanguage || string.IsNullOrEmpty(english)) return english;
        if (_traditionalChinese.TryGetValue(english, out var translated)) return translated;
        foreach (var (prefix, translation) in _diagnosticPrefixes)
        {
            if (english.StartsWith(prefix + " ", StringComparison.Ordinal))
                return translation + " " + TranslateDiagnostic(english[(prefix.Length + 1)..]);
        }
        return Translate(english);
    }

    private static IReadOnlyDictionary<string, string> LoadTraditionalChineseResources()
    {
        var resources = new Dictionary<string, string>(StringComparer.Ordinal);
        var assembly = typeof(LocalizationService).Assembly;
        foreach (var name in assembly.GetManifestResourceNames()
                     .Where(name => name.Contains(".Localization.", StringComparison.Ordinal) &&
                                    name.EndsWith(".zh-TW.json", StringComparison.Ordinal))
                     .OrderBy(name => name, StringComparer.Ordinal))
        {
            using var stream = assembly.GetManifestResourceStream(name);
            if (stream is null) continue;
            var entries = JsonSerializer.Deserialize<Dictionary<string, string>>(stream);
            if (entries is null) continue;
            foreach (var (key, value) in entries)
            {
                if (!string.IsNullOrWhiteSpace(value)) resources[key] = value;
            }
        }
        return resources;
    }

    private sealed record TemplateTranslation(Regex Pattern, string Translation, int[] Indices, int Specificity)
    {
        public static TemplateTranslation Create(string english, string translation)
        {
            var indices = new List<int>();
            var pattern = new System.Text.StringBuilder("\\A");
            var offset = 0;
            foreach (Match placeholder in Placeholder.Matches(english))
            {
                pattern.Append(Regex.Escape(english[offset..placeholder.Index]));
                pattern.Append("(.*?)");
                indices.Add(int.Parse(placeholder.Groups["index"].Value, CultureInfo.InvariantCulture));
                offset = placeholder.Index + placeholder.Length;
            }
            pattern.Append(Regex.Escape(english[offset..]));
            pattern.Append("\\z");
            return new(new Regex(pattern.ToString(), RegexOptions.CultureInvariant | RegexOptions.Singleline,
                    TimeSpan.FromMilliseconds(50)), translation, indices.ToArray(), english.Length - indices.Count * 3);
        }

        public string? TryTranslate(string text)
        {
            Match match;
            try { match = Pattern.Match(text); }
            catch (RegexMatchTimeoutException) { return null; }
            if (!match.Success) return null;
            var args = new object?[Indices.Max() + 1];
            for (var index = 0; index < Indices.Length; index++) args[Indices[index]] = match.Groups[index + 1].Value;
            return string.Format(CultureInfo.CurrentCulture, Translation, args);
        }
    }
}
