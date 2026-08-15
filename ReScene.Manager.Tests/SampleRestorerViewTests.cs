using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using ReScene.App.Core.Services;
using ReScene.App.Core.ViewModels;
using ReScene.Manager.Services;
using ReScene.Manager.Views;
using ReScene.SRS;

namespace ReScene.Manager.Tests;

/// <summary>
/// Headless render tests for the ported <see cref="SampleRestorerView"/> (SRS Restorer tab — the
/// app's one genuinely editable <c>DataGrid</c>: a <c>DataGridCheckBoxColumn</c> plus an
/// editable Media File text column). The central gate is <b>zero binding errors</b> (via
/// <see cref="BindingErrorSink"/>), plus: the grid has its 5 explicit columns and reflects seeded
/// <c>SRSEntries</c> rows, the checkbox column round-trips a real click back to the entry's
/// <c>IsSelected</c>, and a side-effect-free path TextBox (Output Directory — unlike SRR/Media
/// Directory, its setter has no reactive <c>OnXxxChanged</c> hook that kicks off an async scan) is
/// two-way bound. The restore pipeline and file dialogs are inert fakes — only the view wiring is
/// exercised; a live restore is the controller's launch-smoke.
/// </summary>
public class SampleRestorerViewTests
{
    // ── Inert service doubles (the view test never runs a restore) ──

    private sealed class InertSampleRestorerService : ISampleRestorerService
    {
        public event EventHandler<SRSReconstructionProgressEventArgs>? Progress { add { } remove { } }

        public List<SRSEntryInfo> GetSRSEntries(string srrFilePath) => [];

        public Task<SRSReconstructionResult> RestoreSampleAsync(
            string srrFilePath, string srsFileName, string mediaFilePath, string outputPath, CancellationToken ct)
            => Task.FromResult(new SRSReconstructionResult(true, true, 0, 0, 0, 0, null));
    }

    private sealed class InlineUiDispatcher : IUiDispatcher
    {
        public void Invoke(Action action) => action();
        public void Post(Action action) => action();
        public void Post(Action action, UiDispatcherPriority priority) => action();
        public bool CheckAccess() => true;
    }

    private static SampleRestorerViewModel CreateViewModel() =>
        new(
            new InertSampleRestorerService(),
            new AvaloniaFileDialogService(static () => null), // headless: dialogs no-op, never block
            new InlineUiDispatcher());

    private static SampleRestorerViewModel.SRSFileEntry Entry(
        string srsFileName, string sampleFileName, string mediaFilePath, string status, bool isSelected) =>
        new()
        {
            SRSFileName = srsFileName,
            SampleFileName = sampleFileName,
            MediaFilePath = mediaFilePath,
            Status = status,
            IsSelected = isSelected,
        };

