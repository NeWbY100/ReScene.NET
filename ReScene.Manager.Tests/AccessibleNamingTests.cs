using Avalonia.Automation.Peers;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using ReScene.App.Core.ViewModels;
using ReScene.App.Core.ViewModels.Wizards;
using ReScene.Manager.Views;
using ReScene.Manager.Views.Wizards;

namespace ReScene.Manager.Tests;

/// <summary>
/// The accessible name every control renamed or newly named by the a11y naming pass actually
/// announces, read through the REAL automation peer — the same channel a screen reader calls and
/// the same one <see cref="CompactViewRig.Describe"/> reads.
/// <para>
/// Two rules hold throughout, both learned from this workstream's own review rounds:
/// </para>
/// <para>
/// 1. EXPECTED NAMES ARE LITERAL STRINGS WRITTEN HERE. Reading the expected value off the control
/// under test — or off a shared constant the view also uses — is tautological: it passes through
/// any rename, including one that strips a name back to a bare "Browse". Where two surfaces must
/// agree (WCAG 3.2.4), BOTH are compared against the same literal rather than against each other.
/// </para>
/// <para>
/// 2. CONTROLS ARE RESOLVED BY SOMETHING OTHER THAN THEIR NAME — a bound command reference, an
/// x:Name, a bound value made distinctive for the purpose, or structural position. Selecting a
/// control by the very name under test proves only that a control with that name exists somewhere.
/// </para>
/// </summary>
public class AccessibleNamingTests
{
    private static string PeerName(Control control) =>
        ControlAutomationPeer.CreatePeerForElement(control).GetName() ?? string.Empty;

    private static Window Host(Control view)
    {
        var window = new Window { Width = 1000, Height = 800, Content = view };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return window;
    }

    private static Button ByCommand(Window window, System.Windows.Input.ICommand command) =>
        window.GetVisualDescendants().OfType<Button>().Single(b => ReferenceEquals(b.Command, command));

    private static TextBox ByName(Window window, string testId) =>
        window.GetVisualDescendants().OfType<TextBox>().Single(t => t.Name == testId);

    /// <summary>Resolves a TextBox by the DISTINCTIVE VALUE bound into it, never by its own name.</summary>
    private static TextBox ByBoundText(Window window, string boundValue) =>
        window.GetVisualDescendants().OfType<TextBox>().Single(t => t.Text == boundValue);

    // ── Reconstructor: the four path pickers ──────────────────────────────────

    /// <summary>
    /// The headline gate finding: four pickers whose TextBoxes announced nothing at all and whose
    /// four Browse buttons all announced the same bare "Browse". Each of the eight is asserted
    /// against a literal, and the four buttons' literals are the strings ReconstructWizardBody was
    /// ALREADY shipping for these same four commands — see
    /// <see cref="ReconstructorAndWizard_IdentifyTheSameFourCommandsIdentically"/>, which is what
    /// makes that a WCAG 3.2.4 claim rather than a coincidence.
    /// </summary>
    [AvaloniaFact]
    public void Reconstructor_PathPickers_AnnounceTheirSubject()
    {
        BeginnerShellViewModel shell = BeginnerShellTestFactory.Create();
        ReconstructorViewModel vm = shell.Reconstructor;
        Window window = Host(new ReconstructorView { DataContext = vm });
        try
        {
            Assert.Equal("Browse for WinRAR versions folder", PeerName(ByCommand(window, vm.BrowseWinRARCommand)));
            Assert.Equal("Browse for extracted release files", PeerName(ByCommand(window, vm.BrowseReleaseCommand)));
            Assert.Equal("Browse for verification file", PeerName(ByCommand(window, vm.BrowseVerificationCommand)));
            Assert.Equal("Browse for output folder", PeerName(ByCommand(window, vm.BrowseOutputCommand)));

            Assert.Equal("WinRAR versions folder path", PeerName(ByName(window, "WinRARTextBox")));
            Assert.Equal("Release files path", PeerName(ByName(window, "ReleaseTextBox")));
            Assert.Equal("Verify file path", PeerName(ByName(window, "VerifyTextBox")));
            Assert.Equal("Output folder path", PeerName(ByName(window, "OutputTextBox")));
        }
        finally { window.Close(); }
    }

