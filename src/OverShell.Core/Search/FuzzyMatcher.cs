namespace OverShell.Core.Search;

/// <summary>A scored match with the character positions that matched, for highlighting.</summary>
public sealed record FuzzyMatch(int Score, int[] Positions);

/// <summary>
/// Subsequence matching in the style of editor quick-open: every query character must
/// appear in order; consecutive hits, word starts and a start-of-text hit score higher,
/// gaps cost a little. Case-insensitive, with a small bonus for exact case.
/// </summary>
public static class FuzzyMatcher
{
    public static FuzzyMatch? Match(string query, string text)
    {
        if (string.IsNullOrEmpty(query))
        {
            return new FuzzyMatch(0, []);
        }

        if (string.IsNullOrEmpty(text))
        {
            return null;
        }

        var positions = new int[query.Length];
        var score = 0;
        var qi = 0;
        var lastHit = -1;

        for (var ti = 0; ti < text.Length && qi < query.Length; ti++)
        {
            var q = query[qi];
            var t = text[ti];
            if (char.ToUpperInvariant(q) != char.ToUpperInvariant(t))
            {
                continue;
            }

            var bonus = 1;
            if (q == t)
            {
                bonus += 1;
            }

            if (ti == 0)
            {
                bonus += 8;
            }
            else
            {
                var prev = text[ti - 1];
                if (!char.IsLetterOrDigit(prev) || (char.IsLower(prev) && char.IsUpper(t)))
                {
                    bonus += 5; // word start (after separator, or camelCase hump)
                }
            }

            // A contiguous run must beat a chain of separator-delimited hits, otherwise
            // "h-e-r-d" would outscore "view.herd" for the query "herd".
            if (lastHit >= 0)
            {
                bonus += ti == lastHit + 1 ? 8 : -Math.Min(3, ti - lastHit - 1);
            }

            score += bonus;
            positions[qi++] = ti;
            lastHit = ti;
        }

        if (qi < query.Length)
        {
            return null;
        }

        // Shorter candidates edge out longer ones at equal quality.
        score -= Math.Min(10, text.Length / 8);
        return new FuzzyMatch(score, positions);
    }

    /// <summary>Best match across several fields of one candidate, weighted by field order (first field counts most).</summary>
    public static int? Score(string query, params string?[] fields)
    {
        int? best = null;
        for (var i = 0; i < fields.Length; i++)
        {
            if (fields[i] is null)
            {
                continue;
            }

            if (Match(query, fields[i]!) is { } m)
            {
                var weighted = m.Score - (i * 4);
                if (best is null || weighted > best)
                {
                    best = weighted;
                }
            }
        }

        return best;
    }
}

/// <summary>
/// A palette query split into its parts: <c>&gt;</c> switches to commands, <c>@state</c>
/// filters by agent state, <c>#text</c> filters by project, the rest is fuzzy text.
/// </summary>
public sealed record PaletteQuery(bool CommandsMode, string Text, string? StateFilter, string? ProjectFilter)
{
    public static PaletteQuery Parse(string raw)
    {
        var commands = false;
        string? state = null;
        string? project = null;
        var text = new List<string>();

        var input = raw.TrimStart();
        if (input.StartsWith('>'))
        {
            commands = true;
            input = input[1..];
        }

        foreach (var token in input.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!commands && token.Length > 1 && token[0] == '@')
            {
                state = token[1..].ToLowerInvariant();
            }
            else if (!commands && token.Length > 1 && token[0] == '#')
            {
                project = token[1..];
            }
            else
            {
                text.Add(token);
            }
        }

        return new PaletteQuery(commands, string.Join(' ', text), state, project);
    }
}
