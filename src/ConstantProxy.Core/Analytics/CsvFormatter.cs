using System.Globalization;
using System.Text;

namespace ConstantProxy.Core.Analytics;

/// <summary>RFC 4180 CSV writer with spreadsheet-formula neutralisation for text cells.</summary>
public static class CsvFormatter
{
    public static string Line(IEnumerable<object?> cells) => string.Join(',', cells.Select(Cell)) + "\r\n";

    public static string Cell(object? value)
    {
        switch (value)
        {
            case null:
                return string.Empty;
            case string text:
                return Quote(Neutralise(text));
            case bool b:
                return b ? "true" : "false";
            case DateTimeOffset dto:
                return dto.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture);
            case IFormattable f:
                return f.ToString(null, CultureInfo.InvariantCulture); // numbers: never locale-dependent, never quoted
            default:
                return Quote(Neutralise(value.ToString() ?? string.Empty));
        }
    }

    /// <summary>
    /// A leading = + - @ (or control character) makes spreadsheets evaluate the cell as a formula; prefixing a quote
    /// keeps exported host names and error text inert.
    /// </summary>
    private static string Neutralise(string text) =>
        text.Length > 0 && text[0] is '=' or '+' or '-' or '@' or '\t' or '\r' ? "'" + text : text;

    private static string Quote(string text)
    {
        if (text.IndexOfAny(new[] { ',', '"', '\r', '\n' }) < 0)
        {
            return text;
        }

        var sb = new StringBuilder(text.Length + 2).Append('"');
        foreach (var ch in text)
        {
            sb.Append(ch);
            if (ch == '"')
            {
                sb.Append('"');
            }
        }

        return sb.Append('"').ToString();
    }
}