    /// <summary>
    /// WCAG 3.2.4 Consistent Identification, asserted rather than assumed: the Advanced tab and the
    /// Beginner wizard drive the SAME four commands on the SAME ViewModel instance, so they must
    /// identify them identically. Both surfaces are compared against the same four literals — not
    /// against each other, which would pass if both drifted together.
    /// </summary>
    [AvaloniaFact]
    public void ReconstructorAndWizard_IdentifyTheSameFourCommandsIdentically()
    {
        const string WinRar = "Browse for WinRAR versions folder";
        const string Release = "Browse for extracted release files";
        const string Verification = "Browse for verification file";
        const string Output = "Browse for output folder";

        BeginnerShellViewModel shell = BeginnerShellTestFactory.Create();
        ReconstructorViewModel vm = shell.Reconstructor;

        Window advanced = Host(new ReconstructorView { DataContext = vm });
        try
        {
            Assert.Equal(WinRar, PeerName(ByCommand(advanced, vm.BrowseWinRARCommand)));
            Assert.Equal(Release, PeerName(ByCommand(advanced, vm.BrowseReleaseCommand)));
            Assert.Equal(Verification, PeerName(ByCommand(advanced, vm.BrowseVerificationCommand)));
            Assert.Equal(Output, PeerName(ByCommand(advanced, vm.BrowseOutputCommand)));
        }
        finally { advanced.Close(); }

        // The wizard body reads the hosting Window's WizardViewModel.CurrentStepIndex; the pickers
        // live on step 1, and unselected steps are IsVisible=false rather than unrealized, so the
        // peers exist either way — the step is selected anyway so this reads the shipping state.
        var wizard = new WizardViewModel("Reconstruct", vm,
            [.. Enumerable.Range(0, 2).Select(i => new WizardStep { Title = $"step {i}" })]);
        var body = new ReconstructWizardBody { DataContext = vm };
        var wizardWindow = new Window { Width = 1000, Height = 800, DataContext = wizard, Content = body };
        wizardWindow.Show();
        wizard.CurrentStepIndex = 1;
        Dispatcher.UIThread.RunJobs();
        try
        {
            Assert.Equal(WinRar, PeerName(ByCommand(wizardWindow, vm.BrowseWinRARCommand)));
            Assert.Equal(Release, PeerName(ByCommand(wizardWindow, vm.BrowseReleaseCommand)));
            Assert.Equal(Verification, PeerName(ByCommand(wizardWindow, vm.BrowseVerificationCommand)));
            Assert.Equal(Output, PeerName(ByCommand(wizardWindow, vm.BrowseOutputCommand)));
        }
        finally { wizardWindow.Close(); }
    }

    // ── Reconstructor: Options tab ────────────────────────────────────────────

    /// <summary>
    /// The -mt range pair, the volume size and its unit ComboBox. The two -mt boxes are structurally
    /// identical, so each is resolved by the DISTINCTIVE VALUE bound into it rather than by
    /// document order — order would silently pass if the two bindings were ever swapped.
    /// <para>
    /// "Thread count from"/"…to" and not the "range start"/"range end" the gate report suggested:
    /// each box's visible label is the "From:"/"To:" rendered beside it, and WCAG 2.5.3 requires
    /// the accessible name to CONTAIN the visible label. That constraint is asserted here directly,
    /// so a future rename to a cleaner-reading phrase that drops the visible word fails.
    /// </para>
    /// </summary>
    [AvaloniaFact]
    public void Reconstructor_OptionsFields_AnnounceThemselves_AndContainTheirVisibleLabels()
    {
        BeginnerShellViewModel shell = BeginnerShellTestFactory.Create();
        ReconstructorViewModel vm = shell.Reconstructor;
        vm.SwitchMTStart = 3;
        vm.SwitchMTEnd = 9;
        vm.VolumeSize = "123";

        Window window = Host(new ReconstructorView { DataContext = vm });
        try
        {
            SelectOptionsTab(window);

            Assert.Equal("Thread count from", PeerName(ByBoundText(window, "3")));
            Assert.Equal("Thread count to", PeerName(ByBoundText(window, "9")));
            Assert.Equal("Volume size", PeerName(ByBoundText(window, "123")));

            ComboBox unit = window.GetVisualDescendants().OfType<ComboBox>()
                .Single(c => ReferenceEquals(c.ItemsSource, ReconstructorViewModel.VolumeSizeUnits));
            Assert.Equal("Volume size unit", PeerName(unit));

            // WCAG 2.5.3 Label in Name, stated as the rule rather than left implicit in the strings
            // above: whatever these are called, the name must contain what the user can read.
            Assert.Contains("From", PeerName(ByBoundText(window, "3")), StringComparison.OrdinalIgnoreCase);
            Assert.Contains("To", PeerName(ByBoundText(window, "9")), StringComparison.OrdinalIgnoreCase);
        }
        finally { window.Close(); }
    }

