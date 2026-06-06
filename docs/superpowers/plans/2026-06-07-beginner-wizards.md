# Beginner Pop-up Wizards Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax.

**Goal:** In Beginner mode, clicking a hub card opens a modal pop-up **wizard** (step-by-step, Back/Next/Close) for that task, instead of an in-place single screen.

**Architecture:** A generic, unit-tested `WizardViewModel` (pure navigation: ordered steps, current index, per-step `CanAdvance`) drives a generic `WizardWindow` (chrome: header + Back/Next/Close footer + a body host). Each task supplies a "body" UserControl whose step panels are switched by `CurrentStepIndex` via the existing `IndexToVisibility` converter; the body's DataContext is the **existing** task ViewModel, so all fields/commands are reused — no business logic duplicated. A `BeginnerWizardFactory` assembles (steps + validity + body) per card; the hub opens the window with `ShowDialog()`.

**Tech Stack:** .NET 10, WPF, CommunityToolkit.Mvvm (partial-property `[ObservableProperty]`), xUnit.

**Conventions:**
- Conventional commits ending with: `Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>`.
- Build: `dotnet build ReScene.NET/ReScene.NET.csproj` · Test: `dotnet test ReScene.NET.Tests/ReScene.NET.Tests.csproj`.
- Reuse the existing `IndexToVisibility`, `BoolToVisibility`, `InverseBoolToVisibility` converters and `DarkTitleBar` helper. Dialog windows are opened from code-behind (same pattern as `MainWindow.OnSettingsClick`).

---

## File Structure

**New**
- `ReScene.NET/ViewModels/Wizards/WizardStep.cs` — a step: `Title` + `CanAdvance` predicate.
- `ReScene.NET/ViewModels/Wizards/WizardViewModel.cs` — navigation VM (pure, testable).
- `ReScene.NET/Views/Wizards/WizardWindow.xaml(.cs)` — generic chrome window hosting a body.
- `ReScene.NET/Views/Wizards/CreateSrrWizardBody.xaml(.cs)` — 3 step panels over `CreatorViewModel`.
- `ReScene.NET/Views/Wizards/CreateSrsWizardBody.xaml(.cs)` — 3 step panels over `SRSCreatorViewModel`.
- `ReScene.NET/Views/Wizards/ReconstructWizardBody.xaml(.cs)` — 5 step panels over `ReconstructorViewModel`.
- `ReScene.NET/Views/Wizards/RestoreWizardBody.xaml(.cs)` — 3 step panels over `BeginnerRestoreViewModel` (+ its sub-VMs).
- `ReScene.NET/ViewModels/Wizards/BeginnerWizardFactory.cs` — builds `(WizardViewModel, body)` per `BeginnerCard`.
- `ReScene.NET.Tests/WizardViewModelTests.cs`.

**Modified**
- `ReScene.NET/Views/BeginnerShellView.xaml(.cs)` — cards open a wizard window; remove the in-place task host.
- `ReScene.NET/ViewModels/BeginnerShellViewModel.cs` — drop the in-place `CurrentCard`/`Show*`/`CurrentTitle` navigation; keep the task-VM references + `OpenInAdvanced`.

**Removed (superseded by wizard bodies — their field markup migrates into the bodies)**
- `ReScene.NET/Views/Beginner/BeginnerCreatorView.xaml(.cs)`, `BeginnerSrsCreatorView.xaml(.cs)`, `BeginnerReconstructorView.xaml(.cs)`, `BeginnerRestoreView.xaml(.cs)`, `BeginnerRestoreBulkView.xaml(.cs)`, `BeginnerRestoreSingleView.xaml(.cs)`.
- Keep `BeginnerRestoreViewModel` (the restore wizard uses its routing).

---

## W1 — Wizard framework

### Task 1: `WizardStep` + `WizardViewModel` (TDD)

**Files:**
- Create: `ReScene.NET/ViewModels/Wizards/WizardStep.cs`
- Create: `ReScene.NET/ViewModels/Wizards/WizardViewModel.cs`
- Test: `ReScene.NET.Tests/WizardViewModelTests.cs`

- [ ] **Step 1: Write the failing test**

`ReScene.NET.Tests/WizardViewModelTests.cs`:

