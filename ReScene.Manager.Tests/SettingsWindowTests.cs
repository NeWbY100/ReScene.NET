using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using ReScene.App.Core.Models;
using ReScene.App.Core.Services;
using ReScene.App.Core.ViewModels;
using ReScene.Manager.Services;
using ReScene.Manager.Views;

namespace ReScene.Manager.Tests;

/// <summary>
/// Headless render/behavior tests for the ported <see cref="SettingsWindow"/>. The central gate is
/// <b>zero binding errors</b> (via <see cref="BindingErrorSink"/>) when the window renders against a
/// real <see cref="SettingsViewModel"/>, plus the key inputs actually being two-way bound (text/int
/// fields and the Beginner/Advanced radios). Live dialog interaction — Save actually persisting to
/// disk, Browse opening a real folder picker — is the controller's Phase-4 launch-smoke, not
/// exercised here.
/// </summary>
/// <remarks>
/// Shares the "AppDataConfig" collection with <see cref="AppDataConfigTests"/>,
/// <see cref="AppInfoTests"/> and <see cref="CompositionRootTests"/>: each test here points
/// <see cref="AppDataConfig.FolderName"/> at a unique temp folder for a real
/// <see cref="AppSettingsService"/>, so none of the four classes may run concurrently.
/// </remarks>
[Collection("AppDataConfig")]
public class SettingsWindowTests
{
    private static SettingsViewModel CreateViewModel(AppSettings? seed = null)
    {
        var settingsService = new AppSettingsService();
        if (seed is not null)
        {
            settingsService.Save(seed);
        }

        var fileDialog = new AvaloniaFileDialogService(static () => null);
        return new SettingsViewModel(settingsService, fileDialog);
    }

    private static string UseTempAppDataFolder()
    {
        string tempFolder = $"ReScene.Manager.Tests-{Guid.NewGuid():N}";
        AppDataConfig.FolderName = tempFolder;
        return tempFolder;
    }

