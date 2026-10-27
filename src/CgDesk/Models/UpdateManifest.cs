using System.Text.RegularExpressions;

namespace CgDesk.Models;

public sealed record UpdateEnvelope(string KeyId, string Payload, string Signature);
public sealed record UpdateManifest(int SchemaVersion, string Product, string Channel, string Version,
    DateTimeOffset PublishedAt, string ReleaseNotes, UpdatePackage[] Packages);
public sealed record UpdatePackage(string Rid, int MinWindowsBuild, long Size, string Sha256, string[] Urls);

internal sealed class ReleaseVersion : IComparable<ReleaseVersion>
{
    private static readonly Regex Pattern = new(@"^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(?:-([0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*))?(?:\+[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*)?$", RegexOptions.CultureInvariant);
    private readonly string[] _numbers;
    private readonly string[] _pre;
    private ReleaseVersion(string[] numbers, string[] pre) { _numbers = numbers; _pre = pre; }

    public static ReleaseVersion Parse(string text)
    {
        if (text.Length > 100) throw new FormatException("版本号过长。");
        var match = Pattern.Match(text);
        if (!match.Success) throw new FormatException("版本号不符合语义化版本格式。");
        var pre = match.Groups[4].Success ? match.Groups[4].Value.Split('.') : [];
        if (pre.Any(value => value.All(char.IsAsciiDigit) && value.Length > 1 && value[0] == '0'))
            throw new FormatException("预发布编号不能以零开头。");
        return new ReleaseVersion([match.Groups[1].Value, match.Groups[2].Value, match.Groups[3].Value], pre);
    }

    public bool IsPrerelease => _pre.Length > 0;
    public int CompareTo(ReleaseVersion? other)
    {
        if (other == null) return 1;
        for (var i = 0; i < 3; i++) { var result = Number(_numbers[i], other._numbers[i]); if (result != 0) return result; }
        if (_pre.Length == 0 || other._pre.Length == 0) return other._pre.Length.CompareTo(_pre.Length);
        for (var i = 0; i < Math.Min(_pre.Length, other._pre.Length); i++)
        {
            var a = _pre[i]; var b = other._pre[i];
            var aNumeric = a.All(char.IsAsciiDigit); var bNumeric = b.All(char.IsAsciiDigit);
            var result = aNumeric && bNumeric ? Number(a, b) : aNumeric != bNumeric ? (aNumeric ? -1 : 1) : string.CompareOrdinal(a, b);
            if (result != 0) return result;
        }
        return _pre.Length.CompareTo(other._pre.Length);
    }
    private static int Number(string a, string b) => a.Length == b.Length ? string.CompareOrdinal(a, b) : a.Length.CompareTo(b.Length);
}
