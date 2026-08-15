using ReScene.App.Core.ViewModels.Reconstruction;
using ReScene.Core;

namespace ReScene.App.Core.Tests;

public sealed class WinRARVersionScannerTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "wrvs-" + Guid.NewGuid().ToString("N"));

    public WinRARVersionScannerTests()
    {
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try
        { Directory.Delete(_root, recursive: true); }
        catch { /* best effort */ }
    }

    private void MakeVersion(string folderName, bool withRARExe)
    {
        string dir = Path.Combine(_root, folderName);
        Directory.CreateDirectory(dir);
        if (withRARExe)
        {
            // The scanner looks for the platform's console binary (rar.exe on Windows, rar elsewhere),
            // so create whichever name this OS resolves to — keeps the test valid on every platform.
            File.WriteAllText(Path.Combine(dir, RarExecutable.FileName), "stub");
        }
    }

    [Fact]
    public void Scan_NullOrMissingFolder_ReturnsEmpty()
    {
        Assert.Empty(WinRARVersionScanner.Scan(null));
        Assert.Empty(WinRARVersionScanner.Scan(""));
        Assert.Empty(WinRARVersionScanner.Scan(Path.Combine(_root, "does-not-exist")));
    }

    [Fact]
    public void Scan_IncludesOnlyFoldersWithRARExeAndParseableName_SortedAscending()
    {
        MakeVersion("winrar-624", withRARExe: true);
        MakeVersion("winrar-560", withRARExe: true);
        MakeVersion("winrar-590", withRARExe: false);  // no rar.exe -> excluded
        MakeVersion("winrar-beta", withRARExe: true);  // unparseable -> excluded (no throw)

        IReadOnlyList<InstalledRARVersion> result = WinRARVersionScanner.Scan(_root);

        int[] expectedVersions = [560, 624];
        Assert.Equal(expectedVersions, result.Select(r => r.Version).ToArray());
        Assert.Equal("winrar-560", result[0].FolderName);
    }

    [Fact]
    public void Scan_TwoDigitName_NormalisedToThreeDigits()
    {
        MakeVersion("winrar-56", withRARExe: true);

        IReadOnlyList<InstalledRARVersion> result = WinRARVersionScanner.Scan(_root);

        Assert.Single(result);
        Assert.Equal(560, result[0].Version);
    }

    [Fact]
    public void Scan_AcceptsLinuxAndMacOSTarballFolderNames()
    {
        // Regression guard for the reported Linux bug: the standard *nix tarball folder names must be
        // recognised — rarlinux-…/rarosx-…, both concatenated ("611") and dotted ("5.5.0") versions.
        MakeVersion("rarlinux-x64-611", withRARExe: true);
        MakeVersion("rarlinux-x64-5.5.0", withRARExe: true);
        MakeVersion("rarosx-3.1.0", withRARExe: true);

        IReadOnlyList<InstalledRARVersion> result = WinRARVersionScanner.Scan(_root);

        int[] expectedVersions = [310, 550, 611];
        Assert.Equal(expectedVersions, result.Select(r => r.Version).ToArray());
    }

    [Fact]
    public void Scan_SameVersionVariants_CarryDistinguishingTags()
    {
        MakeVersion("winrar-250", withRARExe: true);
        MakeVersion("winrar-250-beta1", withRARExe: true);

        IReadOnlyList<InstalledRARVersion> result = WinRARVersionScanner.Scan(_root);

        Assert.Equal(2, result.Count);
        Assert.All(result, r => Assert.Equal(250, r.Version));
        Assert.Contains(result, r => r.FolderName == "winrar-250" && r.Tag.Length == 0);
        Assert.Contains(result, r => r.FolderName == "winrar-250-beta1" && r.Tag == "beta1");
    }
}