    /// <summary>
    /// The tri-state legend's three disabled sample checkboxes, which the gate recorded as
    /// announcing "unnamed disabled". Each is resolved STRUCTURALLY — the CheckBox sharing a
    /// StackPanel with a specific, x:Named caption — and asserted to announce that caption's exact
    /// text, written here as a literal.
    /// <para>
    /// Naming them via LabeledBy rather than removing them from the control view is a deliberate,
    /// measured choice: <c>AutomationProperties.AccessibilityView="Raw"</c> does NOT prune a peer
    /// from its parent's children walk on this Avalonia — see
    /// <see cref="AccessibilityViewRaw_DoesNotPruneThePeer_WhichIsWhyTheLegendIsNamedInstead"/>,
    /// which pins that platform fact so this decision cannot be quietly "corrected" later.
    /// </para>
    /// </summary>
    [AvaloniaFact]
    public void Reconstructor_TriStateLegend_CheckBoxesAnnounceTheirCaption()
    {
        BeginnerShellViewModel shell = BeginnerShellTestFactory.Create();
        Window window = Host(new ReconstructorView { DataContext = shell.Reconstructor });
        try
        {
            SelectOptionsTab(window);

            Assert.Equal("Option is never set", PeerName(LegendCheckBoxBeside(window, "LegendNeverCaption")));
            Assert.Equal("Test with and without this option set", PeerName(LegendCheckBoxBeside(window, "LegendBothCaption")));
            Assert.Equal("Option is always set", PeerName(LegendCheckBoxBeside(window, "LegendAlwaysCaption")));
        }
        finally { window.Close(); }
    }

    /// <summary>
    /// The platform fact the legend's treatment rests on, pinned so it is not taken on trust: a
    /// child marked <c>AccessibilityView="Raw"</c> is still returned by its parent peer's
    /// <c>GetChildren()</c>. Avalonia accepts the property and reads it back, which makes it look
    /// like it works; it simply has no effect on the peer tree here. StylesTests'
    /// <c>HelpDisclosure_ExposesCoherentAutomationPeers_InBothModes</c> records the same finding
    /// from the other direction (why a custom peer, not Raw, was needed there).
    /// <para>
    /// If a future Avalonia starts honouring it, this test fails — which is the right outcome: that
    /// is the moment to reconsider whether the decorative legend should be pruned instead of named.
    /// </para>
    /// </summary>
    [AvaloniaFact]
    public void AccessibilityViewRaw_DoesNotPruneThePeer_WhichIsWhyTheLegendIsNamedInstead()
    {
        var plain = new CheckBox { Content = "plain" };
        var raw = new CheckBox { Content = "raw" };
        Avalonia.Automation.AutomationProperties.SetAccessibilityView(
            raw, Avalonia.Automation.AccessibilityView.Raw);

        var panel = new StackPanel();
        panel.Children.Add(plain);
        panel.Children.Add(raw);
        Window window = Host(panel);
        try
        {
            Assert.Equal(
                Avalonia.Automation.AccessibilityView.Raw,
                Avalonia.Automation.AutomationProperties.GetAccessibilityView(raw));

            IReadOnlyList<AutomationPeer> children = ControlAutomationPeer.CreatePeerForElement(panel).GetChildren();
            Assert.Equal(2, children.Count);
            Assert.Contains(children, c => c.GetName() == "raw");
        }
        finally { window.Close(); }
    }

    private static void SelectOptionsTab(Window window)
    {
        TabControl settingsTabs = window.GetVisualDescendants().OfType<TabControl>().Single(t => t.ItemCount == 6);
        settingsTabs.SelectedIndex = 4;
        Dispatcher.UIThread.RunJobs();
    }

