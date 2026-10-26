using System.Text;

namespace OverShell.Core.Input;

/// <summary>Encodes pasted text as one unit, using one snapshot of DECSET 2004.</summary>
public static class TerminalPaste
{
    public static string Encode(string text, bool bracketed)
    {
        if (text.Length == 0)
        {
            return string.Empty;
        }

        var result = new StringBuilder(text.Length + 16);
        if (bracketed) result.Append("\u001b[200~");
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c == '\r')
            {
                result.Append('\r');
                if (i + 1 < text.Length && text[i + 1] == '\n') i++;
            }
            else if (c == '\n') result.Append('\r');
            else if (c >= ' ' || c == '\t') result.Append(c);
        }

        if (bracketed) result.Append("\u001b[201~");
        return result.ToString();
    }
}
