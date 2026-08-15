using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using ReScene.App.Core.Models;
using ReScene.App.Core.Services;
using ReScene.App.Core.ViewModels;
using ReScene.Manager.Services;
using ReScene.Manager.Views;
using ReScene.SRR;

namespace ReScene.Manager.Tests;

/// <summary>
/// Two of the Inspector's outcomes toggle <c>IsVisible</c> and so could not announce themselves: the
/// custom-packer warning bar, and the Integrity Verify Result panel. The verify panel is the starker
/// case — by inspection, neither the view nor its code-behind moves focus into it, so pressing Verify changed the
/// screen and told a screen-reader user nothing whatsoever.
/// <para>
/// Both now ride always-in-tree live lines sharing the File caption's row.
/// </para>
/// </summary>
public class InspectorAnnouncementTests
{
    private const string Warning =
        "Custom RAR packer detected — file size fields may be unreliable. Known groups: RELOADED, HI2U, QCF.";

    private const string Verdict = "Integrity verify: errors detected, 3 issues.";

    private sealed class UnusedEditingService : ISRREditingService
    {
        public void AddStoredFiles(string p, IReadOnlyList<(string StoredName, string FilePath)> f) { }
        public void RemoveStoredFiles(string p, IReadOnlyList<string> n) { }
        public Task RenameStoredFileAsync(string p, string o, string n, CancellationToken ct = default) => Task.CompletedTask;
        public Task MoveStoredFileAsync(string p, string n, int o, CancellationToken ct = default) => Task.CompletedTask;
        public IReadOnlyList<StoredFileInfo> GetStoredFiles(string p) => [];
        public Task<string?> ExtractStoredFileAsync(string p, string d, string n, CancellationToken ct = default) => Task.FromResult<string?>(null);
        public Task<byte[]?> ReadStoredFileBytesAsync(string p, string n, CancellationToken ct = default) => Task.FromResult<byte[]?>(null);
    }

    private sealed class UnusedVerifyService : ISRRVerifyService
    {
        public Task<SRRVerifyResult> VerifyAsync(string p, CancellationToken ct = default) =>
            Task.FromResult(new SRRVerifyResult { IsValid = true, Issues = [], BlocksScanned = 0, FileSize = 0 });
    }

    private sealed class UnusedExportService : IPropertyExportService
    {
        public Task ExportSelectedAsync(string o, TreeNodeViewModel n, IEnumerable<PropertyItem> p, CancellationToken ct = default) => Task.CompletedTask;
        public Task ExportTreeAsync(string o, IEnumerable<TreeNodeViewModel> r, CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class UnusedPreviewService : IImagePreviewService
    {
        public void Preview(byte[] d, string f) { }
    }

    private static InspectorViewModel CreateVm() =>
        new(new AvaloniaFileDialogService(static () => null), new UnusedEditingService(),
            new UnusedVerifyService(), new UnusedExportService(), new UnusedPreviewService(), settingsService: null);

    [AvaloniaFact]
    public void BothOutcomes_AnnounceThroughAlwaysInTreeLiveLines_AtNoLayoutCost()
    {
        InspectorViewModel vm = CreateVm();
        var window = new Window { Width = 1200, Height = 900, Content = new InspectorView { DataContext = vm } };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        try
        {
            TextBlock warning = Find(window, "WarningStatus");
            TextBlock verify = Find(window, "VerifyStatus");

            foreach (TextBlock line in (TextBlock[])[warning, verify])
            {
                Assert.Equal(AutomationLiveSetting.Polite, AutomationProperties.GetLiveSetting(line));

                // Idle is "no text" — null for WarningMessage, which is string?, and empty for
                // VerifyAnnouncement. Either renders nothing, which is what lets the line sit in the
                // tree permanently without showing.
                Assert.True(string.IsNullOrEmpty(line.Text), $"idle live line already reads \"{line.Text}\"");
                Assert.True(line.IsEffectivelyVisible, "the live line must be realized BEFORE its text arrives");
            }

            var row = warning.GetVisualAncestors().OfType<DockPanel>().First();
            TextBlock caption = row.Children.OfType<TextBlock>().Single(t => t.Text == "File");
            double idleRowHeight = row.Bounds.Height;
            Assert.Equal(caption.Bounds.Height, idleRowHeight, precision: 1);

            vm.WarningMessage = Warning;
            vm.VerifyAnnouncement = Verdict;
            Dispatcher.UIThread.RunJobs();

            Assert.Equal(Warning, warning.Text);
            Assert.Equal(Verdict, verify.Text);
            Assert.Equal(idleRowHeight, row.Bounds.Height, precision: 1);

            // Long text in BOTH at once is the specific failure the sibling log-header row records:
            // two presenters sharing one row can starve each other unless the split is fixed.
            Assert.True(warning.Bounds.Width > 0, "the warning line was squeezed to nothing by the verdict beside it");
            Assert.True(verify.Bounds.Width > 0, "the verdict line was squeezed to nothing by the warning beside it");
        }
        finally { window.Close(); }
    }

    /// <summary>
    /// The warning bar keeps its own visibility toggle and stays announcement-free — putting
    /// <c>LiveSetting</c> there instead would place it on an element that is not in the tree when the
    /// text arrives, which is the whole defect.
    /// </summary>
    [AvaloniaFact]
    public void TheVisibleWarningBar_StaysAnnouncementFree()
    {
        InspectorViewModel vm = CreateVm();
        var window = new Window { Width = 1200, Height = 900, Content = new InspectorView { DataContext = vm } };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        try
        {
            vm.WarningMessage = Warning;
            Dispatcher.UIThread.RunJobs();

            Border bar = window.GetVisualDescendants().OfType<Border>()
                .Single(b => b.Child is TextBlock t && t.Text == Warning);
            Assert.True(bar.IsVisible);
            Assert.Equal(AutomationLiveSetting.Off, AutomationProperties.GetLiveSetting(bar));
            Assert.Equal(AutomationLiveSetting.Off, AutomationProperties.GetLiveSetting((TextBlock)bar.Child!));
        }
        finally { window.Close(); }
    }

    private static TextBlock Find(Window window, string name) =>
        window.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Name == name);
}
