using System.Globalization;

namespace ConstantProxy.Core.Traffic;

/// <summary>Unit labels; replaced per language by the localization layer.</summary>
public sealed record TrafficUnitLabels(IReadOnlyList<string> Rates, IReadOnlyList<string> Sizes)
{
    public static readonly TrafficUnitLabels English = new(
        new[] { "B/s", "KB/s", "MB/s", "GB/s" },
        new[] { "B", "KB", "MB", "GB", "TB" });
}

/// <summary>
/// Human-readable byte and rate formatting. Convention (documented in the README): binary multiples, 1 KB = 1024 B,
/// used consistently everywhere. Precision follows the SPEC mock-up: 1.82 MB/s, 214 KB/s, 3.74 GB, 618 MB.
/// </summary>
public static class TrafficFormatter
{
    private const double Step = 1024.0;

    public static string FormatRate(double bytesPerSecond, CultureInfo? culture = null, TrafficUnitLabels? labels = null)
    {
        labels ??= TrafficUnitLabels.English;
        return Format(bytesPerSecond, labels.Rates, culture ?? CultureInfo.CurrentCulture);
    }

    public static string FormatBytes(long bytes, CultureInfo? culture = null, TrafficUnitLabels? labels = null)
    {
        labels ??= TrafficUnitLabels.English;
        return Format(bytes, labels.Sizes, culture ?? CultureInfo.CurrentCulture);
    }

    private static string Format(double value, IReadOnlyList<string> units, CultureInfo culture)
    {
        if (double.IsNaN(value) || double.IsInfinity(value) || value < 0)
        {
            value = 0;
        }

        var index = 0;
        while (value >= Step && index < units.Count - 1)
        {
            value /= Step;
            index++;
        }

        if (index == 0)
        {
            return string.Create(culture, $"{Math.Round(value, MidpointRounding.AwayFromZero):0} {units[0]}");
        }

        var decimals = value < 10 ? 2 : value < 100 ? 1 : 0;
        var rounded = Math.Round(value, decimals, MidpointRounding.AwayFromZero);
        if (rounded >= 1000 && index < units.Count - 1)
        {
            // 1023.7 KB would otherwise print as "1024 KB"; present it as 1.00 MB instead.
            rounded /= Step;
            index++;
            decimals = 2;
            rounded = Math.Round(rounded, decimals, MidpointRounding.AwayFromZero);
        }
        else if (rounded >= 100 && decimals > 0)
        {
            decimals = 0;
        }
        else if (rounded >= 10 && decimals == 2)
        {
            decimals = 1;
        }

        return string.Create(culture, $"{rounded.ToString("F" + decimals, culture)} {units[index]}");
    }
}
