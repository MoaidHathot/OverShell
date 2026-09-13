namespace OverShell.Core.Input;

[Flags]
public enum ChordModifiers
{
    None = 0,
    Control = 1,
    Shift = 2,
    Alt = 4,
    Win = 8,
}

/// <summary>
/// A key plus modifiers, in the notation Windows Terminal users already know:
/// <c>ctrl+shift+p</c>, <c>alt+1</c>, <c>ctrl+pgdn</c>. The key is stored under the
/// WPF <c>Key</c> enum's member name (<c>D1</c>, <c>PageDown</c>, <c>OemTilde</c>) so the
/// UI layer can <c>Enum.Parse</c> it without this assembly referencing WPF.
/// </summary>
public readonly record struct KeyChord(ChordModifiers Modifiers, string Key)
{
    private static readonly Dictionary<string, string> Aliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["esc"] = "Escape", ["escape"] = "Escape",
        ["enter"] = "Return", ["return"] = "Return",
        ["space"] = "Space", ["tab"] = "Tab", ["backspace"] = "Back", ["back"] = "Back",
        ["del"] = "Delete", ["delete"] = "Delete", ["ins"] = "Insert", ["insert"] = "Insert",
        ["home"] = "Home", ["end"] = "End",
        ["pgup"] = "PageUp", ["pageup"] = "PageUp", ["pgdn"] = "PageDown", ["pagedown"] = "PageDown",
        ["up"] = "Up", ["down"] = "Down", ["left"] = "Left", ["right"] = "Right",
        ["plus"] = "OemPlus", ["="] = "OemPlus",
        ["minus"] = "OemMinus", ["-"] = "OemMinus",
        ["comma"] = "OemComma", [","] = "OemComma",
        ["period"] = "OemPeriod", ["."] = "OemPeriod",
        ["slash"] = "OemQuestion", ["/"] = "OemQuestion",
        // On a US layout the key left of Enter is VK_OEM_5; Oem102 (OemBackslash) is the extra
        // key on 102-key layouts, so "backslash" must not alias to it.
        ["backslash"] = "Oem5", ["\\"] = "Oem5", ["pipe"] = "Oem5",
        ["semicolon"] = "OemSemicolon", [";"] = "OemSemicolon",
        ["quote"] = "OemQuotes", ["'"] = "OemQuotes",
        ["backtick"] = "OemTilde", ["`"] = "OemTilde", ["tilde"] = "OemTilde",
        ["["] = "OemOpenBrackets", ["]"] = "OemCloseBrackets",
        ["menu"] = "Apps", ["app"] = "Apps",
    };

    /// <summary>
    /// Member names of WPF's <c>System.Windows.Input.Key</c>, mirrored here so a typo in
    /// <c>keybindings.jsonc</c> is reported at load time without this assembly taking a WPF
    /// dependency. The UI layer's <c>Enum.Parse&lt;Key&gt;</c> is therefore guaranteed to succeed.
    /// </summary>
    private static readonly HashSet<string> KnownKeys = new(
        new[]
        {
            "Cancel", "Back", "Tab", "LineFeed", "Clear", "Return", "Pause", "Capital", "KanaMode", "HangulMode",
            "JunjaMode", "FinalMode", "HanjaMode", "KanjiMode", "Escape", "ImeConvert", "ImeNonConvert", "ImeAccept",
            "ImeModeChange", "Space", "PageUp", "PageDown", "End", "Home", "Left", "Up", "Right", "Down", "Select",
            "Print", "Execute", "PrintScreen", "Insert", "Delete", "Help", "LWin", "RWin", "Apps", "Sleep", "Multiply",
            "Add", "Separator", "Subtract", "Decimal", "Divide", "NumLock", "Scroll", "LeftShift", "RightShift",
            "LeftCtrl", "RightCtrl", "LeftAlt", "RightAlt", "BrowserBack", "BrowserForward", "BrowserRefresh",
            "BrowserStop", "BrowserSearch", "BrowserFavorites", "BrowserHome", "VolumeMute", "VolumeDown", "VolumeUp",
            "MediaNextTrack", "MediaPreviousTrack", "MediaStop", "MediaPlayPause", "LaunchMail", "SelectMedia",
            "LaunchApplication1", "LaunchApplication2", "OemSemicolon", "OemPlus", "OemComma", "OemMinus", "OemPeriod",
            "OemQuestion", "OemTilde", "AbntC1", "AbntC2", "OemOpenBrackets", "Oem5", "OemPipe", "OemCloseBrackets",
            "OemQuotes", "Oem8", "OemBackslash", "Oem1", "Oem2", "Oem3", "Oem4", "Oem6", "Oem7", "Oem102",
            "OemAttn", "OemFinish", "OemCopy", "OemAuto", "OemEnlw", "OemBackTab", "Attn", "CrSel", "ExSel",
            "EraseEofF", "Play", "Zoom", "NoName", "Pa1", "OemClear",
        }
        .Concat(Enumerable.Range(0, 10).Select(d => "D" + d))
        .Concat(Enumerable.Range(0, 10).Select(d => "NumPad" + d))
        .Concat(Enumerable.Range(1, 24).Select(f => "F" + f))
        .Concat(Enumerable.Range('A', 26).Select(c => ((char)c).ToString())),
        StringComparer.OrdinalIgnoreCase);

    private static readonly Dictionary<string, string> Display = new(StringComparer.Ordinal)
    {
        ["Escape"] = "Esc", ["Return"] = "Enter", ["Back"] = "Backspace", ["Delete"] = "Del", ["Insert"] = "Ins",
        ["PageUp"] = "PgUp", ["PageDown"] = "PgDn", ["OemPlus"] = "=", ["OemMinus"] = "-", ["OemComma"] = ",",
        ["OemPeriod"] = ".", ["OemQuestion"] = "/", ["OemBackslash"] = "\\", ["Oem5"] = "\\", ["OemSemicolon"] = ";",
        ["OemQuotes"] = "'", ["OemTilde"] = "`", ["OemOpenBrackets"] = "[", ["OemCloseBrackets"] = "]",
    };

    /// <summary>Parses <c>ctrl+shift+p</c>. Case-insensitive; order of modifiers free; the key comes last.</summary>
    public static bool TryParse(string? text, out KeyChord chord)
    {
        chord = default;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var modifiers = ChordModifiers.None;
        string? key = null;

        // A literal '+' key is written as "plus"; splitting on '+' would otherwise eat it.
        foreach (var raw in text.Split('+', StringSplitOptions.TrimEntries))
        {
            if (raw.Length == 0)
            {
                continue;
            }

            switch (raw.ToLowerInvariant())
            {
                case "ctrl" or "control":
                    modifiers |= ChordModifiers.Control;
                    break;
                case "shift":
                    modifiers |= ChordModifiers.Shift;
                    break;
                case "alt":
                    modifiers |= ChordModifiers.Alt;
                    break;
                case "win" or "windows" or "super" or "meta":
                    modifiers |= ChordModifiers.Win;
                    break;
                default:
                    if (key is not null)
                    {
                        return false;
                    }

                    key = NormalizeKey(raw);
                    if (key is null)
                    {
                        return false;
                    }

                    break;
            }
        }

        if (key is null)
        {
            return false;
        }

        chord = new KeyChord(modifiers, key);
        return true;
    }

    /// <summary>Canonical WPF <c>Key</c> member name for a token, or null when it is not a key at all.</summary>
    public static string? NormalizeKey(string raw)
    {
        if (Aliases.TryGetValue(raw, out var alias))
        {
            return alias;
        }

        if (raw.Length == 1)
        {
            var c = raw[0];
            if (char.IsAsciiLetter(c))
            {
                return char.ToUpperInvariant(c).ToString();
            }

            if (char.IsAsciiDigit(c))
            {
                return "D" + c;
            }
        }

        // f1..f24, numpad0..9, or an already-valid WPF Key name in any casing.
        if (raw.Length is >= 2 and <= 3 && (raw[0] is 'f' or 'F') && int.TryParse(raw[1..], out var f) && f is >= 1 and <= 24)
        {
            return "F" + f;
        }

        if (raw.StartsWith("numpad", StringComparison.OrdinalIgnoreCase) && raw.Length == 7 && char.IsAsciiDigit(raw[6]))
        {
            return "NumPad" + raw[6];
        }

        return KnownKeys.TryGetValue(raw, out var canonical) ? canonical : null;
    }

    /// <summary>The user-facing form: <c>Ctrl+Shift+P</c>, <c>Alt+1</c>.</summary>
    public override string ToString()
    {
        var parts = new List<string>(4);
        if (Modifiers.HasFlag(ChordModifiers.Control)) parts.Add("Ctrl");
        if (Modifiers.HasFlag(ChordModifiers.Shift)) parts.Add("Shift");
        if (Modifiers.HasFlag(ChordModifiers.Alt)) parts.Add("Alt");
        if (Modifiers.HasFlag(ChordModifiers.Win)) parts.Add("Win");

        var key = Key;
        if (Display.TryGetValue(key, out var shown))
        {
            key = shown;
        }
        else if (key.Length == 2 && key[0] == 'D' && char.IsAsciiDigit(key[1]))
        {
            key = key[1..];
        }

        parts.Add(key);
        return string.Join('+', parts);
    }
}
