using System.Globalization;
using System.Text.RegularExpressions;

namespace TriffView.Preview;

// Only translate TriffView's older display syntax. AHK itself validates hotkey syntax.
internal static class AhkGesture
{
    public static string UpgradeLegacyVirtualKey(string gesture)
    {
        // Prior to AHK, bare VK70 meant decimal 70. Mark old stored bindings explicitly
        // so new AHK vk70 bindings can use hexadecimal without changing an existing key.
        var match = Regex.Match(gesture.Trim(), @"^(?:(?:Control|Ctrl|Shift|Alt|Windows|Win|NoRepeat)\s*\+\s*)*VK(\d+)$", RegexOptions.IgnoreCase);
        return match.Success ? match.Value.Insert(match.Groups[1].Index, "_") : gesture;
    }

    public static string Translate(string gesture)
    {
        var value = gesture.Trim();
        var prefixes = "";
        while (value.Length > 1 && "*~$".Contains(value[0]))
        {
            prefixes += value[0];
            value = value[1..];
        }
        while (Regex.Match(value, @"^(Control|Ctrl|Shift|Alt|Windows|Win|NoRepeat)\s*\+\s*(.+)$", RegexOptions.IgnoreCase) is { Success: true } match)
        {
            prefixes += match.Groups[1].Value.ToLowerInvariant() switch
            {
                "control" or "ctrl" => "^", "shift" => "+", "alt" => "!", "win" or "windows" => "#", _ => "",
            };
            value = match.Groups[2].Value;
        }
        // Legacy aliases emitted by the recorder. Never reinterpret AHK modifier symbols.
        value = value.ToLowerInvariant() switch
        {
            "grave" or "backquote" or "tilde" or "oem3" or "oemtilde" => "vkC0",
            "plus" or "oemplus" => "vkBB", "minus" or "oemminus" => "vkBD",
            "comma" or "oemcomma" => "vkBC", "period" or "oemperiod" => "vkBE",
            "openbracket" or "openbrackets" or "leftbracket" or "leftbrackets" or "oemopenbrackets" => "vkDB",
            "closebracket" or "closebrackets" or "rightbracket" or "rightbrackets" or "oemclosebrackets" => "vkDD",
            "semicolon" or "oemsemicolon" => "vkBA", "slash" or "question" or "oemquestion" => "vkBF",
            "backslash" or "pipe" or "oempipe" => "vkDC", "quote" or "quotes" or "apostrophe" or "oemquotes" => "vkDE",
            "oem8" => "vkDF", "oem102" or "oembackslash" => "vkE2",
            "capital" => "CapsLock", "snapshot" => "PrintScreen", "apps" or "menu" or "contextmenu" => "AppsKey",
            "multiply" => "NumpadMult", "numpadmultiply" => "NumpadMult", "add" => "NumpadAdd",
            "subtract" or "numpadsubtract" => "NumpadSub", "divide" or "numpaddivide" => "NumpadDiv",
            "decimal" or "numpaddecimal" => "NumpadDot", "separator" or "numpadseparator" => "vk6C",
            "scroll" => "ScrollLock", "back" => "Backspace", "return" => "Enter", "next" => "PgDn", "prior" => "PgUp",
            "arrowup" => "Up", "arrowdown" => "Down", "arrowleft" => "Left", "arrowright" => "Right",
            "cancel" => "vk03",
            "lcontrolkey" => "LControl", "rcontrolkey" => "RControl", "controlkey" => "Control",
            "lshiftkey" => "LShift", "rshiftkey" => "RShift", "shiftkey" => "Shift",
            "lmenu" => "LAlt", "rmenu" => "RAlt",
            "browserback" => "Browser_Back", "browserforward" => "Browser_Forward", "browserrefresh" => "Browser_Refresh",
            "browserstop" => "Browser_Stop", "browsersearch" => "Browser_Search", "browserfavorites" => "Browser_Favorites",
            "browserhome" => "Browser_Home", "volumemute" => "Volume_Mute", "volumedown" => "Volume_Down", "volumeup" => "Volume_Up",
            "medianexttrack" => "Media_Next", "mediaprevioustrack" => "Media_Prev", "mediastop" => "Media_Stop", "mediaplaypause" => "Media_Play_Pause",
            "launchmail" => "Launch_Mail", "selectmedia" => "Launch_Media", "launchapplication1" => "Launch_App1", "launchapplication2" => "Launch_App2",
            _ => value,
        };
        // Preserve AHK vkNN as hexadecimal; legacy VK_ and 0x spellings remain accepted.
        if (Regex.Match(value, @"^(?:VK_(?:0x)?|0x)([0-9a-f]+)$", RegexOptions.IgnoreCase) is { Success: true } vk)
        {
            var digits = vk.Groups[1].Value;
            var hex = value.Contains("0x", StringComparison.OrdinalIgnoreCase) || digits.Any(char.IsLetter);
            if (uint.TryParse(digits, hex ? NumberStyles.HexNumber : NumberStyles.Integer, CultureInfo.InvariantCulture, out var code))
                value = $"vk{code:X2}";
        }
        return prefixes + value;
    }

    public static bool IsNoRepeat(string gesture) => Regex.IsMatch(gesture, @"(?:^[*~$]*|\+)NoRepeat\s*\+", RegexOptions.IgnoreCase);

    // Older settings allow comma-separated lists in one string. Preserve the AHK comma key.
    public static IEnumerable<string> SplitList(string? text)
    {
        var value = text ?? "";
        var start = 0;
        for (var index = 0; index < value.Length; index++)
        {
            if (value[index] != ',') continue;
            var before = value[start..index].Trim();
            var withoutModifiers = Regex.Replace(before, @"^(?:[*~$<>^!+#]|(?:Control|Ctrl|Shift|Alt|Windows|Win|NoRepeat)\s*\+\s*)*", "", RegexOptions.IgnoreCase);
            if (withoutModifiers.Length == 0 || before.EndsWith('&')) continue;
            yield return before;
            start = index + 1;
        }
        if (value[start..].Trim() is { Length: > 0 } last) yield return last;
    }

    public static string Identity(string gesture)
    {
        var value = Translate(gesture);
        var modifiers = new List<string>();
        var index = 0;
        while (index < value.Length - 1)
        {
            var symbol = value[index];
            // AHK treats these as options of the same hotkey, not separate triggers.
            if (symbol is '~' or '$') { index++; continue; }
            if (symbol is '<' or '>' && index + 2 < value.Length && "^!+#".Contains(value[index + 1]))
            {
                modifiers.Add(value.Substring(index, 2));
                index += 2;
            }
            else if ("*^!+#".Contains(symbol)) { modifiers.Add(symbol.ToString()); index++; }
            else break;
        }
        return string.Concat(modifiers.Order(StringComparer.Ordinal)) + value[index..];
    }
}