```csharp
using ReScene.NET.ViewModels.Wizards;

namespace ReScene.NET.Tests;

public class WizardViewModelTests
{
    private static WizardViewModel Make(params bool[] canAdvance)
    {
        var steps = new List<WizardStep>();
        for (int i = 0; i < canAdvance.Length; i++)
        {
            bool v = canAdvance[i];
            steps.Add(new WizardStep { Title = $"Step {i}", CanAdvance = () => v });
        }
        return new WizardViewModel("Test", new object(), steps);
    }

    [Fact]
    public void NewWizard_StartsAtFirstStep()
    {
        var w = Make(true, true, true);
        Assert.Equal(0, w.CurrentStepIndex);
        Assert.True(w.IsFirstStep);
        Assert.False(w.IsLastStep);
        Assert.Equal(3, w.StepCount);
        Assert.Equal(1, w.CurrentStepNumber);
        Assert.False(w.BackCommand.CanExecute(null));
    }

    [Fact]
    public void Next_AdvancesWhenStepValid_AndStopsAtLast()
    {
        var w = Make(true, true);
        Assert.True(w.NextCommand.CanExecute(null));
        w.NextCommand.Execute(null);
        Assert.Equal(1, w.CurrentStepIndex);
        Assert.True(w.IsLastStep);
        Assert.False(w.NextCommand.CanExecute(null));
    }

    [Fact]
    public void Next_BlockedWhenStepInvalid()
    {
        var w = Make(false, true);
        Assert.False(w.NextCommand.CanExecute(null));
        w.NextCommand.Execute(null);
        Assert.Equal(0, w.CurrentStepIndex);
    }

    [Fact]
    public void Back_ReturnsToPreviousStep()
    {
        var w = Make(true, true);
        w.NextCommand.Execute(null);
        Assert.True(w.BackCommand.CanExecute(null));
        w.BackCommand.Execute(null);
        Assert.Equal(0, w.CurrentStepIndex);
        Assert.True(w.IsFirstStep);
    }
}
```

- [ ] **Step 2: Run it — expect FAIL** (`WizardViewModel`/`WizardStep` missing).

Run: `dotnet test ReScene.NET.Tests/ReScene.NET.Tests.csproj --filter WizardViewModelTests`

- [ ] **Step 3: Implement `WizardStep.cs`**

```csharp
namespace ReScene.NET.ViewModels.Wizards;

/// <summary>One step in a wizard: a display title and a predicate gating advance to the next step.</summary>
public sealed class WizardStep
{
    public required string Title { get; init; }
    public Func<bool> CanAdvance { get; init; } = static () => true;
}
```

- [ ] **Step 4: Implement `WizardViewModel.cs`**

```csharp
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ReScene.NET.ViewModels.Wizards;

/// <summary>
/// Drives a multi-step wizard: ordered steps, current index, and Back/Next navigation gated by
/// per-step validity. The task ViewModel that owns the real data/commands is exposed as
/// <see cref="Content"/> (used as the DataContext for the step body); this VM only navigates.
/// </summary>
public partial class WizardViewModel : ViewModelBase
{
    public string Title { get; }
    public IReadOnlyList<WizardStep> Steps { get; }
    public object Content { get; }

    public WizardViewModel(string title, object content, IReadOnlyList<WizardStep> steps)
    {
        Title = title;
        Content = content;
        Steps = steps;

        // Step validity often depends on fields of the content VM; re-evaluate Next when it changes.
        if (content is INotifyPropertyChanged notifier)
        {
            notifier.PropertyChanged += (_, _) => NextCommand.NotifyCanExecuteChanged();
        }
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsFirstStep))]
    [NotifyPropertyChangedFor(nameof(IsLastStep))]
    [NotifyPropertyChangedFor(nameof(CurrentStepNumber))]
    [NotifyPropertyChangedFor(nameof(StepHeader))]
    [NotifyCanExecuteChangedFor(nameof(NextCommand))]
    [NotifyCanExecuteChangedFor(nameof(BackCommand))]
    public partial int CurrentStepIndex { get; set; }

    public int StepCount => Steps.Count;
    public int CurrentStepNumber => CurrentStepIndex + 1;
    public bool IsFirstStep => CurrentStepIndex == 0;
    public bool IsLastStep => CurrentStepIndex == Steps.Count - 1;
    public string StepHeader => $"{Steps[CurrentStepIndex].Title}  —  Step {CurrentStepNumber} of {StepCount}";

    private bool CanGoNext() => !IsLastStep && Steps[CurrentStepIndex].CanAdvance();
    private bool CanGoBack() => !IsFirstStep;

    [RelayCommand(CanExecute = nameof(CanGoNext))]
    private void Next()
    {
        if (CanGoNext()) { CurrentStepIndex++; }
    }

    [RelayCommand(CanExecute = nameof(CanGoBack))]
    private void Back()
    {
        if (CanGoBack()) { CurrentStepIndex--; }
    }
}
```

