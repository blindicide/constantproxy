using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ConstantProxy.Tests.Localization;

public class ResourceCompletenessTests
{
    private static readonly IReadOnlyDictionary<string, string> En = LocalizationService.AllTables["en"];
    private static readonly IReadOnlyDictionary<string, string> Ru = LocalizationService.AllTables["ru"];

    /// <summary>Keys whose Russian text is legitimately identical to the English one (identifiers, brand names, symbols).</summary>
    private static readonly HashSet<string> IdenticalByDesign = new()
    {
        "label.socks", "label.aliveInterval", "label.aliveCountMax", "language.en", "language.ru",
        "tray.tooltip", "stats.traffic.pair", "button.ok",
    };

    [Fact]
    public void EveryEnglishKeyHasARussianCounterpartAndViceVersa()
    {
        Assert.Empty(En.Keys.Except(Ru.Keys));
        Assert.Empty(Ru.Keys.Except(En.Keys));
        Assert.NotEmpty(En);
    }

    [Fact]
    public void NoValueIsEmpty()
    {
        Assert.All(En, kv => Assert.False(string.IsNullOrWhiteSpace(kv.Value), "en " + kv.Key));
        Assert.All(Ru, kv => Assert.False(string.IsNullOrWhiteSpace(kv.Value), "ru " + kv.Key));
    }

    [Fact]
    public void NoKeyIsDuplicatedInTheJsonFiles()
    {
        foreach (var code in new[] { "en", "ru" })
        {
            using var stream = typeof(LocalizationService).Assembly.GetManifestResourceStream($"ConstantProxy.Core.Localization.Strings.{code}.json")!;
            using var document = JsonDocument.Parse(stream);
            var names = document.RootElement.EnumerateObject().Select(p => p.Name).ToList();
            Assert.Equal(names.Count, names.Distinct().Count());
        }
    }

    private static IEnumerable<string> Placeholders(string text) =>
        Regex.Matches(text, @"\{(\d+)\}").Select(m => m.Groups[1].Value).Distinct().OrderBy(x => x);

    [Fact]
    public void PlaceholdersMatchBetweenLanguages()
    {
        foreach (var (key, en) in En)
        {
            Assert.True(Placeholders(en).SequenceEqual(Placeholders(Ru[key])), $"placeholder mismatch in '{key}': en [{string.Join(',', Placeholders(en))}] ru [{string.Join(',', Placeholders(Ru[key]))}]");
        }
    }

    [Fact]
    public void EveryTemplateFormatsWithoutThrowing()
    {
        foreach (var table in new[] { En, Ru })
        {
            foreach (var (key, text) in table)
            {
                var max = Placeholders(text).Select(int.Parse).DefaultIfEmpty(-1).Max();
                var args = Enumerable.Range(0, max + 1).Select(i => (object)("arg" + i)).ToArray();
                var ex = Record.Exception(() => string.Format(CultureInfo.InvariantCulture, text, args));
                Assert.True(ex is null, $"'{key}' is not a valid format string: {ex?.Message}");
            }
        }
    }

    [Fact]
    public void AccessKeyMarkersAndLineBreaksMatchBetweenLanguages()
    {
        foreach (var (key, en) in En)
        {
            Assert.True(en.Count(c => c == '_') == Ru[key].Count(c => c == '_'), $"access key marker mismatch in '{key}'");
            Assert.True(en.Count(c => c == '\n') == Ru[key].Count(c => c == '\n'), $"line break mismatch in '{key}'");
        }
    }

    [Fact]
    public void NoRussianStringIsLeftUntranslated()
    {
        foreach (var (key, en) in En)
        {
            if (Ru[key] == en)
            {
                Assert.True(IdenticalByDesign.Contains(key), $"'{key}' is identical in both languages; translate it or add it to the allow-list: {en}");
            }
        }
    }

