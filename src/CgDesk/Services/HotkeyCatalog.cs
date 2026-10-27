using CgDesk.Models;

namespace CgDesk.Services;

internal readonly record struct HotkeyAction(string Name, int Id, string Label);

internal readonly record struct HotkeyPlan(List<HotkeySlot> Slots, List<int> DuplicateIds);

internal static class HotkeyCatalog
{
    public static readonly HotkeyAction[] Actions =
    [
        new("1", 1, "切到窗口 1"),
        new("2", 2, "切到窗口 2"),
        new("3", 3, "切到窗口 3"),
        new("4", 4, "切到窗口 4"),
        new("5", 5, "切到窗口 5"),
        new("6", 6, "切到窗口 6"),
        new("7", 7, "切到窗口 7"),
        new("8", 8, "切到窗口 8"),
        new("9", 9, "切到窗口 9"),
        new("next", HotkeyHost.NextId, "下一个窗口"),
        new("arrange", HotkeyHost.ArrangeId, "重新接入主画面")
    ];

    public static int DefaultModifiers { get; } = (int)(NativeMethods.ModControl | NativeMethods.ModAlt);

    public static List<HotkeyChord> Normalize(IReadOnlyList<HotkeyChord>? saved)
    {
        var map = new Dictionary<string, HotkeyChord>(StringComparer.Ordinal);
        if (saved != null)
        {
            foreach (var chord in saved)
            {
                if (!string.IsNullOrWhiteSpace(chord.Action))
                {
                    map[chord.Action] = chord;
                }
            }
        }

        var result = new List<HotkeyChord>(Actions.Length);
        foreach (var action in Actions)
        {
            if (map.TryGetValue(action.Name, out var chord))
            {
                result.Add(new HotkeyChord
                {
                    Action = action.Name,
                    Modifiers = chord.Modifiers,
                    Key = chord.Key
                });
            }
            else
            {
                result.Add(CreateDefault(action.Name));
            }
        }

        return result;
    }

    public static HotkeyChord CreateDefault(string action)
    {
        return new HotkeyChord
        {
            Action = action,
            Modifiers = DefaultModifiers,
            Key = DefaultVirtualKey(action)
        };
    }

    public static int DefaultVirtualKey(string action)
    {
        return action switch
        {
            "next" => 0x27,
            "arrange" => 0x30,
            _ => 0x30 + int.Parse(action)
        };
    }

    public static HotkeyPlan Plan(IReadOnlyList<HotkeyChord> chords)
    {
        var seen = new HashSet<(int Modifiers, int Key)>();
        var slots = new List<HotkeySlot>();
        var duplicates = new List<int>();
        foreach (var action in Actions)
        {
            var chord = chords.FirstOrDefault(item => item.Action == action.Name);
            if (chord == null || chord.Key == 0 || chord.Modifiers == 0)
            {
                continue;
            }

            if (!seen.Add((chord.Modifiers, chord.Key)))
            {
                duplicates.Add(action.Id);
                continue;
            }

            slots.Add(new HotkeySlot(action.Id, (uint)chord.Modifiers, (uint)chord.Key));
        }

        return new HotkeyPlan(slots, duplicates);
    }

    public static string? DuplicateMessage(IReadOnlyList<HotkeyChord> chords)
    {
        var group = chords
            .Where(chord => chord.Key != 0 && chord.Modifiers != 0)
            .GroupBy(chord => (chord.Modifiers, chord.Key))
            .FirstOrDefault(item => item.Count() > 1);
        if (group == null)
        {
            return null;
        }

        return string.Join("、", group.Select(chord => Label(chord.Action))) + " 用了同一个热键。";
    }