- [ ] **Step 5: Run tests — expect PASS** (30→ existing + 4 new). Run the full file filter and confirm green.

- [ ] **Step 6: Commit** `feat(wizard): add WizardViewModel navigation framework`.

### Task 2: `WizardWindow` (generic chrome)

**Files:**
- Create: `ReScene.NET/Views/Wizards/WizardWindow.xaml`
- Create: `ReScene.NET/Views/Wizards/WizardWindow.xaml.cs`

- [ ] **Step 1: Code-behind**

```csharp
using System.Windows;
using ReScene.NET.Helpers;
using ReScene.NET.ViewModels.Wizards;

namespace ReScene.NET.Views.Wizards;

public partial class WizardWindow : Window
{
    public WizardWindow(WizardViewModel viewModel, FrameworkElement body)
    {
        InitializeComponent();
        DataContext = viewModel;
        body.DataContext = viewModel.Content; // step fields bind to the task VM
        BodyHost.Content = body;
        SourceInitialized += (_, _) => DarkTitleBar.Enable(this);
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();
}
```

- [ ] **Step 2: View**

`ReScene.NET/Views/Wizards/WizardWindow.xaml`:

```xml
<Window x:Class="ReScene.NET.Views.Wizards.WizardWindow"
        xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
        xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
        Title="{Binding Title}"
        Width="580" SizeToContent="Height" MinHeight="300"
        WindowStartupLocation="CenterOwner" ResizeMode="NoResize" ShowInTaskbar="False"
        Background="{DynamicResource WindowBackground}"
        Foreground="{DynamicResource ForegroundPrimary}"
        FontFamily="{DynamicResource UIFontFamily}"
        FontSize="{DynamicResource FontSizeBody}">
  <DockPanel Margin="{DynamicResource PageMargin}">

    <!-- Header -->
    <StackPanel DockPanel.Dock="Top" Margin="0,0,0,12">
      <TextBlock Text="{Binding Title}" FontSize="{DynamicResource FontSizeH1}" FontWeight="SemiBold" />
      <TextBlock Text="{Binding StepHeader}"
                 Foreground="{DynamicResource ForegroundSecondary}"
                 FontSize="{DynamicResource FontSizeCaption}" Margin="0,2,0,0" />
    </StackPanel>

    <!-- Footer -->
    <DockPanel DockPanel.Dock="Bottom" Margin="0,14,0,0">
      <Button DockPanel.Dock="Left" Content="Close" Click="OnCloseClick"
              Style="{StaticResource GhostButton}" MinWidth="80" />
      <StackPanel Orientation="Horizontal" HorizontalAlignment="Right">
        <Button Content="‹ Back" Command="{Binding BackCommand}"
                Style="{StaticResource GhostButton}" MinWidth="90" Margin="0,0,8,0" />
        <Button Content="Next ›" Command="{Binding NextCommand}"
                Style="{StaticResource PrimaryButton}" MinWidth="90"
                Visibility="{Binding IsLastStep, Converter={StaticResource InverseBoolToVisibility}}" />
      </StackPanel>
    </DockPanel>

    <!-- Body: per-task step panels (DataContext = task VM, injected in code-behind) -->
    <ContentPresenter x:Name="BodyHost" />
  </DockPanel>
</Window>
```

- [ ] **Step 3: Build** — `dotnet build ReScene.NET/ReScene.NET.csproj` (0 errors; window not yet referenced).

- [ ] **Step 4: Commit** `feat(wizard): add generic WizardWindow chrome`.

---

## W2–W4 — Task wizard bodies

