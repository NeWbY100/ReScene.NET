# Beginner / Advanced Mode Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add a persisted Beginner/Advanced UI mode — Advanced is today's 8-tab shell unchanged; Beginner is a guided hub of four simplified task screens bound to the existing ViewModels.

**Architecture:** `MainWindow` becomes a thin frame: a top-bar mode toggle plus a `Grid` hosting either `AdvancedShellView` (today's `TabControl`, lifted near-verbatim) or `BeginnerShellView` (a 4-card hub that navigates to minimal task views). Beginner views are alternate XAML over the **same shared** task ViewModels (`CreatorViewModel`, `SRSCreatorViewModel`, `ReconstructorViewModel`, `SampleRestorerViewModel`, `SRSReconstructorViewModel`) — no duplicated business logic. Mode persists via the existing `AppSettings`/`AppSettingsService` and swaps live through the existing `AppSettingsService.Changed` event.

**Tech Stack:** .NET 10, WPF, CommunityToolkit.Mvvm (partial-property `[ObservableProperty]`), xUnit (test project `ReScene.NET.Tests`, net10.0-windows).

**Conventions:**
- Conventional commit messages (`feat:`, `refactor:`, `test:`). End every commit message with the repo trailer: `Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>`.
- Build the app: `dotnet build ReScene.NET/ReScene.NET.csproj`
- Run tests: `dotnet test ReScene.NET.Tests/ReScene.NET.Tests.csproj`
- New C# files: add explicit `using` lines shown in each task (the app enables ImplicitUsings, but be explicit for clarity).

---

## File Structure

**New files**
- `ReScene.NET/Models/UserMode.cs` — `enum UserMode { Beginner, Advanced }`.
- `ReScene.NET/ViewModels/BeginnerCard.cs` — `enum BeginnerCard { CreateSrr, CreateSrs, Reconstruct, Restore }`.
- `ReScene.NET/Helpers/SampleRestoreRouter.cs` — pure router: file path → `SampleRestoreKind { Srr, Srs, Unknown }`.
- `ReScene.NET/ViewModels/BeginnerShellViewModel.cs` — hub navigation state (which card is open) + references to shared task VMs + "open in Advanced" callback.
- `ReScene.NET/ViewModels/BeginnerRestoreViewModel.cs` — smart-restore router VM: one input file → routes to bulk (`SampleRestorerViewModel`) or single (`SRSReconstructorViewModel`).
- `ReScene.NET/Views/AdvancedShellView.xaml(.cs)` — the existing `TabControl` moved into a UserControl.
- `ReScene.NET/Views/BeginnerShellView.xaml(.cs)` — the hub + task-area host.
- `ReScene.NET/Views/Beginner/BeginnerCreatorView.xaml(.cs)` — simplified SRR Creator.
- `ReScene.NET/Views/Beginner/BeginnerSrsCreatorView.xaml(.cs)` — simplified SRS Creator.
- `ReScene.NET/Views/Beginner/BeginnerReconstructorView.xaml(.cs)` — guided RAR Reconstructor (import-driven).
- `ReScene.NET/Views/Beginner/BeginnerRestoreView.xaml(.cs)` — smart restore (file picker + routed sub-flow).
- `ReScene.NET/Views/Beginner/BeginnerRestoreBulkView.xaml(.cs)` — SRR bulk restore body.
- `ReScene.NET/Views/Beginner/BeginnerRestoreSingleView.xaml(.cs)` — single SRS rebuild body.
- `ReScene.NET.Tests/AppSettingsModeTests.cs`, `SampleRestoreRouterTests.cs`, `BeginnerShellViewModelTests.cs`, `BeginnerRestoreViewModelTests.cs`.

**Modified files**
- `ReScene.NET/Models/AppSettings.cs` — add nullable `Mode`.
- `ReScene.NET/Services/AppSettingsService.cs` — add `ResolveStartupMode` + resolve in `Load`.
- `ReScene.NET/ViewModels/SettingsViewModel.cs` — load/save/expose `Mode`.
- `ReScene.NET/Views/SettingsWindow.xaml` — Mode radio buttons.
- `ReScene.NET/ViewModels/MainWindowViewModel.cs` — `Mode`, mode bools, mode commands, `Beginner` shell VM, persistence + `Changed` subscription, `OpenSceneFile` forces Advanced.
- `ReScene.NET/ViewModels/SRSCreatorViewModel.cs` — auto-select first ISO media file.
- `ReScene.NET/Views/MainWindow.xaml` — top-bar toggle + shell host `Grid`.
- `ReScene.NET/Views/MainWindow.xaml.cs` — clamp restored tab index, gate `Ctrl+1–7` to Advanced.

---

## Phase 0 — Mode setting & persistence (no visible change)

### Task 1: `UserMode` enum

**Files:**
- Create: `ReScene.NET/Models/UserMode.cs`

- [ ] **Step 1: Create the enum**

```csharp
namespace ReScene.NET.Models;

/// <summary>
/// UI complexity mode. Beginner shows a guided task hub; Advanced shows the full tabbed layout.
/// </summary>
public enum UserMode
{
    Beginner,
    Advanced,
}
```

- [ ] **Step 2: Build**

Run: `dotnet build ReScene.NET/ReScene.NET.csproj`
Expected: build succeeds.

- [ ] **Step 3: Commit**

```bash
git add ReScene.NET/Models/UserMode.cs
git commit -m "feat(mode): add UserMode enum"
```

### Task 2: Persist `Mode` with startup resolution rule

The rule: no settings file ⇒ brand-new install ⇒ **Beginner**; settings file present but no `Mode` field ⇒ existing user ⇒ **Advanced**; explicit value ⇒ honored.

**Files:**
- Modify: `ReScene.NET/Models/AppSettings.cs`
- Modify: `ReScene.NET/Services/AppSettingsService.cs`
- Test: `ReScene.NET.Tests/AppSettingsModeTests.cs`

- [ ] **Step 1: Write the failing test**

Create `ReScene.NET.Tests/AppSettingsModeTests.cs`:

```csharp
using ReScene.NET.Models;
using ReScene.NET.Services;

namespace ReScene.NET.Tests;

public class AppSettingsModeTests
{
    [Fact]
    public void ResolveStartupMode_NoFile_DefaultsToBeginner()
        => Assert.Equal(UserMode.Beginner, AppSettingsService.ResolveStartupMode(settingsFileExisted: false, persistedMode: null));

    [Fact]
    public void ResolveStartupMode_ExistingFileWithoutMode_DefaultsToAdvanced()
        => Assert.Equal(UserMode.Advanced, AppSettingsService.ResolveStartupMode(settingsFileExisted: true, persistedMode: null));

    [Theory]
    [InlineData(UserMode.Beginner)]
    [InlineData(UserMode.Advanced)]
    public void ResolveStartupMode_PersistedValue_IsHonored(UserMode persisted)
        => Assert.Equal(persisted, AppSettingsService.ResolveStartupMode(settingsFileExisted: true, persistedMode: persisted));
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test ReScene.NET.Tests/ReScene.NET.Tests.csproj --filter AppSettingsModeTests`
Expected: FAIL — `AppSettingsService.ResolveStartupMode` does not exist (compile error).

- [ ] **Step 3: Add `Mode` to the model**

In `ReScene.NET/Models/AppSettings.cs`, add after the `RecentFilesLimit` property (keep the existing `using ReScene.NET.Helpers;`):

```csharp
    /// <summary>
    /// Gets or sets the persisted UI mode. Null means "not yet chosen" — resolved at load time.
    /// </summary>
    public UserMode? Mode { get; set; }
```

- [ ] **Step 4: Add resolution + wire into Load**

In `ReScene.NET/Services/AppSettingsService.cs`, add this static method to the class:

```csharp
    /// <summary>
    /// Resolves the effective startup mode: a brand-new install (no file) starts in Beginner;
    /// an existing user whose settings predate this feature (file present, no Mode) stays in Advanced.
    /// </summary>
    public static UserMode ResolveStartupMode(bool settingsFileExisted, UserMode? persistedMode)
        => persistedMode ?? (settingsFileExisted ? UserMode.Advanced : UserMode.Beginner);
```

Replace the existing `Load()` body with:

```csharp
    public AppSettings Load()
    {
        bool fileExisted = File.Exists(_filePath);
        AppSettings settings;
        try
        {
            settings = fileExisted
                ? JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(_filePath)) ?? new AppSettings()
                : new AppSettings();
        }
        catch
        {
            settings = new AppSettings();
        }

        settings.Mode = ResolveStartupMode(fileExisted, settings.Mode);
        return settings;
    }
```

- [ ] **Step 5: Run tests to verify they pass**

Run: `dotnet test ReScene.NET.Tests/ReScene.NET.Tests.csproj --filter AppSettingsModeTests`
Expected: PASS (4 tests).

- [ ] **Step 6: Commit**

```bash
git add ReScene.NET/Models/AppSettings.cs ReScene.NET/Services/AppSettingsService.cs ReScene.NET.Tests/AppSettingsModeTests.cs
git commit -m "feat(mode): persist UserMode with first-run resolution rule"
```

### Task 3: Preserve `Mode` through the Settings dialog

`SettingsViewModel.Save()` builds a fresh `AppSettings`, so without this it would wipe `Mode` to null (silently demoting users to Advanced). Add Mode load/save now, before any UI shows it.

**Files:**
- Modify: `ReScene.NET/ViewModels/SettingsViewModel.cs`

- [ ] **Step 1: Add Mode property + helper bools**

In `SettingsViewModel`, add `using ReScene.NET.Models;` is already present. Add these members alongside the other `[ObservableProperty]` declarations:

```csharp
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsBeginnerMode))]
    [NotifyPropertyChangedFor(nameof(IsAdvancedMode))]
    public partial UserMode Mode { get; set; }

    public bool IsBeginnerMode
    {
        get => Mode == UserMode.Beginner;
        set { if (value) { Mode = UserMode.Beginner; } }
    }

    public bool IsAdvancedMode
    {
        get => Mode == UserMode.Advanced;
        set { if (value) { Mode = UserMode.Advanced; } }
    }
```

- [ ] **Step 2: Load Mode in the constructor**

In the `SettingsViewModel` constructor, after `RecentFilesLimit = settings.RecentFilesLimit;` add:

```csharp
        Mode = settings.Mode ?? UserMode.Advanced;
```

- [ ] **Step 3: Save Mode**

In `Save()`, add `Mode = Mode,` to the `new AppSettings { ... }` initializer:

```csharp
        _settingsService.Save(new AppSettings
        {
            DefaultAppName = DefaultAppName,
            DefaultOutputDirectory = DefaultOutputDirectory,
            RecentFilesLimit = Math.Clamp(RecentFilesLimit, 1, 100),
            Mode = Mode
        });
```

- [ ] **Step 4: Build**

Run: `dotnet build ReScene.NET/ReScene.NET.csproj`
Expected: build succeeds.

- [ ] **Step 5: Commit**

```bash
git add ReScene.NET/ViewModels/SettingsViewModel.cs
git commit -m "feat(mode): round-trip UserMode through the settings dialog"
```

---

## Phase 1 — Shell refactor + top-bar toggle

After this phase Advanced mode behaves exactly as before; Beginner mode shows an empty placeholder (the real hub arrives in Phase 2).

### Task 4: Mode state + commands on `MainWindowViewModel`

**Files:**
- Modify: `ReScene.NET/ViewModels/MainWindowViewModel.cs`

- [ ] **Step 1: Add `using` for Models**

At the top of `MainWindowViewModel.cs`, ensure `using ReScene.NET.Models;` is present (add it under the existing `using ReScene.NET.Helpers;`).

- [ ] **Step 2: Add Mode property, derived bools, and commands**

Add these members to the class (next to the other `[ObservableProperty]` declarations such as `SelectedTabIndex`):

```csharp
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsAdvancedMode))]
    [NotifyPropertyChangedFor(nameof(IsBeginnerMode))]
    public partial UserMode Mode { get; set; }

    public bool IsAdvancedMode => Mode == UserMode.Advanced;

    public bool IsBeginnerMode => Mode == UserMode.Beginner;

    private bool _applyingExternalModeChange;

    [RelayCommand]
    private void SetBeginnerMode() => Mode = UserMode.Beginner;

    [RelayCommand]
    private void SetAdvancedMode() => Mode = UserMode.Advanced;

    partial void OnModeChanged(UserMode value)
    {
        if (_applyingExternalModeChange)
        {
            return;
        }

        AppSettings settings = _appSettingsService.Load();
        settings.Mode = value;
        _appSettingsService.Save(settings);
    }

    private void OnSettingsChanged(object? sender, EventArgs e)
    {
        UserMode resolved = _appSettingsService.Load().Mode ?? Mode;
        if (resolved == Mode)
        {
            return;
        }

        _applyingExternalModeChange = true;
        Mode = resolved;
        _applyingExternalModeChange = false;
    }
```

- [ ] **Step 3: Initialize Mode + subscribe at the end of the full constructor**

At the **end** of the second (parameterful) `MainWindowViewModel(...)` constructor body — after the `SampleRestorer.PropertyChanged += ...` block — add:

```csharp
        Mode = _appSettingsService.Load().Mode ?? UserMode.Advanced;
        _appSettingsService.Changed += OnSettingsChanged;
```

(Setting `Mode` before subscribing means the startup persistence write fires `Changed` with no subscriber, avoiding a redundant reload.)

- [ ] **Step 4: Force Advanced when opening a scene file**

In `OpenSceneFile(string filePath)`, add as the first line of the method body:

```csharp
        Mode = UserMode.Advanced;
```

(The Inspector lives only in Advanced, so opening a file via menu/drag-drop/CLI flips to Advanced so the result is visible.)

- [ ] **Step 5: Build**

Run: `dotnet build ReScene.NET/ReScene.NET.csproj`
Expected: build succeeds.

- [ ] **Step 6: Commit**

```bash
git add ReScene.NET/ViewModels/MainWindowViewModel.cs
git commit -m "feat(mode): add Mode state, commands, and persistence wiring to MainWindowViewModel"
```

### Task 5: Extract `AdvancedShellView`

Move the `TabControl` (the 8 `TabItem`s) out of `MainWindow.xaml` into a UserControl, unchanged.

**Files:**
- Create: `ReScene.NET/Views/AdvancedShellView.xaml`
- Create: `ReScene.NET/Views/AdvancedShellView.xaml.cs`

- [ ] **Step 1: Create the code-behind**

`ReScene.NET/Views/AdvancedShellView.xaml.cs`:

```csharp
using System.Windows.Controls;

namespace ReScene.NET.Views;

public partial class AdvancedShellView : UserControl
{
    public AdvancedShellView()
    {
        InitializeComponent();
    }
}
```

- [ ] **Step 2: Create the view (TabControl moved verbatim)**

`ReScene.NET/Views/AdvancedShellView.xaml`:

```xml
<UserControl x:Class="ReScene.NET.Views.AdvancedShellView"
             xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
             xmlns:v="clr-namespace:ReScene.NET.Views">
  <TabControl SelectedIndex="{Binding SelectedTabIndex}" Padding="0">
    <TabItem Header="Home">
      <v:HomeView DataContext="{Binding Home}" />
    </TabItem>
    <TabItem Header="Inspector">
      <v:InspectorView DataContext="{Binding Inspector}" />
    </TabItem>
    <TabItem Header="SRR Creator">
      <v:CreatorView DataContext="{Binding Creator}" />
    </TabItem>
    <TabItem Header="SRS Creator">
      <v:SRSCreatorView DataContext="{Binding SRSCreator}" />
    </TabItem>
    <TabItem Header="RAR Reconstructor">
      <v:ReconstructorView DataContext="{Binding Reconstructor}" />
    </TabItem>
    <TabItem Header="SRS Reconstructor">
      <v:SRSReconstructorView DataContext="{Binding SRSReconstructor}" />
    </TabItem>
    <TabItem Header="SRS Restorer">
      <v:SampleRestorerView DataContext="{Binding SampleRestorer}" />
    </TabItem>
    <TabItem Header="Compare">
      <v:FileCompareView DataContext="{Binding FileCompare}" />
    </TabItem>
  </TabControl>
</UserControl>
```

- [ ] **Step 3: Build**

Run: `dotnet build ReScene.NET/ReScene.NET.csproj`
Expected: build succeeds (view not yet referenced).

- [ ] **Step 4: Commit**

```bash
git add ReScene.NET/Views/AdvancedShellView.xaml ReScene.NET/Views/AdvancedShellView.xaml.cs
git commit -m "refactor(shell): extract the tab layout into AdvancedShellView"
```

### Task 6: Convert `MainWindow` into a shell frame

**Files:**
- Modify: `ReScene.NET/Views/MainWindow.xaml`
- Modify: `ReScene.NET/Views/MainWindow.xaml.cs`

- [ ] **Step 1: Replace the menu region with a menu + mode toggle row**

In `MainWindow.xaml`, replace the entire `<Menu DockPanel.Dock="Top"> ... </Menu>` element with:

```xml
    <!-- ══ Top bar: menu + mode toggle ═══════════════════════════════ -->
    <DockPanel DockPanel.Dock="Top">
      <StackPanel DockPanel.Dock="Right" Orientation="Horizontal"
                  VerticalAlignment="Center" Margin="0,0,8,0">
        <TextBlock Text="Mode:" VerticalAlignment="Center"
                   Foreground="{DynamicResource ForegroundSecondary}"
                   FontSize="{DynamicResource FontSizeCaption}" Margin="0,0,6,0" />
        <ToggleButton Content="Beginner" MinWidth="74"
                      IsChecked="{Binding IsBeginnerMode, Mode=OneWay}"
                      Command="{Binding SetBeginnerModeCommand}"
                      Style="{StaticResource ToolbarToggleButton}" />
        <ToggleButton Content="Advanced" MinWidth="74"
                      IsChecked="{Binding IsAdvancedMode, Mode=OneWay}"
                      Command="{Binding SetAdvancedModeCommand}"
                      Style="{StaticResource ToolbarToggleButton}" />
      </StackPanel>
      <Menu>
        <MenuItem Header="_File">
          <MenuItem Header="_Open..." Command="{Binding OpenFileCommand}" InputGestureText="Ctrl+O" />
          <MenuItem Header="_Export Stored File..." Command="{Binding ExportStoredFileCommand}" InputGestureText="Ctrl+E" />
          <Separator />
          <MenuItem Header="E_xit" Click="OnExitClick" />
        </MenuItem>
        <MenuItem Header="_Help">
          <MenuItem Header="_Settings..." Click="OnSettingsClick" />
          <Separator />
          <MenuItem Header="_About" Click="OnAboutClick" />
        </MenuItem>
      </Menu>
    </DockPanel>
```

- [ ] **Step 2: Replace the `TabControl` with the shell host**

In `MainWindow.xaml`, replace the entire `<TabControl ...> ... </TabControl>` block with:

```xml
    <!-- ══ Shell host (Advanced tabs OR Beginner hub) ════════════════ -->
    <Grid>
      <v:AdvancedShellView Visibility="{Binding IsAdvancedMode, Converter={StaticResource BoolToVisibility}}" />
      <ContentControl Content="{Binding Beginner}"
                      Visibility="{Binding IsBeginnerMode, Converter={StaticResource BoolToVisibility}}">
        <ContentControl.ContentTemplate>
          <DataTemplate>
            <v:BeginnerShellView />
          </DataTemplate>
        </ContentControl.ContentTemplate>
      </ContentControl>
    </Grid>
```

> NOTE: `MainWindowViewModel.Beginner` and `BeginnerShellView` do not exist until Phase 2. To keep this task independently buildable, temporarily replace the `<ContentControl>...</ContentControl>` above with a placeholder `<TextBlock Text="Beginner mode (coming soon)" Visibility="{Binding IsBeginnerMode, Converter={StaticResource BoolToVisibility}}" HorizontalAlignment="Center" VerticalAlignment="Center" Foreground="{DynamicResource ForegroundSecondary}" />` and swap in the real `ContentControl` in Phase 2 Task 9.

- [ ] **Step 3: Clamp the restored tab index**

In `MainWindow.xaml.cs`, inside `RestoreWindowState()`, change:

```csharp
            vm.SelectedTabIndex = state.SelectedTabIndex;
```
to:
```csharp
            vm.SelectedTabIndex = Math.Clamp(state.SelectedTabIndex, 0, 7);
```

- [ ] **Step 4: Gate the tab shortcuts to Advanced mode**

In `MainWindow.xaml.cs`, replace the body of `OnPreviewKeyDown` with:

```csharp
    private void OnPreviewKeyDown(object _, KeyEventArgs e)
    {
        // Ctrl+1 through Ctrl+7 switch tabs (Advanced mode only)
        if (Keyboard.Modifiers == ModifierKeys.Control && e.Key >= Key.D1 && e.Key <= Key.D7)
        {
            if (DataContext is MainWindowViewModel vm && vm.IsAdvancedMode)
            {
                vm.SelectedTabIndex = e.Key - Key.D1;
                e.Handled = true;
            }
        }
    }
```

- [ ] **Step 5: Build & run**

Run: `dotnet build ReScene.NET/ReScene.NET.csproj`
Expected: build succeeds. Launch the app; Advanced mode shows the usual tabs. Toggle to Beginner → placeholder text; toggle back → tabs. Close and relaunch → mode persisted.

- [ ] **Step 6: Commit**

```bash
git add ReScene.NET/Views/MainWindow.xaml ReScene.NET/Views/MainWindow.xaml.cs
git commit -m "refactor(shell): host Advanced/Beginner shells behind the mode toggle"
```

---

## Phase 2 — Beginner shell + hub

### Task 7: `BeginnerCard` enum

**Files:**
- Create: `ReScene.NET/ViewModels/BeginnerCard.cs`

- [ ] **Step 1: Create the enum**

```csharp
namespace ReScene.NET.ViewModels;

/// <summary>
/// The task cards shown on the Beginner hub.
/// </summary>
public enum BeginnerCard
{
    CreateSrr,
    CreateSrs,
    Reconstruct,
    Restore,
}
```

- [ ] **Step 2: Commit**

```bash
git add ReScene.NET/ViewModels/BeginnerCard.cs
git commit -m "feat(beginner): add BeginnerCard enum"
```

### Task 8: `BeginnerShellViewModel` (navigation)

Navigation state is independent of the task VMs (which are set via object-initializer by `MainWindowViewModel`), so it is unit-testable with no service fakes.

**Files:**
- Create: `ReScene.NET/ViewModels/BeginnerShellViewModel.cs`
- Test: `ReScene.NET.Tests/BeginnerShellViewModelTests.cs`

- [ ] **Step 1: Write the failing test**

`ReScene.NET.Tests/BeginnerShellViewModelTests.cs`:

```csharp
using ReScene.NET.ViewModels;

namespace ReScene.NET.Tests;

public class BeginnerShellViewModelTests
{
    [Fact]
    public void NewShell_StartsOnHub()
    {
        var vm = new BeginnerShellViewModel();
        Assert.True(vm.IsHubVisible);
        Assert.Null(vm.CurrentCard);
        Assert.False(vm.ShowCreateSrr);
    }

    [Fact]
    public void OpenCard_LeavesHub_AndSetsTheMatchingShowFlag()
    {
        var vm = new BeginnerShellViewModel();
        vm.OpenCardCommand.Execute(BeginnerCard.Reconstruct);

        Assert.False(vm.IsHubVisible);
        Assert.Equal(BeginnerCard.Reconstruct, vm.CurrentCard);
        Assert.True(vm.ShowReconstruct);
        Assert.False(vm.ShowCreateSrr);
    }

    [Fact]
    public void BackToHub_ReturnsToHub()
    {
        var vm = new BeginnerShellViewModel();
        vm.OpenCardCommand.Execute(BeginnerCard.CreateSrs);
        vm.BackToHubCommand.Execute(null);

        Assert.True(vm.IsHubVisible);
        Assert.Null(vm.CurrentCard);
    }

    [Fact]
    public void OpenInAdvanced_InvokesCallbackWithCard()
    {
        BeginnerCard? captured = null;
        var vm = new BeginnerShellViewModel { OpenInAdvancedAction = c => captured = c };
        vm.OpenInAdvancedCommand.Execute(BeginnerCard.Restore);

        Assert.Equal(BeginnerCard.Restore, captured);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test ReScene.NET.Tests/ReScene.NET.Tests.csproj --filter BeginnerShellViewModelTests`
Expected: FAIL — `BeginnerShellViewModel` does not exist.

- [ ] **Step 3: Implement the ViewModel**

`ReScene.NET/ViewModels/BeginnerShellViewModel.cs`:

```csharp
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ReScene.NET.ViewModels;

/// <summary>
/// Hosts the Beginner hub. Holds references to the shared task ViewModels and tracks which
/// task card (if any) is open. Navigation state is independent of the task VMs.
/// </summary>
public partial class BeginnerShellViewModel : ViewModelBase
{
    // Shared task ViewModels, assigned by MainWindowViewModel via object initializer.
    public CreatorViewModel Creator { get; set; } = null!;
    public SRSCreatorViewModel SRSCreator { get; set; } = null!;
    public ReconstructorViewModel Reconstructor { get; set; } = null!;
    public BeginnerRestoreViewModel Restore { get; set; } = null!;

    /// <summary>Invoked with the current card to switch to Advanced mode on the matching tab.</summary>
    public Action<BeginnerCard>? OpenInAdvancedAction { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsHubVisible))]
    [NotifyPropertyChangedFor(nameof(ShowCreateSrr))]
    [NotifyPropertyChangedFor(nameof(ShowCreateSrs))]
    [NotifyPropertyChangedFor(nameof(ShowReconstruct))]
    [NotifyPropertyChangedFor(nameof(ShowRestore))]
    [NotifyPropertyChangedFor(nameof(CurrentTitle))]
    public partial BeginnerCard? CurrentCard { get; set; }

    public bool IsHubVisible => CurrentCard is null;
    public bool ShowCreateSrr => CurrentCard == BeginnerCard.CreateSrr;
    public bool ShowCreateSrs => CurrentCard == BeginnerCard.CreateSrs;
    public bool ShowReconstruct => CurrentCard == BeginnerCard.Reconstruct;
    public bool ShowRestore => CurrentCard == BeginnerCard.Restore;

    public string CurrentTitle => CurrentCard switch
    {
        BeginnerCard.CreateSrr => "Create an SRR",
        BeginnerCard.CreateSrs => "Create a sample SRS",
        BeginnerCard.Reconstruct => "Reconstruct RAR archives",
        BeginnerCard.Restore => "Restore a sample",
        _ => string.Empty,
    };

    [RelayCommand]
    private void OpenCard(BeginnerCard card) => CurrentCard = card;

    [RelayCommand]
    private void BackToHub() => CurrentCard = null;

    [RelayCommand]
    private void OpenInAdvanced(BeginnerCard card) => OpenInAdvancedAction?.Invoke(card);
}
```

> This references `BeginnerRestoreViewModel`, created in Phase 3 Task 12. To build Phase 2 in isolation, do Task 12 (the router) and the `BeginnerRestoreViewModel` skeleton first, OR temporarily change the `Restore` property type to `object` and fix it in Phase 3. Recommended: implement Phase 3 Task 11–12 before wiring `MainWindowViewModel` in Task 10 below. The test in this task does not touch `Restore`, so it passes regardless.

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test ReScene.NET.Tests/ReScene.NET.Tests.csproj --filter BeginnerShellViewModelTests`
Expected: PASS (4 tests). (If `BeginnerRestoreViewModel` is not yet created, temporarily type `Restore` as `object` to compile.)

- [ ] **Step 5: Commit**

```bash
git add ReScene.NET/ViewModels/BeginnerShellViewModel.cs ReScene.NET.Tests/BeginnerShellViewModelTests.cs
git commit -m "feat(beginner): add BeginnerShellViewModel navigation"
```

### Task 9: `BeginnerShellView` (hub + task host)

**Files:**
- Create: `ReScene.NET/Views/BeginnerShellView.xaml`
- Create: `ReScene.NET/Views/BeginnerShellView.xaml.cs`
- Modify: `ReScene.NET/Views/MainWindow.xaml` (swap the placeholder from Task 6 for the real `ContentControl`)

- [ ] **Step 1: Create the code-behind**

`ReScene.NET/Views/BeginnerShellView.xaml.cs`:

```csharp
using System.Windows.Controls;

namespace ReScene.NET.Views;

public partial class BeginnerShellView : UserControl
{
    public BeginnerShellView()
    {
        InitializeComponent();
    }
}
```

- [ ] **Step 2: Create the view**

`ReScene.NET/Views/BeginnerShellView.xaml`. The hub uses a local card button style; the task area swaps four child views by `Show*` bools. Child views are added in Phase 3 — until then, comment out the four `<bv:Beginner*View>` lines so it builds.

```xml
<UserControl x:Class="ReScene.NET.Views.BeginnerShellView"
             xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
             xmlns:vm="clr-namespace:ReScene.NET.ViewModels"
             xmlns:bv="clr-namespace:ReScene.NET.Views.Beginner">
  <UserControl.Resources>
    <Style x:Key="HubCardButton" TargetType="Button">
      <Setter Property="Background" Value="{DynamicResource SurfaceBackground}" />
      <Setter Property="BorderBrush" Value="{DynamicResource BorderSubtle}" />
      <Setter Property="BorderThickness" Value="1" />
      <Setter Property="Padding" Value="16" />
      <Setter Property="Margin" Value="6" />
      <Setter Property="Cursor" Value="Hand" />
      <Setter Property="HorizontalContentAlignment" Value="Left" />
      <Setter Property="Template">
        <Setter.Value>
          <ControlTemplate TargetType="Button">
            <Border Background="{TemplateBinding Background}"
                    BorderBrush="{TemplateBinding BorderBrush}"
                    BorderThickness="{TemplateBinding BorderThickness}"
                    CornerRadius="{DynamicResource LargeRadius}"
                    Padding="{TemplateBinding Padding}">
              <ContentPresenter />
            </Border>
          </ControlTemplate>
        </Setter.Value>
      </Setter>
      <Style.Triggers>
        <Trigger Property="IsMouseOver" Value="True">
          <Setter Property="BorderBrush" Value="{DynamicResource AccentPrimary}" />
          <Setter Property="Background" Value="{DynamicResource HoverBackground}" />
        </Trigger>
      </Style.Triggers>
    </Style>
  </UserControl.Resources>

  <Grid Margin="{DynamicResource PageMargin}">

    <!-- ══ HUB ═══════════════════════════════════════════════════════ -->
    <ScrollViewer VerticalScrollBarVisibility="Auto"
                  Visibility="{Binding IsHubVisible, Converter={StaticResource BoolToVisibility}}">
      <StackPanel MaxWidth="760" HorizontalAlignment="Center" Margin="0,12,0,0">
        <TextBlock Text="What would you like to do?"
                   FontSize="{DynamicResource FontSizeH1}" FontWeight="SemiBold"
                   HorizontalAlignment="Center" Margin="0,0,0,12" />
        <UniformGrid Columns="2">
          <Button Style="{StaticResource HubCardButton}"
                  Command="{Binding OpenCardCommand}" CommandParameter="{x:Static vm:BeginnerCard.CreateSrr}">
            <StackPanel>
              <TextBlock Text="&#x1F4E6;" FontSize="28" Margin="0,0,0,6" />
              <TextBlock Text="Create an SRR" FontSize="{DynamicResource FontSizeH2}" FontWeight="SemiBold" />
              <TextBlock Text="Make a recovery file for a release." TextWrapping="Wrap"
                         Foreground="{DynamicResource ForegroundSecondary}" FontSize="{DynamicResource FontSizeCaption}" Margin="0,2,0,0" />
            </StackPanel>
          </Button>
          <Button Style="{StaticResource HubCardButton}"
                  Command="{Binding OpenCardCommand}" CommandParameter="{x:Static vm:BeginnerCard.CreateSrs}">
            <StackPanel>
              <TextBlock Text="&#x1F3AC;" FontSize="28" Margin="0,0,0,6" />
              <TextBlock Text="Create a sample SRS" FontSize="{DynamicResource FontSizeH2}" FontWeight="SemiBold" />
              <TextBlock Text="Capture a movie sample." TextWrapping="Wrap"
                         Foreground="{DynamicResource ForegroundSecondary}" FontSize="{DynamicResource FontSizeCaption}" Margin="0,2,0,0" />
            </StackPanel>
          </Button>
          <Button Style="{StaticResource HubCardButton}"
                  Command="{Binding OpenCardCommand}" CommandParameter="{x:Static vm:BeginnerCard.Reconstruct}">
            <StackPanel>
              <TextBlock Text="&#x1F527;" FontSize="28" Margin="0,0,0,6" />
              <TextBlock Text="Reconstruct RAR archives" FontSize="{DynamicResource FontSizeH2}" FontWeight="SemiBold" />
              <TextBlock Text="Rebuild the original RARs from an SRR." TextWrapping="Wrap"
                         Foreground="{DynamicResource ForegroundSecondary}" FontSize="{DynamicResource FontSizeCaption}" Margin="0,2,0,0" />
            </StackPanel>
          </Button>
          <Button Style="{StaticResource HubCardButton}"
                  Command="{Binding OpenCardCommand}" CommandParameter="{x:Static vm:BeginnerCard.Restore}">
            <StackPanel>
              <TextBlock Text="&#x21BA;" FontSize="28" Margin="0,0,0,6" />
              <TextBlock Text="Restore a sample" FontSize="{DynamicResource FontSizeH2}" FontWeight="SemiBold" />
              <TextBlock Text="Get a sample back from an SRR or SRS." TextWrapping="Wrap"
                         Foreground="{DynamicResource ForegroundSecondary}" FontSize="{DynamicResource FontSizeCaption}" Margin="0,2,0,0" />
            </StackPanel>
          </Button>
        </UniformGrid>
      </StackPanel>
    </ScrollViewer>

    <!-- ══ TASK AREA ═════════════════════════════════════════════════ -->
    <DockPanel Visibility="{Binding IsHubVisible, Converter={StaticResource InverseBoolToVisibility}}">
      <DockPanel DockPanel.Dock="Top" Margin="0,0,0,8">
        <Button DockPanel.Dock="Left" Content="&#x2039; Tasks"
                Command="{Binding BackToHubCommand}"
                Style="{StaticResource GhostButton}" Padding="8,2" />
        <Button DockPanel.Dock="Right" Content="Open in Advanced"
                Command="{Binding OpenInAdvancedCommand}" CommandParameter="{Binding CurrentCard}"
                Style="{StaticResource GhostButton}" Padding="8,2" />
        <TextBlock Text="{Binding CurrentTitle}" FontWeight="SemiBold"
                   VerticalAlignment="Center" HorizontalAlignment="Center" />
      </DockPanel>
      <Grid>
        <bv:BeginnerCreatorView DataContext="{Binding Creator}"
                                Visibility="{Binding DataContext.ShowCreateSrr, RelativeSource={RelativeSource AncestorType=UserControl}, Converter={StaticResource BoolToVisibility}}" />
        <bv:BeginnerSrsCreatorView DataContext="{Binding SRSCreator}"
                                   Visibility="{Binding DataContext.ShowCreateSrs, RelativeSource={RelativeSource AncestorType=UserControl}, Converter={StaticResource BoolToVisibility}}" />
        <bv:BeginnerReconstructorView DataContext="{Binding Reconstructor}"
                                      Visibility="{Binding DataContext.ShowReconstruct, RelativeSource={RelativeSource AncestorType=UserControl}, Converter={StaticResource BoolToVisibility}}" />
        <bv:BeginnerRestoreView DataContext="{Binding Restore}"
                                Visibility="{Binding DataContext.ShowRestore, RelativeSource={RelativeSource AncestorType=UserControl}, Converter={StaticResource BoolToVisibility}}" />
      </Grid>
    </DockPanel>

  </Grid>
</UserControl>
```

> The four `<bv:Beginner*View>` elements reference views created in Phase 3. Comment them out for now (leave the surrounding `<Grid>`), build the hub, then uncomment each as its view is created in Phase 3. The `RelativeSource AncestorType=UserControl` binding reaches the `BeginnerShellViewModel` (the UserControl's own DataContext) for the `Show*` flags while each child uses its task VM as DataContext.

- [ ] **Step 3: Swap the MainWindow placeholder for the real host**

In `MainWindow.xaml`, replace the Task 6 placeholder `<TextBlock Text="Beginner mode (coming soon)" .../>` with the real `ContentControl` block shown in Phase 1 Task 6 Step 2.

- [ ] **Step 4: Wire `Beginner` onto `MainWindowViewModel`**

See Phase 3 Task 13 for the `MainWindowViewModel` constructor wiring (it depends on `BeginnerRestoreViewModel`). Until then, the `ContentControl` `Content="{Binding Beginner}"` binds to nothing (renders empty) — harmless.

- [ ] **Step 5: Build & run**

Run: `dotnet build ReScene.NET/ReScene.NET.csproj`
Expected: build succeeds (with child views commented out). The hub will render once `Beginner` is wired in Task 13.

- [ ] **Step 6: Commit**

```bash
git add ReScene.NET/Views/BeginnerShellView.xaml ReScene.NET/Views/BeginnerShellView.xaml.cs ReScene.NET/Views/MainWindow.xaml
git commit -m "feat(beginner): add BeginnerShellView hub and task host"
```

---

## Phase 3 — Beginner task views

### Task 10: `SampleRestoreRouter` (pure)

**Files:**
- Create: `ReScene.NET/Helpers/SampleRestoreRouter.cs`
- Test: `ReScene.NET.Tests/SampleRestoreRouterTests.cs`

- [ ] **Step 1: Write the failing test**

`ReScene.NET.Tests/SampleRestoreRouterTests.cs`:

```csharp
using ReScene.NET.Helpers;

namespace ReScene.NET.Tests;

public class SampleRestoreRouterTests
{
    [Theory]
    [InlineData(@"C:\rel\movie.srr", SampleRestoreKind.Srr)]
    [InlineData(@"C:\rel\movie.SRR", SampleRestoreKind.Srr)]
    [InlineData(@"C:\rel\movie.sample.srs", SampleRestoreKind.Srs)]
    [InlineData(@"C:\rel\movie.SRS", SampleRestoreKind.Srs)]
    [InlineData(@"C:\rel\movie.mkv", SampleRestoreKind.Unknown)]
    [InlineData("", SampleRestoreKind.Unknown)]
    [InlineData(null, SampleRestoreKind.Unknown)]
    public void Route_ClassifiesByExtension(string? path, SampleRestoreKind expected)
        => Assert.Equal(expected, SampleRestoreRouter.Route(path));
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test ReScene.NET.Tests/ReScene.NET.Tests.csproj --filter SampleRestoreRouterTests`
Expected: FAIL — `SampleRestoreRouter` does not exist.

- [ ] **Step 3: Implement the router**

`ReScene.NET/Helpers/SampleRestoreRouter.cs`:

```csharp
namespace ReScene.NET.Helpers;

/// <summary>Which restore flow an input file maps to.</summary>
public enum SampleRestoreKind
{
    Unknown,
    Srr,
    Srs,
}

/// <summary>
/// Routes a chosen file to the right Beginner restore flow: an .srr triggers bulk restore of
/// every embedded sample; a standalone .srs triggers a single sample rebuild.
/// </summary>
public static class SampleRestoreRouter
{
    public static SampleRestoreKind Route(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return SampleRestoreKind.Unknown;
        }

        string ext = Path.GetExtension(path);
        if (ext.Equals(".srr", StringComparison.OrdinalIgnoreCase))
        {
            return SampleRestoreKind.Srr;
        }

        if (ext.Equals(".srs", StringComparison.OrdinalIgnoreCase))
        {
            return SampleRestoreKind.Srs;
        }

        return SampleRestoreKind.Unknown;
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test ReScene.NET.Tests/ReScene.NET.Tests.csproj --filter SampleRestoreRouterTests`
Expected: PASS (7 cases).

- [ ] **Step 5: Commit**

```bash
git add ReScene.NET/Helpers/SampleRestoreRouter.cs ReScene.NET.Tests/SampleRestoreRouterTests.cs
git commit -m "feat(beginner): add SampleRestoreRouter"
```

### Task 11: Auto-select first ISO media file in `SRSCreatorViewModel`

So the Beginner SRS screen is usable for ISO input without the user touching the ISO combo.

**Files:**
- Modify: `ReScene.NET/ViewModels/SRSCreatorViewModel.cs`

- [ ] **Step 1: Subscribe to the ISO collection in the constructor**

In the `SRSCreatorViewModel` constructor body, add (the `ISOMediaFiles` collection and `SelectedISOMediaFile` property already exist):

```csharp
        ISOMediaFiles.CollectionChanged += (_, _) =>
        {
            if (SelectedISOMediaFile is null && ISOMediaFiles.Count > 0)
            {
                SelectedISOMediaFile = ISOMediaFiles[0];
            }
        };
```

Ensure `using System.Collections.Specialized;` is not required (the lambda ignores the event args, so no extra using is needed).

- [ ] **Step 2: Build**

Run: `dotnet build ReScene.NET/ReScene.NET.csproj`
Expected: build succeeds.

- [ ] **Step 3: Commit**

```bash
git add ReScene.NET/ViewModels/SRSCreatorViewModel.cs
git commit -m "feat(beginner): auto-select first ISO media file in SRS Creator"
```

### Task 12: `BeginnerRestoreViewModel` (smart routing)

**Files:**
- Create: `ReScene.NET/ViewModels/BeginnerRestoreViewModel.cs`
- Test: `ReScene.NET.Tests/BeginnerRestoreViewModelTests.cs`

- [ ] **Step 1: Write the failing test**

`ReScene.NET.Tests/BeginnerRestoreViewModelTests.cs`:

```csharp
using ReScene.NET.Helpers;
using ReScene.NET.Models;
using ReScene.NET.ViewModels;

namespace ReScene.NET.Tests;

public class BeginnerRestoreViewModelTests
{
    [Fact]
    public void NewVm_HasNoFlowSelected()
    {
        var vm = new BeginnerRestoreViewModel(fileDialog: null!);
        Assert.Equal(SampleRestoreKind.Unknown, vm.Kind);
        Assert.False(vm.ShowFlow);
        Assert.False(vm.IsBulk);
        Assert.False(vm.IsSingle);
    }

    [Fact]
    public void SettingSrrInput_SelectsBulkFlow()
    {
        var vm = new BeginnerRestoreViewModel(fileDialog: null!) { InputPath = @"C:\rel\movie.srr" };
        Assert.Equal(SampleRestoreKind.Srr, vm.Kind);
        Assert.True(vm.IsBulk);
        Assert.False(vm.IsSingle);
        Assert.True(vm.ShowFlow);
        Assert.Equal(FieldState.Ok, vm.InputStatus.State);
    }

    [Fact]
    public void SettingSrsInput_SelectsSingleFlow()
    {
        var vm = new BeginnerRestoreViewModel(fileDialog: null!) { InputPath = @"C:\rel\movie.srs" };
        Assert.Equal(SampleRestoreKind.Srs, vm.Kind);
        Assert.True(vm.IsSingle);
        Assert.False(vm.IsBulk);
    }

    [Fact]
    public void SettingUnknownInput_WarnsAndHidesFlow()
    {
        var vm = new BeginnerRestoreViewModel(fileDialog: null!) { InputPath = @"C:\rel\movie.mkv" };
        Assert.Equal(SampleRestoreKind.Unknown, vm.Kind);
        Assert.False(vm.ShowFlow);
        Assert.Equal(FieldState.Warning, vm.InputStatus.State);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test ReScene.NET.Tests/ReScene.NET.Tests.csproj --filter BeginnerRestoreViewModelTests`
Expected: FAIL — `BeginnerRestoreViewModel` does not exist.

- [ ] **Step 3: Implement the ViewModel**

`ReScene.NET/ViewModels/BeginnerRestoreViewModel.cs`:

```csharp
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ReScene.NET.Helpers;
using ReScene.NET.Models;
using ReScene.NET.Services;

namespace ReScene.NET.ViewModels;

/// <summary>
/// Beginner "Restore a sample" flow. One input file is routed by extension: an .srr drives the
/// bulk <see cref="SampleRestorerViewModel"/>; a standalone .srs drives the single
/// <see cref="SRSReconstructorViewModel"/>.
/// </summary>
public partial class BeginnerRestoreViewModel : ViewModelBase
{
    private readonly IFileDialogService _fileDialog;

    // Shared sub-flow ViewModels, assigned by MainWindowViewModel via object initializer.
    public SampleRestorerViewModel BulkRestorer { get; set; } = null!;
    public SRSReconstructorViewModel SingleRebuilder { get; set; } = null!;

    public BeginnerRestoreViewModel(IFileDialogService fileDialog)
    {
        _fileDialog = fileDialog;
    }

    [ObservableProperty]
    public partial string InputPath { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsBulk))]
    [NotifyPropertyChangedFor(nameof(IsSingle))]
    [NotifyPropertyChangedFor(nameof(ShowFlow))]
    public partial SampleRestoreKind Kind { get; set; }

    [ObservableProperty]
    public partial FieldStatus InputStatus { get; set; } = FieldStatus.None;

    public bool IsBulk => Kind == SampleRestoreKind.Srr;
    public bool IsSingle => Kind == SampleRestoreKind.Srs;
    public bool ShowFlow => Kind != SampleRestoreKind.Unknown;

    partial void OnInputPathChanged(string value)
    {
        Kind = SampleRestoreRouter.Route(value);

        switch (Kind)
        {
            case SampleRestoreKind.Srr:
                if (BulkRestorer is not null) { BulkRestorer.SRRFilePath = value; }
                InputStatus = FieldStatus.Ok("SRR — will restore every embedded sample.");
                break;
            case SampleRestoreKind.Srs:
                if (SingleRebuilder is not null) { SingleRebuilder.SRSFilePath = value; }
                InputStatus = FieldStatus.Ok("SRS — will rebuild this one sample.");
                break;
            default:
                InputStatus = string.IsNullOrWhiteSpace(value)
                    ? FieldStatus.None
                    : FieldStatus.Warning("Pick an .srr (whole release) or an .srs (single sample) file.");
                break;
        }
    }

    [RelayCommand]
    private async Task BrowseInput()
    {
        string? path = await _fileDialog.OpenFileAsync(
            "Select an SRR or SRS file", "ReScene files (*.srr;*.srs)|*.srr;*.srs|All files (*.*)|*.*");
        if (path is not null)
        {
            InputPath = path;
        }
    }
}
```

> Verify the `FieldStatus.Ok/Warning/None` factory names against `ReScene.NET/Models/FieldStatus.cs` (the memory notes `.None/.Ok/.Info/.Warning/.Error`). Verify `IFileDialogService.OpenFileAsync(string title, string filter)` signature against `ReScene.NET/Services/IFileDialogService.cs`; adjust the filter argument if the signature differs (the Creator uses `FileDialogFilters.*` constants — reuse one if a suitable SRR/SRS filter exists, otherwise the inline filter string above is fine).

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test ReScene.NET.Tests/ReScene.NET.Tests.csproj --filter BeginnerRestoreViewModelTests`
Expected: PASS (4 tests).

- [ ] **Step 5: Commit**

```bash
git add ReScene.NET/ViewModels/BeginnerRestoreViewModel.cs ReScene.NET.Tests/BeginnerRestoreViewModelTests.cs
git commit -m "feat(beginner): add BeginnerRestoreViewModel smart routing"
```

### Task 13: Wire the Beginner shell into `MainWindowViewModel`

**Files:**
- Modify: `ReScene.NET/ViewModels/MainWindowViewModel.cs`

- [ ] **Step 1: Add the `Beginner` property**

Add to the class, next to the other child-VM properties:

```csharp
    public BeginnerShellViewModel Beginner
    {
        get;
    }
```

- [ ] **Step 2: Construct it in the full constructor**

In the parameterful constructor, after `FileCompare = new FileCompareViewModel(...);` and before the `Home = new HomeViewModel(...)` line (so all task VMs exist), add:

```csharp
        var beginnerRestore = new BeginnerRestoreViewModel(fileDialog)
        {
            BulkRestorer = SampleRestorer,
            SingleRebuilder = SRSReconstructor,
        };
        Beginner = new BeginnerShellViewModel
        {
            Creator = Creator,
            SRSCreator = SRSCreator,
            Reconstructor = Reconstructor,
            Restore = beginnerRestore,
            OpenInAdvancedAction = OpenCardInAdvanced,
        };
```

- [ ] **Step 3: Add the card → tab mapping**

Add this method to the class:

```csharp
    private void OpenCardInAdvanced(BeginnerCard card)
    {
        Mode = UserMode.Advanced;
        SelectedTabIndex = card switch
        {
            BeginnerCard.CreateSrr => 2,
            BeginnerCard.CreateSrs => 3,
            BeginnerCard.Reconstruct => 4,
            BeginnerCard.Restore => 6,
            _ => SelectedTabIndex,
        };
    }
```

- [ ] **Step 4: Build**

Run: `dotnet build ReScene.NET/ReScene.NET.csproj`
Expected: build succeeds.

- [ ] **Step 5: Run all tests**

Run: `dotnet test ReScene.NET.Tests/ReScene.NET.Tests.csproj`
Expected: PASS (existing + all new tests).

- [ ] **Step 6: Commit**

```bash
git add ReScene.NET/ViewModels/MainWindowViewModel.cs
git commit -m "feat(beginner): wire Beginner shell into MainWindowViewModel"
```

### Task 14: `BeginnerCreatorView`

**Files:**
- Create: `ReScene.NET/Views/Beginner/BeginnerCreatorView.xaml`
- Create: `ReScene.NET/Views/Beginner/BeginnerCreatorView.xaml.cs`
- Modify: `ReScene.NET/Views/BeginnerShellView.xaml` (uncomment the `<bv:BeginnerCreatorView>` line)

- [ ] **Step 1: Code-behind**

`ReScene.NET/Views/Beginner/BeginnerCreatorView.xaml.cs`:

```csharp
using System.Windows.Controls;

namespace ReScene.NET.Views.Beginner;

public partial class BeginnerCreatorView : UserControl
{
    public BeginnerCreatorView()
    {
        InitializeComponent();
    }
}
```

- [ ] **Step 2: View (binds to `CreatorViewModel`)**

`ReScene.NET/Views/Beginner/BeginnerCreatorView.xaml`:

```xml
<UserControl x:Class="ReScene.NET.Views.Beginner.BeginnerCreatorView"
             xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
             xmlns:c="clr-namespace:ReScene.NET.Controls">
  <ScrollViewer VerticalScrollBarVisibility="Auto">
    <StackPanel MaxWidth="720" HorizontalAlignment="Left">

      <TextBlock Text="Point at a release's .sfv or first .rar — everything else uses sensible defaults."
                 Foreground="{DynamicResource ForegroundSecondary}" FontSize="{DynamicResource FontSizeCaption}"
                 TextWrapping="Wrap" Margin="0,0,0,12" />

      <TextBlock Text="Release .sfv or first .rar" FontWeight="SemiBold" Margin="0,0,0,2" />
      <DockPanel Margin="0,0,0,2">
        <Button DockPanel.Dock="Right" Content="Browse" Command="{Binding BrowseInputCommand}"
                Style="{StaticResource GhostButton}" Margin="4,0,0,0" MinWidth="75" />
        <TextBox Text="{Binding InputPath}" />
      </DockPanel>
      <c:FieldStatusLine Status="{Binding InputStatus}" />

      <TextBlock Text="Save SRR to" FontWeight="SemiBold" Margin="0,12,0,2" />
      <DockPanel Margin="0,0,0,2">
        <Button DockPanel.Dock="Right" Content="Browse" Command="{Binding BrowseOutputCommand}"
                Style="{StaticResource GhostButton}" Margin="4,0,0,0" MinWidth="75" />
        <TextBox Text="{Binding OutputPath}" />
      </DockPanel>
      <c:FieldStatusLine Status="{Binding OutputStatus}" />

      <StackPanel Orientation="Horizontal" Margin="0,16,0,0">
        <Button Content="Create SRR" Command="{Binding CreateSRRCommand}"
                Style="{StaticResource PrimaryButton}" Padding="18,6" Margin="0,0,8,0" />
        <Button Content="Cancel" Command="{Binding CancelCreationCommand}"
                Style="{StaticResource CancelButton}" Padding="18,6"
                Visibility="{Binding IsCreating, Converter={StaticResource BoolToVisibility}}" />
      </StackPanel>

      <ProgressBar Value="{Binding ProgressPercent}" Maximum="100" Height="6" Margin="0,12,0,0"
                   Visibility="{Binding ShowProgress, Converter={StaticResource BoolToVisibility}}" />
      <TextBlock Text="{Binding ProgressMessage}" Foreground="{DynamicResource ForegroundSecondary}"
                 FontSize="{DynamicResource FontSizeCaption}" Margin="0,4,0,0"
                 Visibility="{Binding ShowProgress, Converter={StaticResource BoolToVisibility}}" />

      <TextBlock Text="Stored files, SRS samples, vobsub, OSO hashes and languages.diz are handled automatically."
                 Foreground="{DynamicResource ForegroundSecondary}" FontSize="{DynamicResource FontSizeCaption}"
                 TextWrapping="Wrap" Margin="0,16,0,0" />
    </StackPanel>
  </ScrollViewer>
</UserControl>
```

- [ ] **Step 3: Uncomment in the hub**

In `BeginnerShellView.xaml`, uncomment the `<bv:BeginnerCreatorView ... />` element.

- [ ] **Step 4: Build & run**

Run: `dotnet build ReScene.NET/ReScene.NET.csproj`
Expected: build succeeds. In Beginner mode the hub shows; clicking **Create an SRR** opens this screen; `‹ Tasks` returns; **Open in Advanced** jumps to the SRR Creator tab in Advanced mode.

- [ ] **Step 5: Commit**

```bash
git add ReScene.NET/Views/Beginner/BeginnerCreatorView.xaml ReScene.NET/Views/Beginner/BeginnerCreatorView.xaml.cs ReScene.NET/Views/BeginnerShellView.xaml
git commit -m "feat(beginner): add simplified Create SRR screen"
```

### Task 15: `BeginnerSrsCreatorView`

**Files:**
- Create: `ReScene.NET/Views/Beginner/BeginnerSrsCreatorView.xaml(.cs)`
- Modify: `ReScene.NET/Views/BeginnerShellView.xaml` (uncomment its line)

- [ ] **Step 1: Code-behind** — same shape as Task 14 Step 1 with class `BeginnerSrsCreatorView`.

```csharp
using System.Windows.Controls;

namespace ReScene.NET.Views.Beginner;

public partial class BeginnerSrsCreatorView : UserControl
{
    public BeginnerSrsCreatorView()
    {
        InitializeComponent();
    }
}
```

- [ ] **Step 2: View (binds to `SRSCreatorViewModel`)**

`ReScene.NET/Views/Beginner/BeginnerSrsCreatorView.xaml`:

```xml
<UserControl x:Class="ReScene.NET.Views.Beginner.BeginnerSrsCreatorView"
             xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
             xmlns:c="clr-namespace:ReScene.NET.Controls">
  <ScrollViewer VerticalScrollBarVisibility="Auto">
    <StackPanel MaxWidth="720" HorizontalAlignment="Left">

      <TextBlock Text="Pick a sample video (or an ISO) — the SRS captures what's needed to rebuild it later."
                 Foreground="{DynamicResource ForegroundSecondary}" FontSize="{DynamicResource FontSizeCaption}"
                 TextWrapping="Wrap" Margin="0,0,0,12" />

      <TextBlock Text="Sample file" FontWeight="SemiBold" Margin="0,0,0,2" />
      <DockPanel Margin="0,0,0,2">
        <Button DockPanel.Dock="Right" Content="Browse" Command="{Binding BrowseInputCommand}"
                Style="{StaticResource GhostButton}" Margin="4,0,0,0" MinWidth="75" />
        <TextBox Text="{Binding InputPath}" />
      </DockPanel>
      <c:FieldStatusLine Status="{Binding SampleStatus}" />

      <DockPanel Margin="0,8,0,0"
                 Visibility="{Binding ShowISOSelection, Converter={StaticResource BoolToVisibility}}">
        <TextBlock Text="File inside ISO:" VerticalAlignment="Center" Margin="0,0,8,0" />
        <ComboBox ItemsSource="{Binding ISOMediaFiles}" SelectedItem="{Binding SelectedISOMediaFile}"
                  FontFamily="{DynamicResource MonoFontFamily}" FontSize="{DynamicResource FontSizeCaption}" />
      </DockPanel>

      <TextBlock Text="Save SRS to" FontWeight="SemiBold" Margin="0,12,0,2" />
      <DockPanel Margin="0,0,0,2">
        <Button DockPanel.Dock="Right" Content="Browse" Command="{Binding BrowseOutputCommand}"
                Style="{StaticResource GhostButton}" Margin="4,0,0,0" MinWidth="75" />
        <TextBox Text="{Binding OutputPath}" />
      </DockPanel>
      <c:FieldStatusLine Status="{Binding OutputStatus}" />

      <StackPanel Orientation="Horizontal" Margin="0,16,0,0">
        <Button Content="Create SRS" Command="{Binding CreateSRSCommand}"
                Style="{StaticResource PrimaryButton}" Padding="18,6" Margin="0,0,8,0" />
        <Button Content="Cancel" Command="{Binding CancelCreationCommand}"
                Style="{StaticResource CancelButton}" Padding="18,6"
                Visibility="{Binding IsCreating, Converter={StaticResource BoolToVisibility}}" />
      </StackPanel>

      <ProgressBar Value="{Binding ProgressPercent}" Maximum="100" Height="6" Margin="0,12,0,0"
                   Visibility="{Binding ShowProgress, Converter={StaticResource BoolToVisibility}}" />
      <TextBlock Text="{Binding ProgressMessage}" Foreground="{DynamicResource ForegroundSecondary}"
                 FontSize="{DynamicResource FontSizeCaption}" Margin="0,4,0,0"
                 Visibility="{Binding ShowProgress, Converter={StaticResource BoolToVisibility}}" />
    </StackPanel>
  </ScrollViewer>
</UserControl>
```

- [ ] **Step 3: Uncomment** the `<bv:BeginnerSrsCreatorView .../>` line in `BeginnerShellView.xaml`.

- [ ] **Step 4: Build & run** — Run: `dotnet build ReScene.NET/ReScene.NET.csproj`. Expected: build succeeds; the Create-SRS card opens this screen.

- [ ] **Step 5: Commit**

```bash
git add ReScene.NET/Views/Beginner/BeginnerSrsCreatorView.xaml ReScene.NET/Views/Beginner/BeginnerSrsCreatorView.xaml.cs ReScene.NET/Views/BeginnerShellView.xaml
git commit -m "feat(beginner): add simplified Create SRS screen"
```

### Task 16: `BeginnerReconstructorView` (guided import)

Reuses the existing `ImportSRRCommand` (auto-configures all 50+ options from the SRR), `StartCommand`, `StopCommand`, and the custom-packer warning. Beginner sees only: WinRAR folder, Import-from-SRR, Release folder, Output folder, Start.

**Files:**
- Create: `ReScene.NET/Views/Beginner/BeginnerReconstructorView.xaml(.cs)`
- Modify: `ReScene.NET/Views/BeginnerShellView.xaml` (uncomment its line)

- [ ] **Step 1: Code-behind** (class `BeginnerReconstructorView`):

```csharp
using System.Windows.Controls;

namespace ReScene.NET.Views.Beginner;

public partial class BeginnerReconstructorView : UserControl
{
    public BeginnerReconstructorView()
    {
        InitializeComponent();
    }
}
```

- [ ] **Step 2: View (binds to `ReconstructorViewModel`)**

`ReScene.NET/Views/Beginner/BeginnerReconstructorView.xaml`:

```xml
<UserControl x:Class="ReScene.NET.Views.Beginner.BeginnerReconstructorView"
             xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
             xmlns:c="clr-namespace:ReScene.NET.Controls">
  <ScrollViewer VerticalScrollBarVisibility="Auto">
    <StackPanel MaxWidth="720" HorizontalAlignment="Left">

      <TextBlock Text="Step 1: import the SRR (this auto-configures everything). Then point at your extracted files and an output folder."
                 Foreground="{DynamicResource ForegroundSecondary}" FontSize="{DynamicResource FontSizeCaption}"
                 TextWrapping="Wrap" Margin="0,0,0,12" />

      <Button Content="Import from SRR..." Command="{Binding ImportSRRCommand}"
              Style="{StaticResource PrimaryButton}" Padding="14,6" HorizontalAlignment="Left"
              ToolTip="Pick the release's .srr file. Compression, version, dictionary and timestamps are configured automatically." />

      <Border Visibility="{Binding HasCustomPackerWarning, Converter={StaticResource BoolToVisibility}}"
              Background="#3DE0A030" BorderBrush="#E0A030" BorderThickness="1"
              CornerRadius="3" Margin="0,8,0,0" Padding="8,5">
        <TextBlock Text="{Binding CustomPackerWarning}" Foreground="#FFD080"
                   FontSize="{DynamicResource FontSizeBody}" TextWrapping="Wrap" />
      </Border>

      <TextBlock Text="WinRAR versions folder" FontWeight="SemiBold" Margin="0,14,0,2" />
      <DockPanel Margin="0,0,0,2">
        <Button DockPanel.Dock="Right" Content="Browse" Command="{Binding BrowseWinRarCommand}"
                Style="{StaticResource GhostButton}" Margin="4,0,0,0" MinWidth="75" />
        <TextBox Text="{Binding WinRarPath}" />
      </DockPanel>
      <c:FieldStatusLine Status="{Binding WinRarStatus}" />

      <TextBlock Text="Extracted release files" FontWeight="SemiBold" Margin="0,12,0,2" />
      <DockPanel Margin="0,0,0,2">
        <Button DockPanel.Dock="Right" Content="Browse" Command="{Binding BrowseReleaseCommand}"
                Style="{StaticResource GhostButton}" Margin="4,0,0,0" MinWidth="75" />
        <TextBox Text="{Binding ReleasePath}" />
      </DockPanel>
      <c:FieldStatusLine Status="{Binding ReleaseStatus}" />

      <TextBlock Text="Output folder" FontWeight="SemiBold" Margin="0,12,0,2" />
      <DockPanel Margin="0,0,0,2">
        <Button DockPanel.Dock="Right" Content="Browse" Command="{Binding BrowseOutputCommand}"
                Style="{StaticResource GhostButton}" Margin="4,0,0,0" MinWidth="75" />
        <TextBox Text="{Binding OutputPath}" />
      </DockPanel>

      <StackPanel Orientation="Horizontal" Margin="0,16,0,0">
        <Button Content="Start" Command="{Binding StartCommand}"
                Style="{StaticResource PrimaryButton}" Padding="18,6" Margin="0,0,8,0" />
        <Button Content="Stop" Command="{Binding StopCommand}"
                Style="{StaticResource CancelButton}" Padding="18,6"
                Visibility="{Binding IsRunning, Converter={StaticResource BoolToVisibility}}" />
      </StackPanel>

      <ProgressBar Value="{Binding ProgressPercent}" Maximum="100" Height="6" Margin="0,12,0,0"
                   Visibility="{Binding ShowProgress, Converter={StaticResource BoolToVisibility}}" />
      <TextBlock Text="{Binding ProgressMessage}" Foreground="{DynamicResource ForegroundSecondary}"
                 FontSize="{DynamicResource FontSizeCaption}" Margin="0,4,0,0"
                 Visibility="{Binding ShowProgress, Converter={StaticResource BoolToVisibility}}" />
    </StackPanel>
  </ScrollViewer>
</UserControl>
```

- [ ] **Step 3: Uncomment** the `<bv:BeginnerReconstructorView .../>` line in `BeginnerShellView.xaml`.

- [ ] **Step 4: Build & run** — Run: `dotnet build ReScene.NET/ReScene.NET.csproj`. Expected: build succeeds; Reconstruct card shows the import-driven screen; a custom-packer SRR shows the warning.

- [ ] **Step 5: Commit**

```bash
git add ReScene.NET/Views/Beginner/BeginnerReconstructorView.xaml ReScene.NET/Views/Beginner/BeginnerReconstructorView.xaml.cs ReScene.NET/Views/BeginnerShellView.xaml
git commit -m "feat(beginner): add guided Reconstruct RAR screen"
```

### Task 17: `BeginnerRestoreView` + bulk/single bodies

A single file picker routes (via `BeginnerRestoreViewModel`) to either the bulk SRR body or the single SRS body.

**Files:**
- Create: `ReScene.NET/Views/Beginner/BeginnerRestoreView.xaml(.cs)`
- Create: `ReScene.NET/Views/Beginner/BeginnerRestoreBulkView.xaml(.cs)`
- Create: `ReScene.NET/Views/Beginner/BeginnerRestoreSingleView.xaml(.cs)`
- Modify: `ReScene.NET/Views/BeginnerShellView.xaml` (uncomment its line)

- [ ] **Step 1: Code-behind (all three)** — identical shape, classes `BeginnerRestoreView`, `BeginnerRestoreBulkView`, `BeginnerRestoreSingleView`, namespace `ReScene.NET.Views.Beginner`, each `InitializeComponent()` in the constructor. Example:

```csharp
using System.Windows.Controls;

namespace ReScene.NET.Views.Beginner;

public partial class BeginnerRestoreView : UserControl
{
    public BeginnerRestoreView()
    {
        InitializeComponent();
    }
}
```

- [ ] **Step 2: `BeginnerRestoreView.xaml` (binds to `BeginnerRestoreViewModel`)**

```xml
<UserControl x:Class="ReScene.NET.Views.Beginner.BeginnerRestoreView"
             xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
             xmlns:c="clr-namespace:ReScene.NET.Controls"
             xmlns:bv="clr-namespace:ReScene.NET.Views.Beginner">
  <ScrollViewer VerticalScrollBarVisibility="Auto">
    <StackPanel MaxWidth="760" HorizontalAlignment="Left">

      <TextBlock Text="Pick the .srr (whole release) or .srs (single sample) you have — the right tool is chosen for you."
                 Foreground="{DynamicResource ForegroundSecondary}" FontSize="{DynamicResource FontSizeCaption}"
                 TextWrapping="Wrap" Margin="0,0,0,12" />

      <TextBlock Text="SRR or SRS file" FontWeight="SemiBold" Margin="0,0,0,2" />
      <DockPanel Margin="0,0,0,2">
        <Button DockPanel.Dock="Right" Content="Browse" Command="{Binding BrowseInputCommand}"
                Style="{StaticResource GhostButton}" Margin="4,0,0,0" MinWidth="75" />
        <TextBox Text="{Binding InputPath}" />
      </DockPanel>
      <c:FieldStatusLine Status="{Binding InputStatus}" />

      <ContentControl Margin="0,14,0,0"
                      Visibility="{Binding ShowFlow, Converter={StaticResource BoolToVisibility}}">
        <Grid>
          <bv:BeginnerRestoreBulkView DataContext="{Binding BulkRestorer}"
                                      Visibility="{Binding DataContext.IsBulk, RelativeSource={RelativeSource AncestorType=UserControl}, Converter={StaticResource BoolToVisibility}}" />
          <bv:BeginnerRestoreSingleView DataContext="{Binding SingleRebuilder}"
                                        Visibility="{Binding DataContext.IsSingle, RelativeSource={RelativeSource AncestorType=UserControl}, Converter={StaticResource BoolToVisibility}}" />
        </Grid>
      </ContentControl>
    </StackPanel>
  </ScrollViewer>
</UserControl>
```

- [ ] **Step 3: `BeginnerRestoreBulkView.xaml` (binds to `SampleRestorerViewModel`)**

```xml
<UserControl x:Class="ReScene.NET.Views.Beginner.BeginnerRestoreBulkView"
             xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
             xmlns:c="clr-namespace:ReScene.NET.Controls">
  <StackPanel>
    <c:FieldStatusLine Status="{Binding SRRStatus}" />

    <TextBlock Text="Folder with the full media files" FontWeight="SemiBold" Margin="0,8,0,2" />
    <DockPanel Margin="0,0,0,2">
      <Button DockPanel.Dock="Right" Content="Browse" Command="{Binding BrowseMediaDirectoryCommand}"
              Style="{StaticResource GhostButton}" Margin="4,0,0,0" MinWidth="75" />
      <TextBox Text="{Binding MediaDirectoryPath}" />
    </DockPanel>
    <c:FieldStatusLine Status="{Binding MatchStatus}" />

    <TextBlock Text="Save restored samples to" FontWeight="SemiBold" Margin="0,12,0,2" />
    <DockPanel Margin="0,0,0,2">
      <Button DockPanel.Dock="Right" Content="Browse" Command="{Binding BrowseOutputDirectoryCommand}"
              Style="{StaticResource GhostButton}" Margin="4,0,0,0" MinWidth="75" />
      <TextBox Text="{Binding OutputDirectoryPath}" />
    </DockPanel>

    <TextBlock Text="Samples found" FontWeight="SemiBold" Margin="0,12,0,2" />
    <DataGrid ItemsSource="{Binding SRSEntries}" AutoGenerateColumns="False" CanUserAddRows="False"
              CanUserReorderColumns="False" CanUserSortColumns="False" GridLinesVisibility="Horizontal"
              HeadersVisibility="Column" SelectionMode="Single" BorderThickness="0"
              MinHeight="80" MaxHeight="220" IsReadOnly="True">
      <DataGrid.Columns>
        <DataGridTextColumn Header="Sample" Binding="{Binding SampleFileName}" Width="*" />
        <DataGridTextColumn Header="Status" Binding="{Binding Status}" Width="200" />
      </DataGrid.Columns>
    </DataGrid>

    <StackPanel Orientation="Horizontal" Margin="0,14,0,0">
      <Button Content="Restore All" Command="{Binding RestoreCommand}"
              Style="{StaticResource PrimaryButton}" Padding="18,6" Margin="0,0,8,0" />
      <Button Content="Cancel" Command="{Binding CancelRestoreCommand}"
              Style="{StaticResource CancelButton}" Padding="18,6"
              Visibility="{Binding IsRestoring, Converter={StaticResource BoolToVisibility}}" />
    </StackPanel>
    <TextBlock Text="{Binding OverallProgressText}" Foreground="{DynamicResource ForegroundSecondary}"
               FontSize="{DynamicResource FontSizeCaption}" Margin="0,6,0,0" />
  </StackPanel>
</UserControl>
```

- [ ] **Step 4: `BeginnerRestoreSingleView.xaml` (binds to `SRSReconstructorViewModel`)**

```xml
<UserControl x:Class="ReScene.NET.Views.Beginner.BeginnerRestoreSingleView"
             xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
             xmlns:c="clr-namespace:ReScene.NET.Controls">
  <StackPanel>
    <c:FieldStatusLine Status="{Binding SRSStatus}" />

    <TextBlock Text="Full original media file" FontWeight="SemiBold" Margin="0,8,0,2" />
    <DockPanel Margin="0,0,0,2">
      <Button DockPanel.Dock="Right" Content="Browse" Command="{Binding BrowseMediaCommand}"
              Style="{StaticResource GhostButton}" Margin="4,0,0,0" MinWidth="75" />
      <TextBox Text="{Binding MediaFilePath}" IsReadOnly="{Binding IsISOSource}" />
    </DockPanel>
    <c:FieldStatusLine Status="{Binding MediaStatus}" />

    <TextBlock Text="Save rebuilt sample to" FontWeight="SemiBold" Margin="0,12,0,2" />
    <DockPanel Margin="0,0,0,2">
      <Button DockPanel.Dock="Right" Content="Browse" Command="{Binding BrowseOutputCommand}"
              Style="{StaticResource GhostButton}" Margin="4,0,0,0" MinWidth="75" />
      <TextBox Text="{Binding OutputPath}" />
    </DockPanel>
    <c:FieldStatusLine Status="{Binding OutputStatus}" />

    <Button Content="Rebuild Sample" Command="{Binding RebuildCommand}"
            Style="{StaticResource PrimaryButton}" Padding="18,6" HorizontalAlignment="Left" Margin="0,14,0,0" />

    <Border Visibility="{Binding ShowResult, Converter={StaticResource BoolToVisibility}}"
            Padding="8,6" Margin="0,12,0,0" CornerRadius="4">
      <Border.Style>
        <Style TargetType="Border">
          <Setter Property="Background" Value="#30FF4444" />
          <Style.Triggers>
            <DataTrigger Binding="{Binding ResultSuccess}" Value="True">
              <Setter Property="Background" Value="#304EC9B0" />
            </DataTrigger>
          </Style.Triggers>
        </Style>
      </Border.Style>
      <TextBlock Text="{Binding ResultSummary}" FontWeight="SemiBold" TextWrapping="Wrap" />
    </Border>
  </StackPanel>
</UserControl>
```

- [ ] **Step 5: Uncomment** the `<bv:BeginnerRestoreView .../>` line in `BeginnerShellView.xaml`.

- [ ] **Step 6: Build & run** — Run: `dotnet build ReScene.NET/ReScene.NET.csproj`. Expected: build succeeds. Restore card: choosing a `.srr` shows the bulk body (grid + Restore All); choosing a `.srs` shows the single body (media + Rebuild). An unrelated file shows the amber hint and no body.

- [ ] **Step 7: Commit**

```bash
git add ReScene.NET/Views/Beginner/BeginnerRestoreView.xaml ReScene.NET/Views/Beginner/BeginnerRestoreView.xaml.cs ReScene.NET/Views/Beginner/BeginnerRestoreBulkView.xaml ReScene.NET/Views/Beginner/BeginnerRestoreBulkView.xaml.cs ReScene.NET/Views/Beginner/BeginnerRestoreSingleView.xaml ReScene.NET/Views/Beginner/BeginnerRestoreSingleView.xaml.cs ReScene.NET/Views/BeginnerShellView.xaml
git commit -m "feat(beginner): add smart Restore screen with bulk/single routing"
```

---

## Phase 4 — Settings mirror & finishing

### Task 18: Mode control in the Settings dialog

The data round-trips already (Phase 0 Task 3); this adds the visible control so users can switch mode there too.

**Files:**
- Modify: `ReScene.NET/Views/SettingsWindow.xaml`

- [ ] **Step 1: Make room for a 4th row & enlarge the window**

In `SettingsWindow.xaml`, change `Height="240"` on the `<Window>` to `Height="290"`, and add a 4th `<RowDefinition Height="Auto" />` to the inner `<Grid.RowDefinitions>` (it currently has three).

- [ ] **Step 2: Add the Mode row**

Inside the `<Grid>`, after the "Recent files limit" row (Row 2), add:

```xml
      <TextBlock Grid.Row="3" Grid.Column="0"
                 Text="Mode:"
                 VerticalAlignment="Center"
                 Margin="0,8,8,4" />
      <StackPanel Grid.Row="3" Grid.Column="1" Grid.ColumnSpan="2"
                  Orientation="Horizontal" Margin="0,8,0,4">
        <RadioButton Content="Beginner" GroupName="UiMode"
                     IsChecked="{Binding IsBeginnerMode}" Margin="0,0,16,0" VerticalAlignment="Center" />
        <RadioButton Content="Advanced" GroupName="UiMode"
                     IsChecked="{Binding IsAdvancedMode}" VerticalAlignment="Center" />
      </StackPanel>
```

- [ ] **Step 3: Build & run** — Run: `dotnet build ReScene.NET/ReScene.NET.csproj`. Open Help → Settings: the Mode radios reflect the current mode; changing + Save flips the shell live (via `AppSettingsService.Changed` → `MainWindowViewModel.OnSettingsChanged`).

- [ ] **Step 4: Commit**

```bash
git add ReScene.NET/Views/SettingsWindow.xaml
git commit -m "feat(mode): add Mode selector to the Settings dialog"
```

### Task 19: Full verification & manual smoke

- [ ] **Step 1: Run the full test suite**

Run: `dotnet test ReScene.NET.Tests/ReScene.NET.Tests.csproj`
Expected: PASS — all existing tests plus the new `AppSettingsModeTests`, `SampleRestoreRouterTests`, `BeginnerShellViewModelTests`, `BeginnerRestoreViewModelTests`.

- [ ] **Step 2: Manual smoke checklist** (launch the built app)

  - [ ] Fresh start (delete `%LOCALAPPDATA%\ReScene.NET\settings.json` first) → opens in **Beginner**, hub with 4 cards.
  - [ ] Each card opens its screen; `‹ Tasks` returns; **Open in Advanced** lands on the matching tab in Advanced.
  - [ ] Toggle Beginner⇄Advanced in the top bar; relaunch → mode persisted.
  - [ ] Settings dialog Mode radios match and apply live on Save.
  - [ ] In Advanced, `Ctrl+1..7` switch tabs; in Beginner they do nothing.
  - [ ] Drag-drop an `.srr` onto the window while in Beginner → flips to Advanced + Inspector shows the file.
  - [ ] Restore card: `.srr` → bulk grid + Restore All; `.srs` → media + Rebuild; other file → amber hint, no body.
  - [ ] Reconstruct card: Import-from-SRR populates settings; a custom-packer SRR shows the warning.
  - [ ] Simulate an "existing user": create a `settings.json` with the three legacy fields and no `Mode` → app opens in **Advanced**.

- [ ] **Step 3: Commit any fixes** found during smoke, then stop for review.

---

## Self-Review (completed during planning)

**Spec coverage** — every spec section maps to tasks: mode setting/persistence + first-run rule → Tasks 1–3; top-bar toggle + default → Tasks 4, 6, 18; shell container + two shells → Tasks 5–6, 9; hub (4 cards, no inspect/recent) → Tasks 7–9; the four beginner screens over existing VMs → Tasks 14–17; smart Restore routing → Tasks 10, 12, 17; guided Reconstruct import → Task 16; edge cases (tab-index clamp, Ctrl+1–7 gating, ISO auto-select, custom-packer messaging, existing-user default, open-file-forces-Advanced) → Tasks 4, 6, 11, 16; testing → Tasks 2, 8, 10, 12, 19.

**Type consistency** — verified against the live code: `CreateSRRCommand`/`CreateSRSCommand`/`StartCommand`/`StopCommand`/`ImportSRRCommand`/`RestoreCommand`/`CancelRestoreCommand`/`RebuildCommand`; status props `InputStatus`/`OutputStatus`/`SampleStatus`/`WinRarStatus`/`ReleaseStatus`/`MatchStatus`/`SRRStatus`/`SRSStatus`/`MediaStatus`; busy flags `IsCreating`/`IsRunning`/`IsRestoring`; styles `PrimaryButton`/`GhostButton`/`CancelButton`/`ToolbarToggleButton`; converters `BoolToVisibility`/`InverseBoolToVisibility`.

**Known follow-ups to confirm during execution** (flagged inline, not blockers): exact `FieldStatus` factory names and `IFileDialogService.OpenFileAsync` signature (Task 12); the Phase 1↔2↔3 forward references are handled by the temporary placeholder (Task 6) and comment-out-then-uncomment of child views (Task 9). Build after each task to catch these immediately.

