using System.Text;

namespace Flextime;

/// <summary>
/// Text from the server, the network or an exception, made safe to print.
/// Spectre's markup escaping and Console.WriteLine both pass control
/// characters through, and a terminal acts on them: an escape sequence in
/// a computer name could recolour, retitle or rewrite the screen.
/// </summary>
public static class TerminalText
{
    /// <summary>
    /// Drops C0 and C1 control characters except newline and tab, DEL,
    /// and the bidi overrides and isolates that can make text read in an
    /// order other than the one it is in.  Null reads as empty.
    /// </summary>
    public static string Clean(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        if (!text.Any(IsUnsafe))
        {
            return text;
        }

        var builder = new StringBuilder(text.Length);

        foreach (var character in text)
        {
            if (!IsUnsafe(character))
            {
                builder.Append(character);
            }
        }

        return builder.ToString();
    }

    private static bool IsUnsafe(char character) => character switch
    {
        '\n' or '\t' => false,
        < ' ' => true,
        >= '\u007f' and <= '\u009f' => true,
        >= '\u202a' and <= '\u202e' => true,
        >= '\u2066' and <= '\u2069' => true,
        _ => false,
    };
}