**Body pattern (applies to all):** a `UserControl` whose root `Grid` holds one `StackPanel` per step. The body's DataContext is the task VM (set by `WizardWindow`), so field bindings are plain (`{Binding InputPath}` etc.). Each step panel's visibility is driven by the **window's** `CurrentStepIndex`:

```xml
Visibility="{Binding DataContext.CurrentStepIndex,
                     RelativeSource={RelativeSource AncestorType=Window},
                     Converter={StaticResource IndexToVisibility}, ConverterParameter=N}"
```

Reuse the field/Browse/FieldStatusLine/action markup from the existing `ReScene.NET/Views/Beginner/Beginner*View.xaml` files (read them for the exact bindings) — just split it across step panels. Code-behind for every body is `InitializeComponent()` only, namespace `ReScene.NET.Views.Wizards`.

### Task 3: `CreateSrrWizardBody` (over `CreatorViewModel`) — full template

**Files:** Create `ReScene.NET/Views/Wizards/CreateSrrWizardBody.xaml(.cs)`

- [ ] **Step 1: Code-behind** — `InitializeComponent()` only, class `CreateSrrWizardBody`, namespace `ReScene.NET.Views.Wizards`.

- [ ] **Step 2: View** — use this as the canonical body shape for the other tasks:

```xml
<UserControl x:Class="ReScene.NET.Views.Wizards.CreateSrrWizardBody"
             xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
             xmlns:c="clr-namespace:ReScene.NET.Controls">
  <Grid MinHeight="150">
    <!-- Step 0: release input -->
    <StackPanel Visibility="{Binding DataContext.CurrentStepIndex, RelativeSource={RelativeSource AncestorType=Window}, Converter={StaticResource IndexToVisibility}, ConverterParameter=0}">
      <TextBlock Text="Point at the release's .sfv or first .rar." Foreground="{DynamicResource ForegroundSecondary}" FontSize="{DynamicResource FontSizeCaption}" TextWrapping="Wrap" Margin="0,0,0,8" />
      <TextBlock Text="Release .sfv or first .rar" FontWeight="SemiBold" Margin="0,0,0,2" />
      <DockPanel Margin="0,0,0,2">
        <Button DockPanel.Dock="Right" Content="Browse" Command="{Binding BrowseInputCommand}" Style="{StaticResource GhostButton}" Margin="4,0,0,0" MinWidth="75" />
        <TextBox Text="{Binding InputPath}" />
      </DockPanel>
      <c:FieldStatusLine Status="{Binding InputStatus}" />
    </StackPanel>
    <!-- Step 1: output -->
    <StackPanel Visibility="{Binding DataContext.CurrentStepIndex, RelativeSource={RelativeSource AncestorType=Window}, Converter={StaticResource IndexToVisibility}, ConverterParameter=1}">
      <TextBlock Text="Where should the .srr be saved?" Foreground="{DynamicResource ForegroundSecondary}" FontSize="{DynamicResource FontSizeCaption}" TextWrapping="Wrap" Margin="0,0,0,8" />
      <TextBlock Text="Save SRR to" FontWeight="SemiBold" Margin="0,0,0,2" />
      <DockPanel Margin="0,0,0,2">
        <Button DockPanel.Dock="Right" Content="Browse" Command="{Binding BrowseOutputCommand}" Style="{StaticResource GhostButton}" Margin="4,0,0,0" MinWidth="75" />
        <TextBox Text="{Binding OutputPath}" />
      </DockPanel>
      <c:FieldStatusLine Status="{Binding OutputStatus}" />
    </StackPanel>
    <!-- Step 2: run + result -->
    <StackPanel Visibility="{Binding DataContext.CurrentStepIndex, RelativeSource={RelativeSource AncestorType=Window}, Converter={StaticResource IndexToVisibility}, ConverterParameter=2}">
      <TextBlock Text="Ready to create the SRR." Foreground="{DynamicResource ForegroundSecondary}" FontSize="{DynamicResource FontSizeCaption}" Margin="0,0,0,8" />
      <StackPanel Orientation="Horizontal">
        <Button Content="Create SRR" Command="{Binding CreateSRRCommand}" Style="{StaticResource PrimaryButton}" Padding="18,6" Margin="0,0,8,0" />
        <Button Content="Cancel" Command="{Binding CancelCreationCommand}" Style="{StaticResource CancelButton}" Padding="18,6" Visibility="{Binding IsCreating, Converter={StaticResource BoolToVisibility}}" />
      </StackPanel>
      <ProgressBar Value="{Binding ProgressPercent}" Maximum="100" Height="6" Margin="0,12,0,0" Visibility="{Binding ShowProgress, Converter={StaticResource BoolToVisibility}}" />
      <TextBlock Text="{Binding ProgressMessage}" Foreground="{DynamicResource ForegroundSecondary}" FontSize="{DynamicResource FontSizeCaption}" Margin="0,4,0,0" Visibility="{Binding ShowProgress, Converter={StaticResource BoolToVisibility}}" />
    </StackPanel>
  </Grid>
</UserControl>
```