    private static void CleanUpTempAppDataFolder(string tempFolder)
    {
        string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), tempFolder);
        if (Directory.Exists(dir))
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    /// <summary>
    /// Selects the settings tab at <paramref name="index"/> and pumps the dispatcher. Unselected tab
    /// content is not materialized, so every control lookup must select its tab first
    /// (0 Interface, 1 General, 2 Inspector &amp; Compare, 3 RAR Reconstruction).
    /// </summary>
    private static TabControl SelectTab(Window window, int index)
    {
        TabControl tabs = window.GetVisualDescendants().OfType<TabControl>().Single();
        tabs.SelectedIndex = index;
        Dispatcher.UIThread.RunJobs();
        return tabs;
    }

    /// <summary>
    /// All three Browse buttons in this window announced the bare "Browse...", which is the worst
    /// case of the app-wide defect: three identical announcements in ONE window, for three
    /// different folders. Each now says which folder.
    /// <para>
    /// LABEL-IN-NAME, and the reason it needs stating here specifically: these are the only Browse
    /// buttons in the app whose visible Content carries a trailing ellipsis ("Browse..."). WCAG
    /// 2.5.3 asks the accessible name to contain the visible label, and "Browse for default output
    /// directory" does not contain the literal string "Browse...". The ellipsis is excluded from
    /// that containment as a conventional affordance marker meaning "opens a dialog" rather than
    /// part of the label's words — which is not a liberty invented here: CreatorView's folder
    /// picker reads "Browse folder…" and has long been named "Browse folder for release input",
    /// with <c>CreatorViewFolderBindingTests</c> asserting containment of "Browse folder" and not
    /// the ellipsis. Same rule, asserted the same way below.
    /// </para>
    /// <para>
    /// The WinRAR one is VERBATIM the Reconstructor's own name for the same kind of target, since
    /// this setting is the default that pre-fills that very field (WCAG 3.2.4). The output one
    /// deliberately is NOT — it picks the default for reconstruction OUTPUT, and the window's own
    /// caption calls it the "Reconstruction output folder".
    /// </para>
    /// </summary>
    [AvaloniaFact]
    public void BrowseButtons_AnnounceWhichFolder_AndContainTheirVisibleLabel()
    {
        // AppDataConfig.FolderName is a process-wide static, so the original MUST be restored or
        // the next test in this collection reads the temp folder instead (which is exactly what
        // the first version of this test did to AppDataConfigTests).
        string originalFolder = AppDataConfig.FolderName;
        string tempFolder = UseTempAppDataFolder();
        try
        {
            SettingsViewModel vm = CreateViewModel();
            var window = new SettingsWindow { DataContext = vm };
            window.Show();
            Dispatcher.UIThread.RunJobs();
            try
            {
                // Unselected tab content is not materialized, so each button's own tab is selected
                // before it is looked up (1 General, 3 RAR Reconstruction).
                SelectTab(window, 1);
                AssertBrowse(window, vm.BrowseOutputDirCommand, "Browse for default output directory");

                SelectTab(window, 3);
                AssertBrowse(window, vm.BrowseReconstructWinRARCommand, "Browse for WinRAR versions folder");
                AssertBrowse(window, vm.BrowseReconstructOutputCommand, "Browse for reconstruction output folder");
            }
            finally { window.Close(); }
        }
        finally
        {
            AppDataConfig.FolderName = originalFolder;
            CleanUpTempAppDataFolder(tempFolder);
        }
    }

    /// <summary>
    /// Asserts one Browse button's literal name, its visible Content, and the Label-in-Name
    /// containment that licenses the shared phrasing despite the trailing ellipsis. Resolved by the
    /// bound command, never by the name under test.
    /// </summary>
    private static void AssertBrowse(Window window, System.Windows.Input.ICommand command, string expectedName)
    {
        Button button = window.GetVisualDescendants().OfType<Button>()
            .Single(b => ReferenceEquals(b.Command, command));

        Assert.Equal("Browse...", button.Content as string);
        string name = Avalonia.Automation.Peers.ControlAutomationPeer.CreatePeerForElement(button).GetName();
        Assert.Equal(expectedName, name);
        Assert.Contains("Browse", name, StringComparison.Ordinal);
    }

    [AvaloniaFact]
    public void Window_IsResizable_WithCenteredFooterButtons()
    {
        string originalFolder = AppDataConfig.FolderName;
        string tempFolder = UseTempAppDataFolder();
        try
        {
            using var sink = new BindingErrorSink();
            var window = new SettingsWindow(CreateViewModel());
            window.Show();
            Dispatcher.UIThread.RunJobs();
            try
            {
                // Resizable with a sane floor; content scrolls when shrunk below natural height.
                Assert.True(window.CanResize);
                Assert.Equal(560, window.MinWidth);
                Assert.NotNull(window.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault());

                // Cancel/Save carry MinWidth=75, so the global WPF-parity centering style (Fluent
                // sets no content alignment → labels would hug the left edge) must apply to them.
                Button[] footer = [.. window.GetVisualDescendants().OfType<Button>()
                    .Where(b => Equals(b.Content, "Cancel") || Equals(b.Content, "Save"))];
                Assert.Equal(2, footer.Length);
                Assert.All(footer, b =>
                    Assert.Equal(Avalonia.Layout.HorizontalAlignment.Center, b.HorizontalContentAlignment));
                Assert.Empty(sink.Messages);
            }
            finally
            {
                window.Close();
            }
        }
        finally
        {
            AppDataConfig.FolderName = originalFolder;
            CleanUpTempAppDataFolder(tempFolder);
        }
    }

    [AvaloniaFact]
    public void Renders_ReflectsViewModelValues_NoBindingErrors()
    {
        string originalFolder = AppDataConfig.FolderName;
        string tempFolder = UseTempAppDataFolder();
        try
        {
            SettingsViewModel vm = CreateViewModel(new AppSettings
            {
                DefaultAppName = "seeded-app",
                RecentFilesLimit = 42,
                MKVMaxElements = 5000,
                Mode = UserMode.Advanced,
            });

            using var sink = new BindingErrorSink();
            var window = new SettingsWindow(vm);
            window.Show();
            Dispatcher.UIThread.RunJobs();

            // Interface tab (default): the seeded Advanced mode is reflected in the radios.
            RadioButton[] radios = [.. window.GetVisualDescendants().OfType<RadioButton>()];
            RadioButton beginner = radios.Single(r => (string?)r.Content == "Beginner");
            RadioButton advanced = radios.Single(r => (string?)r.Content == "Advanced");
            Assert.False(beginner.IsChecked);
            Assert.True(advanced.IsChecked);

            // General tab: app name + recent-files limit.
            SelectTab(window, 1);
            TextBox appNameBox = window.GetVisualDescendants().OfType<TextBox>()
                .Single(t => t.Text == "seeded-app");
            Assert.Equal(vm.DefaultAppName, appNameBox.Text);
            NumericUpDown recent = window.GetVisualDescendants().OfType<NumericUpDown>().Single();
            Assert.Equal(42m, recent.Value);

            // Inspector & Compare tab: the MKV element limit.
            SelectTab(window, 2);
            NumericUpDown mkv = window.GetVisualDescendants().OfType<NumericUpDown>().Single();
            Assert.Equal(5000m, mkv.Value);

            Assert.Empty(sink.Messages);
        }
        finally
        {
            AppDataConfig.FolderName = originalFolder;
            CleanUpTempAppDataFolder(tempFolder);
        }
    }

    [AvaloniaFact]
    public void WorkFilesCheckBox_TwoWayBound_DefaultsUnchecked_AndRoundTrips()
    {
        // x:CompileBindings is False in this window, so the CleanupReconstructionWorkFiles binding is
        // only checked at runtime — this pins it two-way, plus the off-by-default (keep) semantics.
        string originalFolder = AppDataConfig.FolderName;
        string tempFolder = UseTempAppDataFolder();
        try
        {
            SettingsViewModel vm = CreateViewModel();

            using var sink = new BindingErrorSink();
            var window = new SettingsWindow(vm);
            window.Show();
            Dispatcher.UIThread.RunJobs();
            SelectTab(window, 3); // work files live on the RAR Reconstruction tab

            CheckBox clear = window.GetVisualDescendants().OfType<CheckBox>()
                .Single(c => (string?)c.Content == "Clear work files when finished");
            Assert.False(clear.IsChecked);          // off by default: work files are kept
            Assert.False(vm.CleanupReconstructionWorkFiles);

            clear.IsChecked = true;                  // view -> VM
            Dispatcher.UIThread.RunJobs();
            Assert.True(vm.CleanupReconstructionWorkFiles);

            vm.CleanupReconstructionWorkFiles = false; // VM -> view
            Dispatcher.UIThread.RunJobs();
            Assert.False(clear.IsChecked);

            Assert.Empty(sink.Messages);
        }
        finally
        {
            AppDataConfig.FolderName = originalFolder;
            CleanUpTempAppDataFolder(tempFolder);
        }
    }

    [AvaloniaFact]
    public void EditingDefaultAppNameTextBox_UpdatesViewModel()
    {
        string originalFolder = AppDataConfig.FolderName;
        string tempFolder = UseTempAppDataFolder();
        try
        {
            SettingsViewModel vm = CreateViewModel();
            var window = new SettingsWindow(vm);
            window.Show();
            Dispatcher.UIThread.RunJobs();
            SelectTab(window, 1); // the app-name TextBox lives on the General tab

            // Declared TextBoxes only: NumericUpDown's template contains its own editor TextBox,
            // so an unfiltered First() couples to the numeric being declared last on the tab.
            TextBox appNameBox = window.GetVisualDescendants().OfType<TextBox>()
                .First(t => t.TemplatedParent is null);
            appNameBox.Text = "Renamed Tool";
            Dispatcher.UIThread.RunJobs();

            Assert.Equal("Renamed Tool", vm.DefaultAppName);
        }
        finally
        {
            AppDataConfig.FolderName = originalFolder;
            CleanUpTempAppDataFolder(tempFolder);
        }
    }

    [AvaloniaFact]
    public void TogglingViewModelMode_UpdatesRadioButtons_BothDirections()
    {
        string originalFolder = AppDataConfig.FolderName;
        string tempFolder = UseTempAppDataFolder();
        try
        {
            SettingsViewModel vm = CreateViewModel(new AppSettings { Mode = UserMode.Beginner });
            var window = new SettingsWindow(vm);
            window.Show();
            Dispatcher.UIThread.RunJobs();

            RadioButton[] radios = [.. window.GetVisualDescendants().OfType<RadioButton>()];
            RadioButton beginner = radios.Single(r => (string?)r.Content == "Beginner");
            RadioButton advanced = radios.Single(r => (string?)r.Content == "Advanced");

            // VM -> View: precondition seeded Beginner.
            Assert.True(beginner.IsChecked);
            Assert.False(advanced.IsChecked);

            // VM -> View: flipping the VM's mode after construction updates the radios.
            vm.IsAdvancedMode = true;
            Dispatcher.UIThread.RunJobs();
            Assert.False(beginner.IsChecked);
            Assert.True(advanced.IsChecked);

            // View -> VM: checking the Beginner radio (as a user click would) flows back to the VM.
            beginner.IsChecked = true;
            Dispatcher.UIThread.RunJobs();
            Assert.True(vm.IsBeginnerMode);
            Assert.False(vm.IsAdvancedMode);
        }
        finally
        {
            AppDataConfig.FolderName = originalFolder;
            CleanUpTempAppDataFolder(tempFolder);
        }
    }

    [AvaloniaFact]
    public void HasSaveAndCancelButtons_WithDefaultAndCancelFlags()
    {
        string originalFolder = AppDataConfig.FolderName;
        string tempFolder = UseTempAppDataFolder();
        try
        {
            SettingsViewModel vm = CreateViewModel();
            var window = new SettingsWindow(vm);
            window.Show();
            Dispatcher.UIThread.RunJobs();

            Button[] buttons = [.. window.GetVisualDescendants().OfType<Button>()];
            Button save = buttons.Single(b => b.Content is "Save");
            Button cancel = buttons.Single(b => b.Content is "Cancel");

            Assert.True(save.IsDefault);
            Assert.True(cancel.IsCancel);
        }
        finally
        {
            AppDataConfig.FolderName = originalFolder;
            CleanUpTempAppDataFolder(tempFolder);
        }
    }

    [AvaloniaFact]
    public void Tabs_FourSectionHeaders_InOrder_DefaultingToInterface()
    {
        string originalFolder = AppDataConfig.FolderName;
        string tempFolder = UseTempAppDataFolder();
        try
        {
            using var sink = new BindingErrorSink();
            var window = new SettingsWindow(CreateViewModel());
            window.Show();
            Dispatcher.UIThread.RunJobs();

            // The former section names, verbatim, as plain-string tab headers (2.4.6 + UIA name
            // derivation); default tab hosts the mode radios — the premise the radio tests rely on.
            TabControl tabs = window.GetVisualDescendants().OfType<TabControl>().Single();
            string?[] headers = [.. tabs.Items.OfType<TabItem>().Select(t => t.Header as string)];
            string?[] expectedHeaders = ["Interface", "General", "Inspector & Compare", "RAR Reconstruction"];
            Assert.Equal(expectedHeaders, headers);
            Assert.Equal(0, tabs.SelectedIndex);

            // Every tab's declared scroller keeps Auto visibility WITH AllowAutoHide=false — the
            // Fluent overlay bar otherwise draws over the right-edge TextBox/Browse controls
            // (Linux especially); this pair reserves the gutter only while the bar shows.
            for (int i = 0; i < 4; i++)
            {
                SelectTab(window, i);
                ScrollViewer scroll = window.GetVisualDescendants().OfType<ScrollViewer>()
                    .Single(sv => sv.TemplatedParent is null);
                Assert.Equal(Avalonia.Controls.Primitives.ScrollBarVisibility.Auto, scroll.VerticalScrollBarVisibility);
                Assert.False(ScrollViewer.GetAllowAutoHide(scroll));
            }

            Assert.Empty(sink.Messages);
            window.Close();
        }
        finally
        {
            AppDataConfig.FolderName = originalFolder;
            CleanUpTempAppDataFolder(tempFolder);
        }
    }

    [AvaloniaFact]
    public void RarTab_ShowsWinRarPackDownloadLinks_MatchingOtherSurfaces()
    {
        // Settings is the THIRD surface offering the pack downloads; all three assert against
        // ResourceLinkExpectations so none can silently diverge (WCAG 3.2.4).
        string originalFolder = AppDataConfig.FolderName;
        string tempFolder = UseTempAppDataFolder();
        try
        {
            using var sink = new BindingErrorSink();
            var window = new SettingsWindow(CreateViewModel());
            window.Show();
            Dispatcher.UIThread.RunJobs();
            SelectTab(window, 3);

            (string?, string?)[] links =
            [
                .. window.GetVisualDescendants().OfType<Button>()
                    .Where(b => b.Classes.Contains("link"))
                    .Select(b => (b.Content as string, b.Tag as string)),
            ];
            Assert.Equal(
                ResourceLinkExpectations.WinRarPackLinks.Select(p => ((string?)p.Label, (string?)p.Url)),
                links);

            Assert.Empty(sink.Messages);
            window.Close();
        }
        finally
        {
            AppDataConfig.FolderName = originalFolder;
            CleanUpTempAppDataFolder(tempFolder);
        }
    }
}