    [Fact]
    public void RussianTextContainsCyrillicUnlessItIsAnAllowedIdentifier()
    {
        foreach (var (key, ru) in Ru)
        {
            if (IdenticalByDesign.Contains(key) || key == "stats.none" || key == "stats.percent" || key == "label.latency.value")
            {
                continue;
            }

            Assert.True(ru.Any(c => c is >= 'А' and <= 'я' or 'Ё' or 'ё'), $"'{key}' has no Russian text: {ru}");
        }
    }

    [Fact]
    public void PluralGroupsAreCompleteInBothLanguages()
    {
        var bases = En.Keys.Where(k => k.EndsWith(".one", StringComparison.Ordinal)).Select(k => k[..^4]).ToList();
        Assert.NotEmpty(bases);
        foreach (var b in bases)
        {
            foreach (var form in new[] { "one", "few", "many", "other" })
            {
                Assert.True(En.ContainsKey($"{b}.{form}"), $"en is missing {b}.{form}");
                Assert.True(Ru.ContainsKey($"{b}.{form}"), $"ru is missing {b}.{form}");
            }
        }
    }

    [Fact]
    public void EveryConnectionStateHasAName()
    {
        foreach (var state in Enum.GetValues<ConnectionState>())
        {
            Assert.True(En.ContainsKey($"state.{state}"), state.ToString());
        }

        foreach (var metric in new[] { "Traffic", "Latency", "Availability" })
        {
            Assert.True(En.ContainsKey($"metric.{metric}"));
        }
    }

    [Fact]
    public void SpecifiedRussianWordingIsPresent()
    {
        Assert.Equal("Порт {0} уже используется.", Ru["failure.port.inuse"]);
        Assert.Equal("OpenSSH не найден.\nПроверьте путь к ssh.exe в настройках.", Ru["failure.ssh.notfound"]);
    }
}