- [ ] **Step 3: Build & commit** `feat(wizard): add Create SRR wizard body`.

### Task 4: `CreateSrsWizardBody` (over `SRSCreatorViewModel`)

Same 3-panel shape. Steps:
- **0 — Sample:** `InputPath` + Browse (`BrowseInputCommand`) + `c:FieldStatusLine Status="{Binding SampleStatus}"`; plus the ISO selection row (`ShowISOSelection` visibility, `ISOMediaFiles`/`SelectedISOMediaFile` combo) copied from `BeginnerSrsCreatorView.xaml`.
- **1 — Output:** `OutputPath` + Browse (`BrowseOutputCommand`) + `OutputStatus`.
- **2 — Run:** `CreateSRSCommand` / `CancelCreationCommand` (`IsCreating`) + progress (`ProgressPercent`/`ProgressMessage`/`ShowProgress`).

- [ ] Build & commit `feat(wizard): add Create SRS wizard body`.

### Task 5: `ReconstructWizardBody` (over `ReconstructorViewModel`) — 5 panels

- **0 — Import SRR:** the "Import from SRR..." button (`ImportSRRCommand`) + the custom-packer warning border (`HasCustomPackerWarning`/`CustomPackerWarning`) from `BeginnerReconstructorView.xaml`.
- **1 — WinRAR folder:** `WinRarPath` + Browse (`BrowseWinRarCommand`) + `WinRarStatus`.
- **2 — Release files:** `ReleasePath` + Browse (`BrowseReleaseCommand`) + `ReleaseStatus`.
- **3 — Output:** `OutputPath` + Browse (`BrowseOutputCommand`).
- **4 — Run:** `StartCommand` / `StopCommand` (`IsRunning`) + progress (`ProgressPercent`/`ProgressMessage`/`ShowProgress`).

- [ ] Build & commit `feat(wizard): add Reconstruct wizard body`.

### Task 6: `RestoreWizardBody` (over `BeginnerRestoreViewModel`) — 3 panels

DataContext is `BeginnerRestoreViewModel`; reach sub-VMs via `BulkRestorer.*` / `SingleRebuilder.*` and gate with `IsBulk`/`IsSingle`.
- **0 — Pick file:** `InputPath` + Browse (`BrowseInputCommand`) + `c:FieldStatusLine Status="{Binding InputStatus}"`.
- **1 — Media + output** (shown after a kind is resolved; use `IsBulk`/`IsSingle` within the panel):
  - Bulk: `BulkRestorer.MediaDirectoryPath` + Browse (`BulkRestorer.BrowseMediaDirectoryCommand`) + `BulkRestorer.MatchStatus`; `BulkRestorer.OutputDirectoryPath` + `BulkRestorer.BrowseOutputDirectoryCommand`; the `BulkRestorer.SRSEntries` grid (Sample + Status columns, read-only).
  - Single: `SingleRebuilder.MediaFilePath` + Browse (`SingleRebuilder.BrowseMediaCommand`) + `SingleRebuilder.MediaStatus`; `SingleRebuilder.OutputPath` + `SingleRebuilder.BrowseOutputCommand`.
- **2 — Run:** Bulk → `BulkRestorer.RestoreCommand`/`CancelRestoreCommand` (`BulkRestorer.IsRestoring`) + `BulkRestorer.OverallProgressText`; Single → `SingleRebuilder.RebuildCommand` + result box (`SingleRebuilder.ShowResult`/`ResultSuccess`/`ResultSummary`). Wrap each in `IsBulk`/`IsSingle` visibility.

- [ ] Build & commit `feat(wizard): add Restore wizard body`.

---

