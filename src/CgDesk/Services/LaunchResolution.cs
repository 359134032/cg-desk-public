using CgDesk.Models;

namespace CgDesk.Services;

internal sealed record LaunchResolution(int Width, int Height)
{
    public bool Native => Width == 0 && Height == 0;
    public string Label => Native ? "跟随游戏原生设置（关闭实验）" : $"{Width} × {Height} · 实验";
    public string Description => Native ? "使用游戏自身设置，不加载实验组件。"
        : "扩大地图视口；远处角色仍可能受服务端下发范围限制。";
    public override string ToString() => Label;
}

internal static class LaunchResolutions
{
    internal static readonly (int Width, int Height)[] Common =
    [
        (640,480), (800,600), (1024,768), (1152,864), (1280,960), (1400,1050), (1440,1080), (1600,1200)
    ];

    internal static bool Supported(int width, int height) => Common.Contains((width, height))
        && width * 3 == height * 4 && width <= 1600 && height <= 1200;

    internal static (int Width, int Height) FitFourByThree(double width, double height)
    {
        if (!double.IsFinite(width) || !double.IsFinite(height) || width < 8 || height < 6) return (0,0);
        var unit = (int)Math.Min(width / 4, height / 3) & ~1;
        return (unit * 4, unit * 3);
    }

    internal static IReadOnlyList<LaunchResolution> ForMonitor(int width, int height) =>
        new[] { new LaunchResolution(0, 0) }.Concat(Common.Where(size => size.Width <= width && size.Height <= height)
            .Select(size => new LaunchResolution(size.Width, size.Height))).ToArray();

    internal static LaunchResolution Select(AppConfig config, IReadOnlyList<LaunchResolution> choices)
    {
        // Earlier versions saved zero as their default. Migrate it once; an
        // explicit native choice saved by this version remains off on restart.
        if (config.LaunchResolutionSettingsVersion < 1)
        {
            if (config.LaunchWidth == 0 && config.LaunchHeight == 0)
                config.LaunchWidth = config.LaunchHeight = -1;
            config.LaunchResolutionSettingsVersion = 1;
        }
        return choices.FirstOrDefault(mode => mode.Width == config.LaunchWidth && mode.Height == config.LaunchHeight)
            ?? choices.Last();
    }

    internal static (double Width, double Height) FitDesk(double gameWidth, double gameHeight,
        double chromeWidth, double chromeHeight, double workWidth, double workHeight) =>
        (Math.Min(workWidth, Math.Max(Math.Min(760, workWidth), gameWidth + Math.Max(0, chromeWidth))),
         Math.Min(workHeight, Math.Max(Math.Min(560, workHeight), gameHeight + Math.Max(0, chromeHeight))));
}
