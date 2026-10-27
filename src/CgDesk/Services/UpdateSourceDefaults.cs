using CgDesk.Models;

namespace CgDesk.Services;

internal static class UpdateSourceDefaults
{
    internal const string Official = "https://github.com/359134032/cg-desk-public/releases";

    internal static bool NeedsMigration(string? source)
    {
        if (string.IsNullOrWhiteSpace(source)) return true;
        if (!Uri.TryCreate(source, UriKind.Absolute, out var uri)) return false;
        var path = uri.AbsolutePath.TrimEnd('/');
        return uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase)
            && (path.Equals("/359134032/cg-desk/releases", StringComparison.OrdinalIgnoreCase)
                || path.Equals("/359134032/cg-desk/releases/latest", StringComparison.OrdinalIgnoreCase))
            || uri.Host.Equals("api.github.com", StringComparison.OrdinalIgnoreCase)
            && (path.Equals("/repos/359134032/cg-desk/releases", StringComparison.OrdinalIgnoreCase)
                || path.Equals("/repos/359134032/cg-desk/releases/latest", StringComparison.OrdinalIgnoreCase));
    }

    internal static void Apply(AppConfig config)
    {
        var changed = false;
        if (NeedsMigration(config.UpdateManifestUrl)) { config.UpdateManifestUrl = Official; changed = true; }
        if (!string.IsNullOrWhiteSpace(config.UpdateBackupManifestUrl) && NeedsMigration(config.UpdateBackupManifestUrl))
        { config.UpdateBackupManifestUrl = ""; changed = true; }
        if (changed) config.NextUpdateCheckUtc = default;
    }
}