## W5 — Factory, hub wiring, cleanup

### Task 7: `BeginnerWizardFactory`

**Files:** Create `ReScene.NET/Views/Wizards/BeginnerWizardFactory.cs`

Builds the `WizardViewModel` (steps + per-step validity referencing the task VM) and the matching body for a card. Lives in the View layer because it news up body UserControls.

- [ ] **Step 1: Implement**

```csharp
using System.Windows;
using ReScene.NET.Models;
using ReScene.NET.ViewModels;
using ReScene.NET.ViewModels.Wizards;

namespace ReScene.NET.Views.Wizards;

/// <summary>Assembles the wizard (navigation VM + body view) for a Beginner hub card.</summary>
public static class BeginnerWizardFactory
{
    public static (WizardViewModel ViewModel, FrameworkElement Body) Create(BeginnerCard card, BeginnerShellViewModel shell)
    {
        return card switch
        {
            BeginnerCard.CreateSrr => BuildCreateSrr(shell.Creator),
            BeginnerCard.CreateSrs => BuildCreateSrs(shell.SRSCreator),
            BeginnerCard.Reconstruct => BuildReconstruct(shell.Reconstructor),
            BeginnerCard.Restore => BuildRestore(shell.Restore),
            _ => throw new ArgumentOutOfRangeException(nameof(card)),
        };
    }

    private static (WizardViewModel, FrameworkElement) BuildCreateSrr(CreatorViewModel vm)
    {
        var steps = new List<WizardStep>
        {
            new() { Title = "Choose the release", CanAdvance = () => vm.InputStatus.State != FieldState.Error && !string.IsNullOrWhiteSpace(vm.InputPath) },
            new() { Title = "Choose where to save", CanAdvance = () => !string.IsNullOrWhiteSpace(vm.OutputPath) },
            new() { Title = "Create" },
        };
        return (new WizardViewModel("Create an SRR", vm, steps), new CreateSrrWizardBody());
    }

    private static (WizardViewModel, FrameworkElement) BuildCreateSrs(SRSCreatorViewModel vm)
    {
        var steps = new List<WizardStep>
        {
            new() { Title = "Choose the sample", CanAdvance = () => vm.SampleStatus.State != FieldState.Error && !string.IsNullOrWhiteSpace(vm.InputPath) && (!vm.IsISOSource || vm.SelectedISOMediaFile is not null) },
            new() { Title = "Choose where to save", CanAdvance = () => !string.IsNullOrWhiteSpace(vm.OutputPath) },
            new() { Title = "Create" },
        };
        return (new WizardViewModel("Create a sample SRS", vm, steps), new CreateSrsWizardBody());
    }

    private static (WizardViewModel, FrameworkElement) BuildReconstruct(ReconstructorViewModel vm)
    {
        var steps = new List<WizardStep>
        {
            new() { Title = "Import the SRR", CanAdvance = () => !string.IsNullOrWhiteSpace(vm.VerificationPath) && !vm.HasCustomPackerWarning },
            new() { Title = "WinRAR versions", CanAdvance = () => !string.IsNullOrWhiteSpace(vm.WinRarPath) },
            new() { Title = "Extracted files", CanAdvance = () => !string.IsNullOrWhiteSpace(vm.ReleasePath) },
            new() { Title = "Output folder", CanAdvance = () => !string.IsNullOrWhiteSpace(vm.OutputPath) },
            new() { Title = "Reconstruct" },
        };
        return (new WizardViewModel("Reconstruct RAR archives", vm, steps), new ReconstructWizardBody());
    }

    private static (WizardViewModel, FrameworkElement) BuildRestore(BeginnerRestoreViewModel vm)
    {
        var steps = new List<WizardStep>
        {
            new() { Title = "Choose your file", CanAdvance = () => vm.ShowFlow },
            new() { Title = "Media & output", CanAdvance = () => vm.IsBulk
                ? !string.IsNullOrWhiteSpace(vm.BulkRestorer.MediaDirectoryPath)
                : !string.IsNullOrWhiteSpace(vm.SingleRebuilder.MediaFilePath) },
            new() { Title = "Restore" },
        };
        return (new WizardViewModel("Restore a sample", vm, steps), new RestoreWizardBody());
    }
}
```