/// <summary>Scans the source tree so a referenced key can never be missing from the resource files.</summary>
public class ResourceUsageTests
{
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "ConstantProxy.sln")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("Could not locate the repository root from " + AppContext.BaseDirectory);
    }

    private static IEnumerable<string> SourceFiles(string pattern) =>
        Directory.EnumerateFiles(Path.Combine(RepoRoot(), "src"), pattern, SearchOption.AllDirectories)
            .Where(f => !f.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar) && !f.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar));

    private static readonly IReadOnlyDictionary<string, string> En = LocalizationService.AllTables["en"];

    [Fact]
    public void EveryKeyReferencedFromCodeExists()
    {
        var calls = new Regex(@"\b(?:Get|Format|Has)\(\s*""([A-Za-z][A-Za-z0-9_.]*\.[A-Za-z0-9_.]+)""", RegexOptions.Compiled);
        var plurals = new Regex(@"\bPlural\(\s*""([A-Za-z][A-Za-z0-9_.]*)""", RegexOptions.Compiled);
        var missing = new List<string>();
        foreach (var file in SourceFiles("*.cs"))
        {
            var text = File.ReadAllText(file);
            foreach (Match m in calls.Matches(text))
            {
                if (!En.ContainsKey(m.Groups[1].Value))
                {
                    missing.Add($"{Path.GetFileName(file)}: {m.Groups[1].Value}");
                }
            }

            foreach (Match m in plurals.Matches(text))
            {
                if (!En.ContainsKey(m.Groups[1].Value + ".other"))
                {
                    missing.Add($"{Path.GetFileName(file)}: plural {m.Groups[1].Value}");
                }
            }
        }

        Assert.Empty(missing);
    }

    [Fact]
    public void EveryKeyReferencedFromXamlExists()
    {
        var uses = new Regex(@"\{loc:Loc\s+(?:Key=)?([A-Za-z0-9_.]+)\}", RegexOptions.Compiled);
        var missing = new List<string>();
        var found = 0;
        foreach (var file in SourceFiles("*.xaml"))
        {
            foreach (Match m in uses.Matches(File.ReadAllText(file)))
            {
                found++;
                if (!En.ContainsKey(m.Groups[1].Value))
                {
                    missing.Add($"{Path.GetFileName(file)}: {m.Groups[1].Value}");
                }
            }
        }

        Assert.Empty(missing);
        Assert.True(found > 0 || !SourceFiles("MainWindow.xaml").Any(), "MainWindow.xaml should use localized keys");
    }

    [Fact]
    public void NoUserVisibleTextIsHardcodedInXaml()
    {
        // SPEC §29: user-visible text must come from the resources. Brand names and pure symbols are allowed.
        var allowed = new HashSet<string> { "constantproxy" };
        var attribute = new Regex("\\b(Content|Text|Header|Title|ToolTip|EmptyText)=\"([^\"{][^\"]*)\"", RegexOptions.Compiled);
        var offenders = new List<string>();
        foreach (var file in SourceFiles("*.xaml").Where(f => !f.EndsWith("App.xaml", StringComparison.Ordinal)))
        {
            foreach (Match m in attribute.Matches(File.ReadAllText(file)))
            {
                var value = m.Groups[2].Value;
                if (value.Any(char.IsLetter) && !allowed.Contains(value.Trim()))
                {
                    offenders.Add($"{Path.GetFileName(file)}: {m.Groups[1].Value}=\"{value}\"");
                }
            }
        }

        Assert.Empty(offenders);
    }

    [Fact]
    public void EveryFailureCodeProducedBySourceHasATranslation()
    {
        var codes = new Regex(@"FailureCategory\.\w+,\s*""([a-z][a-z.]*[a-z])""", RegexOptions.Compiled);
        var found = new HashSet<string>();
        foreach (var file in SourceFiles("*.cs"))
        {
            foreach (Match m in codes.Matches(File.ReadAllText(file)))
            {
                found.Add(m.Groups[1].Value);
            }
        }

        Assert.NotEmpty(found);
        Assert.Empty(found.Where(code => !En.ContainsKey("failure." + code)));
    }

    [Fact]
    public void EveryValidationCodeHasATranslation()
    {
        var codes = new Regex(@"result\.(?:Error|Warning)\([^,]+,\s*""([a-z][a-z.]*)""", RegexOptions.Compiled);
        var found = new HashSet<string>();
        foreach (var file in SourceFiles("*.cs"))
        {
            foreach (Match m in codes.Matches(File.ReadAllText(file)))
            {
                found.Add(m.Groups[1].Value);
            }
        }

        Assert.True(found.Count >= 15, "expected to find the validator codes, found " + found.Count);
        Assert.Empty(found.Where(code => !En.ContainsKey("validation." + code)));
    }

    [Fact]
    public void PortFailuresStillSurfaceThroughTheirCodes()
    {
        // These two are built through helper methods, so the regex above does not see them.
        foreach (var code in new[] { "port.inuse", "port.denied", "ssh.notfound", "ssh.invalid", "ssh.access", "ssh.launch", "traffic.start", "internal", "ssh.exited", "ssh.exited.startup", "startup.timeout", "health.failed" })
        {
            Assert.True(En.ContainsKey("failure." + code), code);
        }
    }
}

public class LocalizationServiceTests
{
    [Fact]
    public void ReturnsTheStringForTheCurrentLanguageAndSwitchesLive()
    {
        var l = new LocalizationService("en");
        var raised = 0;
        l.LanguageChanged += () => raised++;

        Assert.Equal("Connected", l.Get("state.Connected"));
        l.SetLanguage("ru");
        Assert.Equal("Подключено", l.Get("state.Connected"));
        Assert.Equal(1, raised);
        l.SetLanguage("ru");
        Assert.Equal(1, raised); // no change, no event
        Assert.Equal("ru", l.Language);
        Assert.Equal("ru-RU", l.Culture.Name);
    }

