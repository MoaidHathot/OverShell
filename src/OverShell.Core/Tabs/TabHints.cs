namespace OverShell.Core.Tabs;

public sealed record TabHint(string TabId, string Text);

/// <summary>Short, prefix-free addresses in display order. I and O are omitted beside 1 and 0.</summary>
public static class TabHints
{
    public const string Alphabet = "123456789ABCDEFGHJKLMNPQRSTUVWXYZ";

    public static IReadOnlyList<TabHint> Assign(IReadOnlyList<string> tabIds)
    {
        // Keep the first nine digits (and the first letters) stable even with hundreds
        // of tabs. Zero introduces the overflow namespace, with equal-length suffixes.
        var width = 1;
        var capacity = (long)Alphabet.Length;
        while (tabIds.Count - Alphabet.Length > capacity) { width++; capacity *= Alphabet.Length; }
        return tabIds.Select((id, i) => new TabHint(id, i < Alphabet.Length
            ? Alphabet[i].ToString() : "0" + Encode(i - Alphabet.Length, width))).ToArray();
    }

    private static string Encode(int index, int width)
    {
        var chars = new char[width];
        for (var i = width - 1; i >= 0; i--) { chars[i] = Alphabet[index % Alphabet.Length]; index /= Alphabet.Length; }
        return new string(chars);
    }
}
