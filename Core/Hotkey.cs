using System;
using System.Collections.Generic;
using System.Windows.Input;

namespace SpawnTimer.Core;

/// <summary>A key plus modifiers, stored as text like "Ctrl+Alt+1" or "Num5".</summary>
public readonly record struct Hotkey(ModifierKeys Modifiers, Key Key)
{
    public bool IsEmpty => Key == Key.None;

    public static Hotkey Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return default;
        var mods = ModifierKeys.None;
        var key = Key.None;
        foreach (var raw in text.Split('+'))
        {
            var token = raw.Trim();
            if (token.Length == 0) continue;
            switch (token.ToLowerInvariant())
            {
                case "ctrl":
                case "control":
                    mods |= ModifierKeys.Control;
                    break;
                case "alt":
                    mods |= ModifierKeys.Alt;
                    break;
                case "shift":
                    mods |= ModifierKeys.Shift;
                    break;
                case "win":
                    mods |= ModifierKeys.Windows;
                    break;
                default:
                    key = ParseKey(token);
                    break;
            }
        }
        return key == Key.None ? default : new Hotkey(mods, key);
    }

    public override string ToString()
    {
        if (IsEmpty) return "";
        var parts = new List<string>(5);
        if (Modifiers.HasFlag(ModifierKeys.Control)) parts.Add("Ctrl");
        if (Modifiers.HasFlag(ModifierKeys.Alt)) parts.Add("Alt");
        if (Modifiers.HasFlag(ModifierKeys.Shift)) parts.Add("Shift");
        if (Modifiers.HasFlag(ModifierKeys.Windows)) parts.Add("Win");
        parts.Add(KeyName(Key));
        return string.Join("+", parts);
    }

    private static string KeyName(Key key) => key switch
    {
        >= Key.D0 and <= Key.D9 => ((char)('0' + (key - Key.D0))).ToString(),
        >= Key.NumPad0 and <= Key.NumPad9 => "Num" + (key - Key.NumPad0),
        _ => key.ToString(),
    };

    private static Key ParseKey(string token)
    {
        if (token.Length == 1 && char.IsDigit(token[0])) return Key.D0 + (token[0] - '0');
        if (token.Length == 4 && token.StartsWith("Num", StringComparison.OrdinalIgnoreCase) && char.IsDigit(token[3]))
            return Key.NumPad0 + (token[3] - '0');
        return Enum.TryParse<Key>(token, ignoreCase: true, out var key) ? key : Key.None;
    }
}