> Confirm `FieldState` is in `ReScene.NET.Models` and the property names match the VMs (they were verified during the prior feature). Adjust any predicate whose property differs.

- [ ] **Step 2: Build & commit** `feat(wizard): add BeginnerWizardFactory`.

### Task 8: Open wizards from the hub; simplify the shell; remove old views

**Files:** Modify `ReScene.NET/Views/BeginnerShellView.xaml(.cs)`, `ReScene.NET/ViewModels/BeginnerShellViewModel.cs`; delete the six `Views/Beginner/*` files.

- [ ] **Step 1: `BeginnerShellViewModel`** — remove `CurrentCard`, `IsHubVisible`, `ShowCreateSrr/Srs/Reconstruct/Restore`, `CurrentTitle`, `OpenCardCommand`, `BackToHubCommand`, `OpenInAdvancedCommand`, `OpenInAdvancedAction`. Keep the four task-VM properties (`Creator`, `SRSCreator`, `Reconstructor`, `Restore`). Update `MainWindowViewModel` if it set `OpenInAdvancedAction` (remove that assignment).

- [ ] **Step 2: `BeginnerShellView.xaml`** — keep only the hub (the 4-card `UniformGrid` + heading). Delete the task-area `DockPanel` (back button / title / child views). Change each card `Button` from `Command="{Binding OpenCardCommand}" CommandParameter="..."` to `Click="OnCardClick"` with `Tag="{x:Static vm:BeginnerCard.CreateSrr}"` (etc.). Remove the now-unused `xmlns:bv` and the `HubCardButton` template stays.

- [ ] **Step 3: `BeginnerShellView.xaml.cs`** — add the click handler:

```csharp
using System.Windows;
using System.Windows.Controls;
using ReScene.NET.ViewModels;
using ReScene.NET.Views.Wizards;

namespace ReScene.NET.Views;

public partial class BeginnerShellView : UserControl
{
    public BeginnerShellView() => InitializeComponent();

    private void OnCardClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement fe || fe.Tag is not BeginnerCard card) { return; }
        if (DataContext is not BeginnerShellViewModel shell) { return; }

        (var wizardVm, var body) = BeginnerWizardFactory.Create(card, shell);
        var window = new WizardWindow(wizardVm, body) { Owner = Window.GetWindow(this) };
        window.ShowDialog();
    }
}
```

- [ ] **Step 4: Delete** the six superseded files: `Views/Beginner/BeginnerCreatorView`, `BeginnerSrsCreatorView`, `BeginnerReconstructorView`, `BeginnerRestoreView`, `BeginnerRestoreBulkView`, `BeginnerRestoreSingleView` (`.xaml` + `.xaml.cs`). Use `git rm`.

- [ ] **Step 5: Build & test** — `dotnet build` + `dotnet test` (the WizardViewModel tests plus all prior tests; expect the BeginnerShellViewModel nav tests to be removed/updated — delete `BeginnerShellViewModelTests` assertions that referenced removed members, or replace with a trivial construction test). Report totals.

- [ ] **Step 6: Commit** `feat(beginner): open pop-up wizards from the hub; retire in-place screens`.

---

## W6 — Verify & finish

### Task 9: Full verification

- [ ] `dotnet build ReScene.NET.slnx` — 0 errors.
- [ ] `dotnet test ReScene.NET.Tests/ReScene.NET.Tests.csproj` — all green.
- [ ] Manual smoke (you, on launch): each card opens a centered modal wizard; Back/Next gate correctly; Next disabled until the step is valid; the last step runs the action with progress/result; Close returns to the hub; Reconstruct blocks Next past step 1 on a custom-packer SRR; Restore step 2 shows folder (SRR) vs file (SRS).
- [ ] Final whole-diff review, then finish the branch (merge per your choice).

## Notes / risks
- `BeginnerShellViewModelTests` from the previous feature asserts the removed in-place nav (CurrentCard/Show*). Those tests must be deleted or rewritten in Task 8; the new navigation lives in code-behind (`OnCardClick`) and isn't unit-tested (it opens a window). The wizard *navigation* is covered by `WizardViewModelTests`.
- The shared task VMs persist state across wizard opens (e.g., a path entered earlier remains). Acceptable; if undesired later, reset on open.
- Closing the window mid-run leaves the shared task op running. Acceptable for now (matches prior behavior).