    [AvaloniaFact]
    public void SeededSRSEntries_RenderInFiveColumnGrid_WithEditableCheckboxAndMediaColumns_NoBindingErrors()
    {
        SampleRestorerViewModel vm = CreateViewModel();
        vm.SRSEntries.Add(Entry("movie.srs", "movie-sample.mkv", @"D:\media\movie.mkv", "Found", isSelected: true));
        vm.SRSEntries.Add(Entry("movie2.srs", "movie2-sample.mkv", string.Empty, "Not found", isSelected: false));

        using var sink = new BindingErrorSink();
        var window = new Window { Width = 1000, Height = 800, Content = new SampleRestorerView { DataContext = vm } };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        DataGrid grid = window.GetVisualDescendants().OfType<DataGrid>().Single(g => g.Name == "SRSEntriesGrid");

        // 5 explicit columns: editable checkbox, two read-only text columns, an editable Media File
        // text column, and a read-only Status column.
        Assert.Equal(5, grid.Columns.Count);
        // Authored template column (not DataGridCheckBoxColumn) so the cell checkbox can carry
        // Classes="fullSizeGlyph" — the unlabeled cell's glyph IS the pointer target, so it
        // opts out of the app-wide 14px glyph (see CheckBoxGlyphTests) — plus an automation
        // name in place of the missing visual label.
        Assert.IsType<DataGridTemplateColumn>(grid.Columns[0]);
        // A template column with only a CellTemplate coerces IsReadOnly=true in the GRID's
        // edit model — irrelevant here: the checkbox is directly interactive. Pin the real
        // capability instead: toggling the realized cell checkbox writes through to the VM.
        CheckBox cellCheckBox = window.GetVisualDescendants().OfType<CheckBox>()
            .First(c => c.Classes.Contains("fullSizeGlyph"));
        Assert.True(cellCheckBox.IsEnabled);
        Assert.Equal("Restore this sample",
            Avalonia.Automation.AutomationProperties.GetName(cellCheckBox));
        Assert.True(vm.SRSEntries[0].IsSelected);
        cellCheckBox.IsChecked = false;
        Dispatcher.UIThread.RunJobs();
        Assert.False(vm.SRSEntries[0].IsSelected);
        cellCheckBox.IsChecked = true;
        Dispatcher.UIThread.RunJobs();
        Assert.True(vm.SRSEntries[0].IsSelected);

        // The authored cell replaced the grid's edit-mode keyboard path, so keyboard actuation
        // must work directly: focus the checkbox and toggle it with Space.
        cellCheckBox.Focus();
        Dispatcher.UIThread.RunJobs();
        Assert.True(cellCheckBox.IsFocused);
        window.KeyPressQwerty(PhysicalKey.Space, RawInputModifiers.None);
        window.KeyReleaseQwerty(PhysicalKey.Space, RawInputModifiers.None);
        Dispatcher.UIThread.RunJobs();
        Assert.False(vm.SRSEntries[0].IsSelected);
        Assert.Equal("SRS File", grid.Columns[1].Header);
        Assert.True(grid.Columns[1].IsReadOnly);
        Assert.Equal("Sample Name", grid.Columns[2].Header);
        Assert.True(grid.Columns[2].IsReadOnly);
        Assert.Equal("Media File", grid.Columns[3].Header);
        Assert.False(grid.Columns[3].IsReadOnly);
        Assert.Equal("Status", grid.Columns[4].Header);
        Assert.True(grid.Columns[4].IsReadOnly);

        // The grid reflects the VM's SRSEntries collection, and its rows are realized.
        Assert.Same(vm.SRSEntries, grid.ItemsSource);
        int rows = window.GetVisualDescendants().OfType<DataGridRow>().Count();
        Assert.Equal(vm.SRSEntries.Count, rows);

        Assert.Empty(sink.Messages);
    }

    [AvaloniaFact]
    public void CheckboxColumn_TogglingWritesBackToEntry_NoBindingErrors()
    {
        SampleRestorerViewModel vm = CreateViewModel();
        SampleRestorerViewModel.SRSFileEntry entry =
            Entry("movie.srs", "movie-sample.mkv", @"D:\media\movie.mkv", "Found", isSelected: true);
        vm.SRSEntries.Add(entry);

        using var sink = new BindingErrorSink();
        var window = new Window { Width = 1000, Height = 800, Content = new SampleRestorerView { DataContext = vm } };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        // The checkbox column's realized cell is a real, interactive CheckBox (no double-click-to-edit
        // step needed, unlike a text column) — it's the only CheckBox in this view.
        CheckBox checkbox = window.GetVisualDescendants().OfType<CheckBox>().Single();
        Assert.True(checkbox.IsChecked);

        checkbox.IsChecked = false;
        Dispatcher.UIThread.RunJobs();

        Assert.False(entry.IsSelected);

        Assert.Empty(sink.Messages);
    }

    [AvaloniaFact]
    public void OutputDirectoryTextBox_IsTwoWayBound_NoBindingErrors()
    {
        SampleRestorerViewModel vm = CreateViewModel();

        using var sink = new BindingErrorSink();
        var window = new Window { Width = 1000, Height = 800, Content = new SampleRestorerView { DataContext = vm } };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        // Output Directory has no reactive OnXxxChanged hook (unlike SRRFilePath/MediaDirectoryPath,
        // which kick off an async SRR read / media scan), so it round-trips safely in both directions.
        TextBox output = window.GetVisualDescendants().OfType<TextBox>().Single(t => t.Name == "OutputDirTextBox");

        // view -> VM
        output.Text = @"C:\rel\output";
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(@"C:\rel\output", vm.OutputDirectoryPath);

        // VM -> view
        vm.OutputDirectoryPath = @"C:\rel\other-output";
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(@"C:\rel\other-output", output.Text);

        Assert.Empty(sink.Messages);
    }
}