    [Fact]
    public void UnknownLanguageFallsBackToEnglish()
    {
        var l = new LocalizationService("fr");
        Assert.Equal("en", l.Language);
        l.SetLanguage("de");
        Assert.Equal("en", l.Language);
    }

    [Fact]
    public void MissingKeyIsVisibleNotBlank() => Assert.Equal("[[no.such.key]]", new LocalizationService().Get("no.such.key"));

    [Fact]
    public void FormatSubstitutesArgumentsAndSurvivesBadTemplates()
    {
        var l = new LocalizationService("ru");
        Assert.Equal("Порт 10080 уже используется.", l.Format("failure.port.inuse", 10080));
        Assert.Equal("[[no.such.key]]", l.Format("no.such.key", 1)); // a missing key is not a template
    }

    [Theory]
    [InlineData("en", 1, "1 second")]
    [InlineData("en", 2, "2 seconds")]
    [InlineData("en", 0, "0 seconds")]
    [InlineData("ru", 1, "1 секунда")]
    [InlineData("ru", 2, "2 секунды")]
    [InlineData("ru", 4, "4 секунды")]
    [InlineData("ru", 5, "5 секунд")]
    [InlineData("ru", 11, "11 секунд")]
    [InlineData("ru", 12, "12 секунд")]
    [InlineData("ru", 21, "21 секунда")]
    [InlineData("ru", 22, "22 секунды")]
    [InlineData("ru", 100, "100 секунд")]
    [InlineData("ru", 101, "101 секунда")]
    [InlineData("ru", 111, "111 секунд")]
    [InlineData("ru", 0, "0 секунд")]
    public void PluralFormsFollowTheLanguageRules(string language, long n, string expected) =>
        Assert.Equal(expected, new LocalizationService(language).Plural("duration.second", n));

    [Theory]
    [InlineData("en", 1, "one")]
    [InlineData("en", 5, "other")]
    [InlineData("ru", 1, "one")]
    [InlineData("ru", 3, "few")]
    [InlineData("ru", 13, "many")]
    [InlineData("ru", 1000001, "one")]
    [InlineData("ru", -2, "few")]
    public void PluralCategories(string language, long n, string expected) =>
        Assert.Equal(expected, PluralRules.Category(language, n));
}

public class LanguageResolutionTests
{
    [Theory]
    [InlineData("auto", "ru-RU", "ru")]
    [InlineData("auto", "ru", "ru")]
    [InlineData("auto", "en-US", "en")]
    [InlineData("auto", "de-DE", "en")]
    [InlineData("auto", "uk-UA", "en")]
    [InlineData(null, "ru-RU", "ru")]
    [InlineData("", "en-GB", "en")]
    [InlineData("en", "ru-RU", "en")]   // manual override beats the OS language
    [InlineData("ru", "en-US", "ru")]
    [InlineData("RU", "en-US", "ru")]
    [InlineData("xx", "ru-RU", "ru")]   // junk setting behaves like auto
    public void ResolvesTheLanguage(string? setting, string osCulture, string expected) =>
        Assert.Equal(expected, LanguageCodes.Resolve(setting, CultureInfo.GetCultureInfo(osCulture)));