    private static CheckBox LegendCheckBoxBeside(Window window, string captionTestId)
    {
        TextBlock caption = window.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Name == captionTestId);
        var row = (StackPanel)caption.GetVisualParent()!;
        return row.GetVisualDescendants().OfType<CheckBox>().Single();
    }

    // ── Reconstructor: the Paths sub-tab header ───────────────────────────────

    /// <summary>
    /// The TabItem announced its BODY's type name, "Avalonia.Controls.ScrollViewer" — a composite
    /// header leaves the peer nothing else to fall back on. Both states of the replacement are
    /// asserted, because the second one carries information that previously existed ONLY as a
    /// visual glyph: with any required path missing the header shows a warning triangle, and a
    /// TabItem peer does not expose its header's TextBlocks as children, so nothing about that
    /// state reached an assistive technology at all.
    /// <para>
    /// The transition is driven by filling the paths on the live VM, so the change notification
    /// chain (four path properties → PathsTabAccessibleName → the bound peer name) is exercised
    /// end to end rather than the two strings being read from two freshly-built windows.
    /// </para>
    /// </summary>
    [AvaloniaFact]
    public void Reconstructor_PathsTab_AnnouncesItsNameAndItsNeedsAttentionState()
    {
        BeginnerShellViewModel shell = BeginnerShellTestFactory.Create();
        ReconstructorViewModel vm = shell.Reconstructor;
        Window window = Host(new ReconstructorView { DataContext = vm });
        try
        {
            TabControl settingsTabs = window.GetVisualDescendants().OfType<TabControl>().Single(t => t.ItemCount == 6);
            var pathsTab = (TabItem)settingsTabs.Items[0]!;

            Assert.True(vm.PathsNeedAttention, "precondition: a freshly-built VM has no paths set");
            Assert.Equal("Paths — needs attention", PeerName(pathsTab));

            // Separate real folders, and the .sfv inside the release folder: PathsNeedAttention
            // also fails Release/Output and Verify/Output OVERLAP, so pointing them all at one
            // temp directory would never clear the state under test.
            string root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), $"pathstab-{Guid.NewGuid():N}")).FullName;
            try
            {
                string release = Directory.CreateDirectory(Path.Combine(root, "release")).FullName;
                string sfv = Path.Combine(release, "release.sfv");
                File.WriteAllText(sfv, "; sfv");

                vm.WinRARPath = Directory.CreateDirectory(Path.Combine(root, "winrar")).FullName;
                vm.ReleasePath = release;
                vm.VerificationPath = sfv;
                vm.OutputPath = Directory.CreateDirectory(Path.Combine(root, "output")).FullName;
                Dispatcher.UIThread.RunJobs();

                Assert.False(vm.PathsNeedAttention, "precondition: all four paths now resolve");
                Assert.Equal("Paths", PeerName(pathsTab));
            }
            finally { Directory.Delete(root, recursive: true); }
        }
        finally { window.Close(); }
    }

    // ── Creator ───────────────────────────────────────────────────────────────

    /// <summary>
    /// The Input row's file Browse joins the "Browse for &lt;target&gt;" convention; the folder
    /// Browse deliberately does NOT, and that exception is asserted here rather than only commented
    /// at the site. Its visible Content is "Browse folder…", and WCAG 2.5.3 requires the accessible
    /// name to contain the visible label — "Browse for release folder" would not. Both halves are
    /// checked: the literal name, and the Label-in-Name property that forces it.
    /// </summary>
    [AvaloniaFact]
    public void Creator_InputRowBrowseButtons_UnifyExceptWhereLabelInNameForbidsIt()
    {
        BeginnerShellViewModel shell = BeginnerShellTestFactory.Create();
        CreatorViewModel vm = shell.CreateSRRWizard;
        Window window = Host(new CreatorView { DataContext = vm });
        try
        {
            Button file = ByCommand(window, vm.BrowseInputCommand);
            Button folder = ByCommand(window, vm.BrowseInputFolderCommand);

            Assert.Equal("Browse for input file", PeerName(file));
            Assert.Equal("Browse folder for release input", PeerName(folder));

            Assert.Equal("Browse", file.Content as string);
            Assert.Equal("Browse folder…", folder.Content as string);
            Assert.Contains("Browse", PeerName(file), StringComparison.Ordinal);
            Assert.Contains("Browse folder", PeerName(folder), StringComparison.Ordinal);
        }
        finally { window.Close(); }
    }

    /// <summary>
    /// Both App-name boxes are LabeledBy their "App name:" caption rather than carrying an explicit
    /// name, so the announced value is the caption's text VERBATIM — colon included. That is
    /// asserted as measured rather than tidied to "App name": the caption is the single source of
    /// the label, and a test that expected the tidier string would be asserting something the app
    /// does not do.
    /// </summary>
    [AvaloniaFact]
    public void AppNameBoxes_AnnounceTheirCaption_InBothCreators()
    {
        BeginnerShellViewModel shell = BeginnerShellTestFactory.Create();

        CreatorViewModel creator = shell.CreateSRRWizard;
        creator.AppName = "srr-app-name-probe";
        Window creatorWindow = Host(new CreatorView { DataContext = creator });
        try
        {
            Assert.Equal("App name:", PeerName(ByBoundText(creatorWindow, "srr-app-name-probe")));
        }
        finally { creatorWindow.Close(); }

        SRSCreatorViewModel srs = shell.SRSCreator;
        srs.AppName = "srs-app-name-probe";
        Window srsWindow = Host(new SRSCreatorView { DataContext = srs });
        try
        {
            Assert.Equal("App name:", PeerName(ByBoundText(srsWindow, "srs-app-name-probe")));
        }
        finally { srsWindow.Close(); }
    }

    // ── The remaining picker TextBoxes across the sibling views ───────────────

    /// <summary>
    /// Everything left on the gate's (b) list: the picker TextBoxes that announced nothing. One
    /// literal per control. The convention is "&lt;subject&gt; path", with the subject taken from
    /// that row's OWN visible caption so the name contains the visible label — which is why
    /// SampleRestorer says "directory" (its captions read "Media Directory"/"Output Directory")
    /// while SRSReconstructor says "file" (its caption reads "Media File").
    /// </summary>
    [AvaloniaFact]
    public void SiblingViews_PickerTextBoxes_AnnounceTheirSubject()
    {
        BeginnerShellViewModel shell = BeginnerShellTestFactory.Create();

        Window restorer = Host(new SampleRestorerView { DataContext = shell.Restore.BulkRestorer });
        try
        {
            Assert.Equal("SRR file path", PeerName(ByName(restorer, "SRRFileTextBox")));
            Assert.Equal("Media directory path", PeerName(ByName(restorer, "MediaDirTextBox")));
            Assert.Equal("Output directory path", PeerName(ByName(restorer, "OutputDirTextBox")));
        }
        finally { restorer.Close(); }

        Window rebuilder = Host(new SRSReconstructorView { DataContext = shell.Restore.SingleRebuilder });
        try
        {
            Assert.Equal("SRS file path", PeerName(ByName(rebuilder, "SRSFileTextBox")));
            Assert.Equal("Media file path", PeerName(ByName(rebuilder, "MediaFileTextBox")));
            Assert.Equal("Output path", PeerName(ByName(rebuilder, "OutputTextBox")));
        }
        finally { rebuilder.Close(); }

        SRSCreatorViewModel srs = shell.SRSCreator;
        srs.MainFilePath = @"C:\release\main-file-probe.mkv";
        Window srsCreator = Host(new SRSCreatorView { DataContext = srs });
        try
        {
            Assert.Equal("Sample file path", PeerName(ByName(srsCreator, "InputTextBox")));
            Assert.Equal("Output path", PeerName(ByName(srsCreator, "OutputTextBox")));
            Assert.Equal("Main file path", PeerName(ByBoundText(srsCreator, @"C:\release\main-file-probe.mkv")));
        }
        finally { srsCreator.Close(); }
    }

    /// <summary>
    /// The nine Browse buttons that still announced the bare word "Browse" after the first naming
    /// pass — three each in SampleRestorer, SRSCreator and SRSReconstructor. One literal per
    /// button, each resolved by its bound command.
    /// <para>
    /// The target in each name comes from that row's OWN visible caption subject, which is why
    /// SampleRestorer says "directory" (its captions read "Media Directory"/"Output Directory")
    /// where SRSReconstructor says "file" (its caption reads "Media File"). The shared phrasing is
    /// safe for all nine under WCAG 2.5.3 precisely because each renders the bare word "Browse" —
    /// asserted below alongside the names, since that is the condition that makes the convention
    /// applicable rather than a coincidence.
    /// </para>
    /// </summary>
    [AvaloniaFact]
    public void SiblingViews_BrowseButtons_UseTheSharedConvention()
    {
        BeginnerShellViewModel shell = BeginnerShellTestFactory.Create();

        SampleRestorerViewModel restorerVm = shell.Restore.BulkRestorer!;
        Window restorer = Host(new SampleRestorerView { DataContext = restorerVm });
        try
        {
            AssertBrowseButton(restorer, restorerVm.BrowseSRRCommand, "Browse for SRR file");
            AssertBrowseButton(restorer, restorerVm.BrowseMediaDirectoryCommand, "Browse for media directory");
            AssertBrowseButton(restorer, restorerVm.BrowseOutputDirectoryCommand, "Browse for output directory");
        }
        finally { restorer.Close(); }

        SRSCreatorViewModel srsCreatorVm = shell.SRSCreator;
        Window srsCreator = Host(new SRSCreatorView { DataContext = srsCreatorVm });
        try
        {
            AssertBrowseButton(srsCreator, srsCreatorVm.BrowseInputCommand, "Browse for sample file");
            AssertBrowseButton(srsCreator, srsCreatorVm.BrowseMainFileCommand, "Browse for main file");
            AssertBrowseButton(srsCreator, srsCreatorVm.BrowseOutputCommand, "Browse for output path");
        }
        finally { srsCreator.Close(); }

        SRSReconstructorViewModel rebuilderVm = shell.Restore.SingleRebuilder!;
        Window rebuilder = Host(new SRSReconstructorView { DataContext = rebuilderVm });
        try
        {
            AssertBrowseButton(rebuilder, rebuilderVm.BrowseSRSCommand, "Browse for SRS file");
            AssertBrowseButton(rebuilder, rebuilderVm.BrowseMediaCommand, "Browse for media file");
            AssertBrowseButton(rebuilder, rebuilderVm.BrowseOutputCommand, "Browse for output path");
        }
        finally { rebuilder.Close(); }
    }

    /// <summary>
    /// Asserts one Browse button's literal name AND the Label-in-Name condition that licenses the
    /// shared phrasing: its visible Content must be the bare word, and the accessible name must
    /// contain it (WCAG 2.5.3). If someone ever changes a Content to "Browse folder…" the way
    /// CreatorView's does, this fails rather than letting the convention quietly break a level-A
    /// criterion.
    /// </summary>
    private static void AssertBrowseButton(Window window, System.Windows.Input.ICommand command, string expectedName)
    {
        Button button = ByCommand(window, command);
        Assert.Equal("Browse", button.Content as string);
        Assert.Equal(expectedName, PeerName(button));
        Assert.StartsWith("Browse", PeerName(button), StringComparison.Ordinal);
    }

    /// <summary>
    /// WCAG 3.2.4 for the output-FILE picker: three of the surfaces that use it — CreatorView,
    /// SRSCreatorView and SRSReconstructorView — are hosted here and each compared against ONE
    /// literal rather than against each other, so the test cannot pass by them drifting together.
    /// <para>
    /// SCOPE, corrected: an earlier version of this comment said "the four surfaces … all four are
    /// compared", naming the Create-SRR wizard among them, while the body has only ever hosted
    /// three. **Seven** surfaces now share "Browse for output path" — the three here plus the
    /// Create-SRR, Create-SRS, Edit-SRR and Restore wizard bodies. The other four are asserted in
    /// <see cref="CreateSRRWizard_Step3OutputRow_AnnouncesBothControls"/> and
    /// <see cref="BeginnerWizardBodies_BrowseButtons_UseTheSharedConvention"/>, and every one of the
    /// seven is swept by <c>BrowseButtonCensusTests</c>. Saying "all four" while checking three was
    /// the same overclaim-by-uncounted-denominator this workstream kept making; the count is now
    /// stated as what this test covers, not as what exists.
    /// </para>
    /// <para>
    /// The Reconstructor's own output picker deliberately reads "Browse for output folder" and is
    /// asserted to DIFFER — it chooses a directory, which is a different thing to choose, and
    /// collapsing the two would be false consistency rather than 3.2.4 compliance.
    /// </para>
    /// </summary>
    [AvaloniaFact]
    public void OutputPathPickers_ShareOneName_AndTheFolderPickerDeliberatelyDiffers()
    {
        const string PathName = "Browse for output path";
        const string FolderName = "Browse for output folder";

        BeginnerShellViewModel shell = BeginnerShellTestFactory.Create();

        Window creator = Host(new CreatorView { DataContext = shell.CreateSRRWizard });
        try
        { Assert.Equal(PathName, PeerName(ByCommand(creator, shell.CreateSRRWizard.BrowseOutputCommand))); }
        finally { creator.Close(); }

        Window srsCreator = Host(new SRSCreatorView { DataContext = shell.SRSCreator });
        try
        { Assert.Equal(PathName, PeerName(ByCommand(srsCreator, shell.SRSCreator.BrowseOutputCommand))); }
        finally { srsCreator.Close(); }

        Window rebuilder = Host(new SRSReconstructorView { DataContext = shell.Restore.SingleRebuilder });
        try
        { Assert.Equal(PathName, PeerName(ByCommand(rebuilder, shell.Restore.SingleRebuilder!.BrowseOutputCommand))); }
        finally { rebuilder.Close(); }

        Window reconstructor = Host(new ReconstructorView { DataContext = shell.Reconstructor });
        try
        {
            Assert.Equal(FolderName, PeerName(ByCommand(reconstructor, shell.Reconstructor.BrowseOutputCommand)));
            Assert.NotEqual(PathName, FolderName);
        }
        finally { reconstructor.Close(); }
    }

    /// <summary>
    /// The eight Browse buttons in the three Beginner wizard bodies that a review found still
    /// announcing the bare word — the remainder of the app-wide total that the first two naming
    /// passes had not counted (they measured how many buttons were NAMED and never how many
    /// existed).
    /// <para>
    /// Seven of the eight take a name that already existed elsewhere, VERBATIM, because they are
    /// literally the same commands: <c>RestoreWizardBody</c> binds through
    /// <c>BulkRestorer</c>/<c>SingleRebuilder</c>, which ARE the SampleRestorer and SRSReconstructor
    /// ViewModels, and <c>CreateSRSWizardBody</c> drives the SRSCreator's own commands. Only
    /// the Restore wizard's own entry picker needed a new string, and its target comes from its own
    /// caption ("SRR or SRS file").
    /// </para>
    /// <para>
    /// Worth stating because it is the one place the two conventions genuinely pull apart:
    /// CreateSRSWizardBody's caption for the main-file row reads "Full movie (optional)" where the
    /// Advanced tab says "Main file", and the name follows the COMMAND ("Browse for main file")
    /// rather than this surface's caption. That is safe under WCAG 2.5.3 — the button's own visible
    /// label is the bare "Browse" on both surfaces, so the caption never constrained the name — and
    /// it keeps one function to one announced name. Where a caption DOES constrain the name
    /// (CreatorView's "Browse folder…"), the criterion wins instead; the two rules are recorded
    /// together at that site.
    /// </para>
    /// <para>
    /// All step panels are realized regardless of the selected step (they are IsVisible-gated, not
    /// unloaded), so one host per body reaches every button.
    /// </para>
    /// </summary>
    [AvaloniaFact]
    public void BeginnerWizardBodies_BrowseButtons_UseTheSharedConvention()
    {
        BeginnerShellViewModel shell = BeginnerShellTestFactory.Create();

        SRSCreatorViewModel srs = shell.SRSCreator;
        Window srsWizard = HostWizardBody(new CreateSRSWizardBody { DataContext = srs }, srs, steps: 3);
        try
        {
            AssertBrowseButton(srsWizard, srs.BrowseInputCommand, "Browse for sample file");
            AssertBrowseButton(srsWizard, srs.BrowseMainFileCommand, "Browse for main file");
            AssertBrowseButton(srsWizard, srs.BrowseOutputCommand, "Browse for output path");
        }
        finally { srsWizard.Close(); }

        SRREditorViewModel editor = shell.SRREditor;
        Window editWizard = HostWizardBody(new EditSRRWizardBody { DataContext = editor }, editor, steps: 4);
        try
        {
            AssertBrowseButton(editWizard, editor.BrowseSourceCommand, "Browse for SRR file");
            AssertBrowseButton(editWizard, editor.BrowseOutputCommand, "Browse for output path");
        }
        finally { editWizard.Close(); }

        BeginnerRestoreViewModel restore = shell.Restore;
        Window restoreWizard = HostWizardBody(new RestoreWizardBody { DataContext = restore }, restore, steps: 3);
        try
        {
            AssertBrowseButton(restoreWizard, restore.BrowseInputCommand, "Browse for SRR or SRS file");
            AssertBrowseButton(restoreWizard, restore.BulkRestorer!.BrowseMediaDirectoryCommand, "Browse for media directory");
            AssertBrowseButton(restoreWizard, restore.BulkRestorer!.BrowseOutputDirectoryCommand, "Browse for output directory");
            AssertBrowseButton(restoreWizard, restore.SingleRebuilder!.BrowseMediaCommand, "Browse for media file");
            AssertBrowseButton(restoreWizard, restore.SingleRebuilder!.BrowseOutputCommand, "Browse for output path");
        }
        finally { restoreWizard.Close(); }
    }

    private static Window HostWizardBody(Control body, object taskVm, int steps)
    {
        var wizard = new WizardViewModel("naming probe", taskVm,
            [.. Enumerable.Range(0, steps).Select(i => new WizardStep { Title = $"step {i}" })]);
        var window = new Window { Width = 1000, Height = 800, DataContext = wizard, Content = body };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return window;
    }

    /// <summary>
    /// The ISO picker only exists while an ISO is the source, so it is not in any tab-order fixture
    /// and needs its own coverage. LabeledBy its "File inside ISO:" caption, so — like the App-name
    /// boxes — the announced value is the caption verbatim.
    /// </summary>
    [AvaloniaFact]
    public void SRSCreator_ISOComboBox_AnnouncesItsCaption()
    {
        BeginnerShellViewModel shell = BeginnerShellTestFactory.Create();
        SRSCreatorViewModel vm = shell.SRSCreator;
        vm.IsISOSource = true;
        vm.ISOMediaFiles.Add("VIDEO_TS/VTS_01_1.VOB");

        Window window = Host(new SRSCreatorView { DataContext = vm });
        try
        {
            ComboBox iso = window.GetVisualDescendants().OfType<ComboBox>()
                .Single(c => ReferenceEquals(c.ItemsSource, vm.ISOMediaFiles));
            Assert.True(iso.IsVisible, "precondition: the ISO row is only shown while IsISOSource");
            Assert.Equal("File inside ISO:", PeerName(iso));
        }
        finally { window.Close(); }
    }

    // ── Create-SRR wizard, step 3 ─────────────────────────────────────────────

    /// <summary>
    /// The wizard's "Save SRR to" row: previously the Button fell back to its bare "Browse" and the
    /// TextBox had no name at all — no AutomationProperties.Name, no x:Name, no LabeledBy. The
    /// button now takes CreatorView's own name for the same BrowseOutputCommand (3.2.4, asserted
    /// against the same literal on both surfaces), and the TextBox is LabeledBy the step's heading,
    /// mirroring step 0's WizInputTextBox.
    /// </summary>
    [AvaloniaFact]
    public void CreateSRRWizard_Step3OutputRow_AnnouncesBothControls()
    {
        const string BrowseName = "Browse for output path";

        BeginnerShellViewModel shell = BeginnerShellTestFactory.Create();
        CreatorViewModel vm = shell.CreateSRRWizard;
        vm.OutputPath = @"C:\release\wizard-output-probe.srr";

        Window advanced = Host(new CreatorView { DataContext = vm });
        try
        {
            Assert.Equal(BrowseName, PeerName(ByCommand(advanced, vm.BrowseOutputCommand)));
        }
        finally { advanced.Close(); }

        var wizard = new WizardViewModel("Create SRR", vm,
            [.. Enumerable.Range(0, 4).Select(i => new WizardStep { Title = $"step {i}" })]);
        var body = new CreateSRRWizardBody { DataContext = vm };
        var window = new Window { Width = 1000, Height = 800, DataContext = wizard, Content = body };
        window.Show();
        wizard.CurrentStepIndex = 3;
        Dispatcher.UIThread.RunJobs();
        try
        {
            Assert.Equal(BrowseName, PeerName(ByCommand(window, vm.BrowseOutputCommand)));
            Assert.Equal("Save SRR to", PeerName(ByBoundText(window, @"C:\release\wizard-output-probe.srr")));
        }
        finally { window.Close(); }
    }
}
