using ReScene.App.Core.Models;
using ReScene.App.Core.Services;
using ReScene.App.Core.ViewModels;
using ReScene.SRR;

namespace ReScene.App.Core.Tests;

/// <summary>
/// The Integrity Verify Result panel toggles <c>IsVisible</c>, so it cannot announce its own arrival:
/// an element that is not realized when its text lands gives an assistive technology no transition to
/// notice. A screen-reader user pressing Verify was told nothing, and nothing rescued it: by
/// inspection, neither the view nor its code-behind moves focus into the panel when it appears.
/// <para>
/// The announcement is a one-line verdict rather than the panel's own text, which carries a line per
/// issue; a polite live region would read all of them before the user could act.
/// </para>
/// </summary>
public class InspectorVerifyAnnouncementTests
{
    private sealed class ScriptedVerifyService(Func<SRRVerifyResult> next) : ISRRVerifyService
    {
        public Task<SRRVerifyResult> VerifyAsync(string srrFilePath, CancellationToken ct = default) =>
            Task.FromResult(next());
    }

    private sealed class UnusedExportService : IPropertyExportService
    {
        public Task ExportSelectedAsync(string o, TreeNodeViewModel n, IEnumerable<PropertyItem> p, CancellationToken ct = default) => throw new NotSupportedException();
        public Task ExportTreeAsync(string o, IEnumerable<TreeNodeViewModel> r, CancellationToken ct = default) => throw new NotSupportedException();
    }

    private sealed class UnusedEditingService : ISRREditingService
    {
        public void AddStoredFiles(string p, IReadOnlyList<(string StoredName, string FilePath)> f) => throw new NotSupportedException();
        public void RemoveStoredFiles(string p, IReadOnlyList<string> n) => throw new NotSupportedException();
        public Task RenameStoredFileAsync(string p, string o, string n, CancellationToken ct = default) => throw new NotSupportedException();
        public Task MoveStoredFileAsync(string p, string n, int o, CancellationToken ct = default) => throw new NotSupportedException();
        public IReadOnlyList<StoredFileInfo> GetStoredFiles(string p) => [];
        public Task<string?> ExtractStoredFileAsync(string p, string d, string n, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<byte[]?> ReadStoredFileBytesAsync(string p, string n, CancellationToken ct = default) => throw new NotSupportedException();
    }

    private sealed class UnusedPreviewService : IImagePreviewService
    {
        public void Preview(byte[] data, string fileName) => throw new NotSupportedException();
    }

    private static InspectorViewModel CreateVm(Func<SRRVerifyResult> verify)
    {
        var vm = new InspectorViewModel(new NoOpFileDialogService(), new UnusedEditingService(),
            new ScriptedVerifyService(verify), new UnusedExportService(), new UnusedPreviewService());
        vm.LoadedFilePath = @"X:\input\release.srr";
        return vm;
    }

    private static SRRVerifyResult Clean() =>
        new() { IsValid = true, Issues = [], BlocksScanned = 12, FileSize = 3456 };

    private static SRRVerifyResult WithIssues(int count) =>
        new()
        {
            IsValid = false,
            Issues = [.. Enumerable.Range(0, count).Select(i => new SRRVerifyIssue
            {
                Severity = SRRVerifyIssueSeverity.Error,
                Offset = i,
                Message = $"issue {i}",
            })],
            BlocksScanned = 12,
            FileSize = 3456,
        };

    [Fact]
    public async Task CleanVerify_AnnouncesTheVerdict_NotTheWholeReport()
    {
        InspectorViewModel vm = CreateVm(Clean);

        await vm.VerifyIntegrityCommand.ExecuteAsync(null);

        Assert.Equal("Integrity verify: no errors found.", vm.VerifyAnnouncement);
        Assert.True(vm.IsVerifyResultVisible);

        // The panel's own text still carries the detail — the announcement is a summary OF it,
        // never a replacement for it.
        Assert.Contains("Blocks scanned: 12", vm.VerifyResultText, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(1, "Integrity verify: errors detected, 1 issue.")]
    [InlineData(3, "Integrity verify: errors detected, 3 issues.")]
    public async Task FailedVerify_AnnouncesTheIssueCount(int issues, string expected)
    {
        InspectorViewModel vm = CreateVm(() => WithIssues(issues));

        await vm.VerifyIntegrityCommand.ExecuteAsync(null);

        Assert.Equal(expected, vm.VerifyAnnouncement);
    }

    /// <summary>
    /// Verifying the same file twice produces a byte-identical verdict, which the generated setter
    /// suppresses as an equal value — so without a clear first, the second Verify would say nothing.
    /// The repeat is the obvious user action: press Verify, read, press Verify again.
    /// </summary>
    [Fact]
    public async Task RepeatVerifyOfTheSameFile_ReAnnouncesViaClearThenSetTransition()
    {
        InspectorViewModel vm = CreateVm(Clean);
        await vm.VerifyIntegrityCommand.ExecuteAsync(null);

        var transitions = new List<string>();
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(vm.VerifyAnnouncement))
            { transitions.Add(vm.VerifyAnnouncement); }
        };

        await vm.VerifyIntegrityCommand.ExecuteAsync(null);

        Assert.Equal([string.Empty, "Integrity verify: no errors found."], transitions);
    }
}
