namespace CgDesk.Models;

public sealed class AppConfig
{
    public string LauncherPath { get; set; } = "";
    public string GamePath { get; set; } = "";
    public string GameProcessNames { get; set; } = "cg";
    public bool HotkeysEnabled { get; set; } = true;
    public List<HotkeyChord> Hotkeys { get; set; } = [];
    public bool EnhanceMainWindow { get; set; }
    public double EnhanceSharpness { get; set; } = 0.7;
    public bool? LiveSmallPreview { get; set; }
    public int GuideWidth { get; set; } = 440;
    public int GuideHeight { get; set; } = 720;
    public double GuideLeft { get; set; } = -1;
    public double GuideTop { get; set; } = -1;
    public bool GuideTopmost { get; set; }
    public bool ShowGuide { get; set; }
    public bool SettingsExpanded { get; set; } = true;
    public int SettingsLayoutVersion { get; set; }
    public bool CloseToTray { get; set; }
    public bool DarkMode { get; set; }
    public bool UpdateChecksEnabled { get; set; } = true;
    public string UpdateManifestUrl { get; set; } = "";
    public string UpdateBackupManifestUrl { get; set; } = "";
    public string UpdateChannel { get; set; } = "stable";
    public DateTimeOffset NextUpdateCheckUtc { get; set; }
    public Dictionary<string, string> HighestUpdateVersions { get; set; } = [];
}
