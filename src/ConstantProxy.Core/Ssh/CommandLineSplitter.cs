using System.Text;

namespace ConstantProxy.Core.Ssh;

/// <summary>
/// Splits a user-typed argument string into discrete process arguments, honouring single and double quotes.
/// The result is passed to the process as a list, never re-joined into a shell command.
/// </summary>
public static class CommandLineSplitter
{
    public static List<string> Split(string? text)
    {
        var result = new List<string>();
        if (string.IsNullOrWhiteSpace(text))
        {
            return result;
        }

        var current = new StringBuilder();
        char quote = '\0';
        var hasToken = false;

        foreach (var ch in text)
        {
            if (quote != '\0')
            {
                if (ch == quote)
                {
                    quote = '\0';
                }
                else
                {
                    current.Append(ch);
                }
            }
            else if (ch is '"' or '\'')
            {
                quote = ch;
                hasToken = true;
            }
            else if (char.IsWhiteSpace(ch))
            {
                if (hasToken)
                {
                    result.Add(current.ToString());
                    current.Clear();
                    hasToken = false;
                }
            }
            else
            {
                current.Append(ch);
                hasToken = true;
            }
        }

        if (hasToken)
        {
            result.Add(current.ToString());
        }

        return result;
    }

    /// <summary>Inverse of <see cref="Split"/> for display/editing: quotes entries that contain whitespace.</summary>
    public static string Join(IEnumerable<string> arguments) =>
        string.Join(' ', arguments.Select(a => a.Length == 0 || a.Any(char.IsWhiteSpace) ? $"\"{a}\"" : a));
}
