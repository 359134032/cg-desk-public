namespace CgDesk.Models;

public sealed class HotkeyChord
{
    public string Action { get; set; } = "";
    public int Modifiers { get; set; }
    public int Key { get; set; }
}