    [Theory]
    [InlineData("auto", true)]
    [InlineData("en", true)]
    [InlineData("RU", true)]
    [InlineData("fr", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void ValidSettings(string? setting, bool valid) => Assert.Equal(valid, LanguageCodes.IsValidSetting(setting));

    [Fact]
    public void LanguageSettingDefaultsToAutoAndRoundTrips()
    {
        Assert.Equal("auto", new AppConfig().Interface.Language);
        using var dir = new TempDir();
        var service = new ConfigurationService(dir.File("config.json"));
        var config = AppConfig.CreateDefault();
        config.Interface.Language = "ru";
        service.Save(config);
        Assert.Equal("ru", service.Load().Config.Interface.Language);

        File.WriteAllText(dir.File("c.json"), """{ "interface": { "language": "  RU " }, "profiles": [ { "host": "x" } ] }""");
        Assert.Equal("ru", new ConfigurationService(dir.File("c.json")).Load().Config.Interface.Language);
        File.WriteAllText(dir.File("d.json"), """{ "interface": { "language": null }, "profiles": [ { "host": "x" } ] }""");
        Assert.Equal("auto", new ConfigurationService(dir.File("d.json")).Load().Config.Interface.Language);
    }
}

public class LocalizedTextTests
{
    private static readonly LocalizationService En = new("en");
    private static readonly LocalizationService Ru = new("ru");

    [Fact]
    public void FailuresAreLocalizedByCodeWithArguments()
    {
        var f = FailureClassifier.PortInUse(10080, retryable: false);
        Assert.Equal("Port 10080 is already in use.", LocalizedText.Failure(En, f));
        Assert.Equal("Порт 10080 уже используется.", LocalizedText.Failure(Ru, f));
    }

    [Fact]
    public void UnknownFailureCodesFallBackToTheEnglishMessage()
    {
        var f = new FailureInfo(FailureCategory.Unknown, "future.code", "Something new happened.", true);
        Assert.Equal("Something new happened.", LocalizedText.Failure(Ru, f));
    }

    [Fact]
    public void ConfigFailuresUseTheValidationStrings()
    {
        var f = new FailureInfo(FailureCategory.LocalConfiguration, "config.host.empty", "SSH target must not be empty.", false);
        Assert.Equal("Укажите SSH-хост.", LocalizedText.Failure(Ru, f));
    }

    [Fact]
    public void ClassifiedFailuresAreTranslated()
    {
        var f = FailureClassifier.Classify(new FailureContext(FailureStage.Running, 255, "Permission denied (publickey).", true, 10080));
        Assert.Contains("аутентификации", LocalizedText.Failure(Ru, f));
        Assert.Equal("Exit code: 255" + Environment.NewLine + "Permission denied (publickey).", LocalizedText.FailureDetails(En, f));
        Assert.StartsWith("Код завершения: 255", LocalizedText.FailureDetails(Ru, f));
    }

    [Fact]
    public void ValidationIssuesAreLocalizedByCode()
    {
        var issues = ProfileValidator.Validate(new Profile { Host = "", Port = 0 }, _ => true).Errors.ToList();
        Assert.NotEmpty(issues);
        Assert.All(issues, i => Assert.DoesNotContain("[[", LocalizedText.Issue(Ru, i)));
        Assert.Contains(issues.Select(i => LocalizedText.Issue(Ru, i)), t => t == "Укажите SSH-хост.");
    }

    [Theory]
    [InlineData("en", 18, "18 seconds")]
    [InlineData("ru", 18, "18 секунд")]
    [InlineData("ru", 21, "21 секунда")]
    [InlineData("en", 65, "1 min 5 s")]
    [InlineData("ru", 65, "1 мин 5 с")]
    [InlineData("en", 120, "2 min")]
    [InlineData("ru", 3725, "1 ч 2 мин")]
    [InlineData("en", 3725, "1 h 2 min")]
    public void DowntimeIsFormattedPerLanguage(string language, int seconds, string expected) =>
        Assert.Equal(expected, LocalizedText.Downtime(new LocalizationService(language), TimeSpan.FromSeconds(seconds)));

    [Fact]
    public void NotificationsAreComposedInBothLanguages()
    {
        var restored = new AppNotification(NotificationKind.ConnectionRestored, Downtime: TimeSpan.FromSeconds(18));
        var en = NotificationText.Compose(restored, En);
        Assert.Equal("Proxy connection restored." + Environment.NewLine + "Downtime: 18 seconds.", en.Message);
        Assert.Equal(NotificationSeverity.Info, en.Severity);
        var ru = NotificationText.Compose(restored, Ru);
        Assert.Equal("Соединение с прокси восстановлено." + Environment.NewLine + "Простой: 18 секунд.", ru.Message);

        var lost = NotificationText.Compose(new AppNotification(NotificationKind.ConnectionLost), Ru);
        Assert.Equal("Соединение с прокси потеряно." + Environment.NewLine + "Переподключение...", lost.Message);
        Assert.Equal(NotificationSeverity.Warning, lost.Severity);

        var failed = NotificationText.Compose(new AppNotification(NotificationKind.ConnectionFailed, Failure: FailureClassifier.PortInUse(1080, false)), Ru);
        Assert.Equal("Порт 1080 уже используется.", failed.Message);
        Assert.Equal(NotificationSeverity.Error, failed.Severity);
        Assert.Equal("The proxy connection failed.", NotificationText.Compose(new AppNotification(NotificationKind.ConnectionFailed), En).Message);
    }

    [Fact]
    public void TrafficUnitsAndDecimalSeparatorFollowTheLanguage()
    {
        Assert.Equal("1.50 KB/s", LocalizedText.Rate(En, 1536));
        Assert.Equal("1,50 КБ/с", LocalizedText.Rate(Ru, 1536));
        Assert.Equal("3,74 ГБ", LocalizedText.Bytes(Ru, 4017233879));
        Assert.Equal("512 Б", LocalizedText.Bytes(Ru, 512));
    }

    [Theory]
    [InlineData("en", 8077, "02:14:37")]
    [InlineData("ru", 8077, "02:14:37")]
    [InlineData("en", 90061, "1d 01:01:01")]
    [InlineData("ru", 90061, "1 д 01:01:01")]
    public void DurationsLocalizeTheDayCount(string language, int seconds, string expected) =>
        Assert.Equal(expected, LocalizedText.Duration(new LocalizationService(language), TimeSpan.FromSeconds(seconds)));

    [Fact]
    public void PercentUsesTheLocaleAndShowsADashWhenUnknown()
    {
        Assert.Equal("99.82%", LocalizedText.Percent(En, 99.82));
        Assert.Equal("99,82 %", LocalizedText.Percent(Ru, 99.82));
        Assert.Equal("-", LocalizedText.Percent(En, null));
        Assert.Equal("—", LocalizedText.Percent(Ru, null));
    }

    [Fact]
    public void SummaryIsFormattedInBothLanguages()
    {
        var summary = new AnalyticsSummary(
            new TrafficTotals(1024, 2048), new TrafficTotals(0, 0), new TrafficTotals(0, 0), new TrafficTotals(0, 0),
            99.5, null, 98.25, null, TimeSpan.FromHours(2), TimeSpan.FromHours(1), 3, 1, TimeSpan.FromMinutes(30), 2);

        var en = SummaryFormatter.Format(summary, En);
        Assert.Contains("Session 99.50%   Today -   7 days 98.25%   30 days -", en);
        Assert.Contains("Today        ↓ 2.00 KB   ↑ 1.00 KB", en);
        Assert.Contains("Sessions 2   Runtime 02:00:00   Connected 01:00:00", en);

        var ru = SummaryFormatter.Format(summary, Ru);
        Assert.Contains("Сеанс 99,50 %   Сегодня —   7 дней 98,25 %   30 дней —", ru);
        Assert.Contains("Сегодня      ↓ 2,00 КБ   ↑ 1,00 КБ", ru);
        Assert.DoesNotContain("[[", ru);
    }

    [Fact]
    public void TrayStringsExistInBothLanguages()
    {
        foreach (var key in new[] { "tray.open", "tray.connect", "tray.disconnect", "tray.reconnect", "tray.settings", "tray.exit" })
        {
            Assert.True(En.Has(key));
            Assert.NotEqual(En.Get(key), Ru.Get(key));
        }
    }
}