    public static string Summary(IReadOnlyList<HotkeyChord> chords)
    {
        var parts = new List<string>();
        var seats = FormatSeats(chords);
        if (seats.Length > 0)
        {
            parts.Add(seats);
        }

        var next = chords.FirstOrDefault(chord => chord.Action == "next");
        if (next is { Key: > 0 })
        {
            parts.Add(Format(next) + " 下一个");
        }

        var arrange = chords.FirstOrDefault(chord => chord.Action == "arrange");
        if (arrange is { Key: > 0 })
        {
            parts.Add(Format(arrange) + " 重新接入");
        }

        return parts.Count == 0 ? "未设置热键" : string.Join("  ·  ", parts);
    }

    public static string Format(HotkeyChord chord) => Format(chord.Modifiers, chord.Key);

    public static string Format(int modifiers, int key)
    {
        if (key == 0)
        {
            return "未设置";
        }

        var mods = FormatModifiers(modifiers);
        var name = KeyName(key);
        return mods.Length == 0 ? name : mods + "+" + name;
    }

    public static string FormatModifiers(int modifiers)
    {
        var parts = new List<string>(4);
        if ((modifiers & (int)NativeMethods.ModControl) != 0)
        {
            parts.Add("Ctrl");
        }

        if ((modifiers & (int)NativeMethods.ModAlt) != 0)
        {
            parts.Add("Alt");
        }

        if ((modifiers & (int)NativeMethods.ModShift) != 0)
        {
            parts.Add("Shift");
        }

        if ((modifiers & (int)NativeMethods.ModWin) != 0)
        {
            parts.Add("Win");
        }

        return string.Join("+", parts);
    }

    public static string Label(string action)
    {
        return Actions.First(item => item.Name == action).Label;
    }

    public static string LabelForId(int id)
    {
        return Actions.First(item => item.Id == id).Label;
    }

    public static string OccupiedMessage(IEnumerable<int> ids)
    {
        return string.Join("、", ids.Select(LabelForId)) + " 被其他程序占用，换一组再保存。";
    }

    private static string FormatSeats(IReadOnlyList<HotkeyChord> chords)
    {
        var seats = Enumerable.Range(1, 9)
            .Select(number => chords.First(chord => chord.Action == number.ToString()))
            .Where(chord => chord.Key != 0 && chord.Modifiers != 0)
            .ToList();
        if (seats.Count == 0)
        {
            return "";
        }

        if (seats.Count == 9
            && seats.Select(chord => chord.Modifiers).Distinct().Count() == 1
            && seats.Select(chord => chord.Key).SequenceEqual(Enumerable.Range(0x31, 9)))
        {
            return FormatModifiers(seats[0].Modifiers) + "+1~9 切换";
        }

        if (seats.Count <= 4)
        {
            return string.Join("、", seats.Select(Format)) + " 切换";
        }

        return "切换 " + seats.Count + " 条";
    }

    private static string KeyName(int key)
    {
        return key switch
        {
            >= 0x30 and <= 0x39 => ((char)key).ToString(),
            >= 0x41 and <= 0x5A => ((char)key).ToString(),
            >= 0x70 and <= 0x87 => "F" + (key - 0x6F),
            >= 0x60 and <= 0x69 => "小键盘" + (key - 0x60),
            0x25 => "←",
            0x26 => "↑",
            0x27 => "→",
            0x28 => "↓",
            0x21 => "PageUp",
            0x22 => "PageDown",
            0x23 => "End",
            0x24 => "Home",
            0x2D => "Insert",
            0x2E => "Delete",
            0x08 => "Backspace",
            0x09 => "Tab",
            0x0D => "Enter",
            0x20 => "Space",
            0x6A => "小键盘*",
            0x6B => "小键盘+",
            0x6D => "小键盘-",
            0x6E => "小键盘.",
            0x6F => "小键盘/",
            0xBA => ";",
            0xBB => "=",
            0xBC => ",",
            0xBD => "-",
            0xBE => ".",
            0xBF => "/",
            0xC0 => "`",
            0xDB => "[",
            0xDC => "\\",
            0xDD => "]",
            0xDE => "'",
            _ => "键" + key
        };
    }
}
