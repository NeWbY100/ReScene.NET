# Explain & Assist Forms Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make the five task forms (SRR Creator, SRS Creator, RAR Reconstructor, SRS Reconstructor, SRS Restorer) self-explanatory and assistive in place — every input shows what it is for and what to put there, with ✓/⚠/✗ feedback — without introducing a wizard.

**Architecture:** A reusable status primitive (`FieldStatus` record + `FieldStatusLine` control) and pure guidance helpers (`FieldGuidance`) are built once, then applied to each form. Always-visible caption `TextBlock`s explain each field; a `FieldStatusLine` under each field shows detection/validation results bound to a per-field `FieldStatus` on the ViewModel. Auto-fill only fills empty fields and never overwrites user input. A new `ReScene.NET.Tests` project unit-tests the pure logic (the WPF app currently has no test project).

**Tech Stack:** .NET 10, WPF, CommunityToolkit.Mvvm 8.4.2 (partial properties), xUnit 2.9.

---

## File Structure

**New files:**
- `ReScene.NET/Models/FieldStatus.cs` — `FieldState` enum + `FieldStatus` record (state + message + factories).
- `ReScene.NET/Helpers/FieldGuidance.cs` — pure, static guidance/validation helpers (output-path suggestion, media-vs-sample sanity, release-archive counting, sample description).
- `ReScene.NET/Controls/FieldStatusLine.xaml` (+ `.xaml.cs`) — reusable control rendering a `FieldStatus` as glyph + colored message.
- `ReScene.NET.Tests/ReScene.NET.Tests.csproj` — new xUnit project (net10.0-windows) referencing the WPF app.
- `ReScene.NET.Tests/FieldStatusTests.cs`, `ReScene.NET.Tests/FieldGuidanceTests.cs` — unit tests.

**Modified files (one per form, ViewModel + View):**
- `ReScene.NET/ViewModels/CreatorViewModel.cs` + `Views/CreatorView.xaml` (SRR Creator)
- `ReScene.NET/ViewModels/SRSCreatorViewModel.cs` + `Views/SRSCreatorView.xaml` (SRS Creator)
- `ReScene.NET/ViewModels/SRSReconstructorViewModel.cs` + `Views/SRSReconstructorView.xaml`
- `ReScene.NET/ViewModels/SampleRestorerViewModel.cs` + `Views/SampleRestorerView.xaml`
- `ReScene.NET/ViewModels/ReconstructorViewModel.cs` + `Views/ReconstructorView.xaml`
- `ReScene.NET.slnx` — add the test project.

**Conventions confirmed from the codebase:**
- ViewModels use `[ObservableProperty] public partial T Prop { get; set; }` (CommunityToolkit 8.4.2).
- Captions use `Foreground="{DynamicResource ForegroundSecondary}" FontSize="{DynamicResource FontSizeCaption}"` (already the house style for inline descriptions).
- Semantic brushes exist in `Resources/Tokens.xaml`: `AccentSuccess` (#1ABC9C), `AccentWarning` (#FFC107), `AccentError` (#F44747), `AccentPrimary` (#0078D4).
- Size formatting: `ReScene.NET.Helpers.FormatUtilities.FormatSize(long bytes)` (internal, same assembly).
- Converters/styles are registered in `App.xaml`; `BoolToVisibility` already exists.

---

## Phase A — Shared infrastructure

### Task 1: Create the test project

**Files:**
- Create: `ReScene.NET.Tests/ReScene.NET.Tests.csproj`
- Create: `ReScene.NET.Tests/SmokeTest.cs`
- Modify: `ReScene.NET.slnx`

- [ ] **Step 1: Create the test project file**

Create `ReScene.NET.Tests/ReScene.NET.Tests.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <TargetFramework>net10.0-windows</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <UseWPF>true</UseWPF>
    <IsPackable>false</IsPackable>
  </PropertyGroup>

  <ItemGroup>
    <PackageReference Include="coverlet.collector" Version="6.0.4" />
    <PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.14.1" />
    <PackageReference Include="xunit" Version="2.9.3" />
    <PackageReference Include="xunit.runner.visualstudio" Version="3.1.4" />
  </ItemGroup>

  <ItemGroup>
    <Using Include="Xunit" />
  </ItemGroup>

  <ItemGroup>
    <ProjectReference Include="..\ReScene.NET\ReScene.NET.csproj" />
  </ItemGroup>

</Project>
```

> Rationale: `net10.0-windows` is required to reference the WPF app project. `UseWPF` lets tests touch WPF types if ever needed; the unit tests in this plan only touch pure helpers.

- [ ] **Step 2: Add a smoke test**

Create `ReScene.NET.Tests/SmokeTest.cs`:

```csharp
namespace ReScene.NET.Tests;

public class SmokeTest
{
    [Fact]
    public void TestProjectRuns() => Assert.True(true);
}
```

- [ ] **Step 3: Register the project in the solution**

Add this line to `ReScene.NET.slnx` inside `<Solution>`, after the existing `ReScene.NET` project line:

```xml
  <Project Path="ReScene.NET.Tests/ReScene.NET.Tests.csproj" />
```

- [ ] **Step 4: Build and run the smoke test**

Run: `dotnet test ReScene.NET.Tests/ReScene.NET.Tests.csproj`
Expected: build succeeds; 1 test passed.

- [ ] **Step 5: Commit**

```bash
git add ReScene.NET.Tests/ReScene.NET.Tests.csproj ReScene.NET.Tests/SmokeTest.cs ReScene.NET.slnx
git commit -m "test: add ReScene.NET.Tests project for app unit tests"
```

---

### Task 2: `FieldStatus` model

**Files:**
- Create: `ReScene.NET/Models/FieldStatus.cs`
- Test: `ReScene.NET.Tests/FieldStatusTests.cs`

- [ ] **Step 1: Write the failing test**

Create `ReScene.NET.Tests/FieldStatusTests.cs`:

```csharp
using ReScene.NET.Models;

namespace ReScene.NET.Tests;

public class FieldStatusTests
{
    [Fact]
    public void None_HasNoneStateAndEmptyMessage()
    {
        Assert.Equal(FieldState.None, FieldStatus.None.State);
        Assert.Equal(string.Empty, FieldStatus.None.Message);
    }

    [Fact]
    public void Ok_SetsStateAndMessage()
    {
        FieldStatus status = FieldStatus.Ok("Found 3 volumes");
        Assert.Equal(FieldState.Ok, status.State);
        Assert.Equal("Found 3 volumes", status.Message);
    }

    [Fact]
    public void Warning_And_Error_SetState()
    {
        Assert.Equal(FieldState.Warning, FieldStatus.Warning("hmm").State);
        Assert.Equal(FieldState.Error, FieldStatus.Error("nope").State);
        Assert.Equal(FieldState.Info, FieldStatus.Info("fyi").State);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test ReScene.NET.Tests/ReScene.NET.Tests.csproj --filter "FullyQualifiedName~FieldStatusTests"`
Expected: FAIL — `FieldStatus`/`FieldState` do not exist.

- [ ] **Step 3: Implement the model**

Create `ReScene.NET/Models/FieldStatus.cs`:

```csharp
namespace ReScene.NET.Models;

/// <summary>
/// Severity of a field's detection/validation feedback.
/// </summary>
public enum FieldState
{
    /// <summary>No feedback to show; the status line is hidden.</summary>
    None,
    /// <summary>The value looks correct.</summary>
    Ok,
    /// <summary>Neutral information about the value.</summary>
    Info,
    /// <summary>The value is usable but something looks off.</summary>
    Warning,
    /// <summary>The value is missing or invalid.</summary>
    Error
}

/// <summary>
/// Per-field guidance shown beneath an input: a severity plus a short message.
/// </summary>
public sealed record FieldStatus(FieldState State, string Message)
{
    /// <summary>A hidden, empty status.</summary>
    public static readonly FieldStatus None = new(FieldState.None, string.Empty);

    public static FieldStatus Ok(string message) => new(FieldState.Ok, message);
    public static FieldStatus Info(string message) => new(FieldState.Info, message);
    public static FieldStatus Warning(string message) => new(FieldState.Warning, message);
    public static FieldStatus Error(string message) => new(FieldState.Error, message);
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test ReScene.NET.Tests/ReScene.NET.Tests.csproj --filter "FullyQualifiedName~FieldStatusTests"`
Expected: PASS (4 tests).

- [ ] **Step 5: Commit**

```bash
git add ReScene.NET/Models/FieldStatus.cs ReScene.NET.Tests/FieldStatusTests.cs
git commit -m "feat: add FieldStatus model for form field guidance"
```

---

### Task 3: `FieldGuidance` pure helpers

**Files:**
- Create: `ReScene.NET/Helpers/FieldGuidance.cs`
- Test: `ReScene.NET.Tests/FieldGuidanceTests.cs`

- [ ] **Step 1: Write the failing tests**

Create `ReScene.NET.Tests/FieldGuidanceTests.cs`:

```csharp
using ReScene.NET.Helpers;
using ReScene.NET.Models;

namespace ReScene.NET.Tests;

public class FieldGuidanceTests
{
    [Fact]
    public void SuggestSiblingPath_ReplacesExtension_NextToInput()
    {
        string result = FieldGuidance.SuggestSiblingPath(@"C:\rel\movie.sample.mkv", ".srs");
        Assert.Equal(@"C:\rel\movie.sample.srs", result);
    }

    [Fact]
    public void SuggestSiblingPath_EmptyInput_ReturnsEmpty()
    {
        Assert.Equal(string.Empty, FieldGuidance.SuggestSiblingPath("", ".srr"));
    }

    [Fact]
    public void EvaluateMediaAgainstSample_LargerMedia_IsOk()
    {
        FieldStatus status = FieldGuidance.EvaluateMediaAgainstSample(mediaSize: 700_000_000, sampleSize: 20_000_000);
        Assert.Equal(FieldState.Ok, status.State);
    }

    [Fact]
    public void EvaluateMediaAgainstSample_SmallerMedia_WarnsWrongFile()
    {
        FieldStatus status = FieldGuidance.EvaluateMediaAgainstSample(mediaSize: 10_000_000, sampleSize: 20_000_000);
        Assert.Equal(FieldState.Warning, status.State);
        Assert.Contains("smaller", status.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(".mkv", FieldState.Ok)]
    [InlineData(".avi", FieldState.Ok)]
    [InlineData(".mp4", FieldState.Ok)]
    [InlineData(".mp3", FieldState.Ok)]
    [InlineData(".txt", FieldState.Warning)]
    public void DescribeSample_ClassifiesByExtension(string ext, FieldState expected)
    {
        FieldStatus status = FieldGuidance.DescribeSample(ext, sizeBytes: 24_000_000);
        Assert.Equal(expected, status.State);
    }

    [Fact]
    public void CountReleaseArchives_CountsRarAndOldStyleVolumes()
    {
        string dir = Path.Combine(Path.GetTempPath(), "fg_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "x.rar"), "");
            File.WriteAllText(Path.Combine(dir, "x.r00"), "");
            File.WriteAllText(Path.Combine(dir, "x.r01"), "");
            File.WriteAllText(Path.Combine(dir, "x.nfo"), "");
            Assert.Equal(3, FieldGuidance.CountReleaseArchives(dir));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test ReScene.NET.Tests/ReScene.NET.Tests.csproj --filter "FullyQualifiedName~FieldGuidanceTests"`
Expected: FAIL — `FieldGuidance` does not exist.

- [ ] **Step 3: Implement the helpers**

Create `ReScene.NET/Helpers/FieldGuidance.cs`:

```csharp
using System.IO;
using ReScene.NET.Models;

namespace ReScene.NET.Helpers;

/// <summary>
/// Pure, side-effect-light helpers that turn user input into <see cref="FieldStatus"/>
/// guidance and suggested values. No WPF dependencies — unit-testable.
/// </summary>
public static class FieldGuidance
{
    private static readonly HashSet<string> _knownSampleExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mkv", ".avi", ".mp4", ".m4v", ".mov", ".wmv", ".vob", ".mpg", ".mpeg",
        ".ts", ".m2ts", ".flac", ".mp3", ".ogg", ".wav"
    };

    /// <summary>
    /// Returns <paramref name="inputPath"/> with its extension replaced by
    /// <paramref name="newExtension"/>, in the same directory. Empty input yields empty output.
    /// </summary>
    public static string SuggestSiblingPath(string inputPath, string newExtension)
    {
        if (string.IsNullOrWhiteSpace(inputPath))
        {
            return string.Empty;
        }

        string dir = Path.GetDirectoryName(inputPath) ?? ".";
        string name = Path.GetFileNameWithoutExtension(inputPath);
        return Path.Combine(dir, name + newExtension);
    }

    /// <summary>
    /// Sanity-checks a chosen full media file against the sample it should contain.
    /// The full media must be at least as large as the sample.
    /// </summary>
    public static FieldStatus EvaluateMediaAgainstSample(long mediaSize, long sampleSize)
    {
        if (sampleSize <= 0)
        {
            return FieldStatus.None;
        }

        return mediaSize >= sampleSize
            ? FieldStatus.Ok("Media is larger than the sample — looks right.")
            : FieldStatus.Warning("Media is smaller than the sample; this is likely the wrong file.");
    }

    /// <summary>
    /// Describes a sample file by extension and size, warning on unrecognized media types.
    /// </summary>
    public static FieldStatus DescribeSample(string extension, long sizeBytes)
    {
        string label = extension.TrimStart('.').ToUpperInvariant();
        return _knownSampleExtensions.Contains(extension)
            ? FieldStatus.Ok($"{label} sample, {FormatUtilities.FormatSize(sizeBytes)}")
            : FieldStatus.Warning($"Unrecognized media type ({label}); SRS may not support it.");
    }

    /// <summary>
    /// Counts RAR archive files (.rar and old-style .r00/.r01… and .partN.rar) in a directory.
    /// Returns 0 when the directory is missing.
    /// </summary>
    public static int CountReleaseArchives(string directory)
    {
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
        {
            return 0;
        }

        int count = 0;
        foreach (string file in Directory.EnumerateFiles(directory))
        {
            string ext = Path.GetExtension(file);
            if (ext.Equals(".rar", StringComparison.OrdinalIgnoreCase) || IsOldStyleVolume(ext))
            {
                count++;
            }
        }

        return count;
    }

    // Matches ".r00".."r999": 'r' followed by digits.
    private static bool IsOldStyleVolume(string ext)
    {
        if (ext.Length < 3 || (ext[1] != 'r' && ext[1] != 'R'))
        {
            return false;
        }

        for (int i = 2; i < ext.Length; i++)
        {
            if (!char.IsDigit(ext[i]))
            {
                return false;
            }
        }

        return true;
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test ReScene.NET.Tests/ReScene.NET.Tests.csproj --filter "FullyQualifiedName~FieldGuidanceTests"`
Expected: PASS (all FieldGuidance tests).

- [ ] **Step 5: Commit**

```bash
git add ReScene.NET/Helpers/FieldGuidance.cs ReScene.NET.Tests/FieldGuidanceTests.cs
git commit -m "feat: add FieldGuidance pure helpers for field validation"
```

---

### Task 4: `FieldStatusLine` reusable control

**Files:**
- Create: `ReScene.NET/Controls/FieldStatusLine.xaml`
- Create: `ReScene.NET/Controls/FieldStatusLine.xaml.cs`

> No unit test: this is a visual control. Verified by build and by appearing correctly once wired into the first form (Task 5/6).

- [ ] **Step 1: Create the control XAML**

Create `ReScene.NET/Controls/FieldStatusLine.xaml`:

```xml
<UserControl x:Class="ReScene.NET.Controls.FieldStatusLine"
             xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
             xmlns:m="clr-namespace:ReScene.NET.Models"
             x:Name="Root">
  <StackPanel Orientation="Horizontal" VerticalAlignment="Center">
    <StackPanel.Style>
      <Style TargetType="StackPanel">
        <Setter Property="Visibility" Value="Visible" />
        <Style.Triggers>
          <DataTrigger Binding="{Binding Status.State, ElementName=Root}" Value="{x:Static m:FieldState.None}">
            <Setter Property="Visibility" Value="Collapsed" />
          </DataTrigger>
        </Style.Triggers>
      </Style>
    </StackPanel.Style>

    <!-- Glyph -->
    <TextBlock x:Name="Glyph"
               Margin="0,0,4,0"
               FontSize="{DynamicResource FontSizeCaption}"
               VerticalAlignment="Center">
      <TextBlock.Style>
        <Style TargetType="TextBlock">
          <Setter Property="Text" Value="" />
          <Style.Triggers>
            <DataTrigger Binding="{Binding Status.State, ElementName=Root}" Value="{x:Static m:FieldState.Ok}">
              <Setter Property="Text" Value="&#x2713;" />
              <Setter Property="Foreground" Value="{DynamicResource AccentSuccess}" />
            </DataTrigger>
            <DataTrigger Binding="{Binding Status.State, ElementName=Root}" Value="{x:Static m:FieldState.Info}">
              <Setter Property="Text" Value="&#x2139;" />
              <Setter Property="Foreground" Value="{DynamicResource AccentPrimary}" />
            </DataTrigger>
            <DataTrigger Binding="{Binding Status.State, ElementName=Root}" Value="{x:Static m:FieldState.Warning}">
              <Setter Property="Text" Value="&#x26A0;" />
              <Setter Property="Foreground" Value="{DynamicResource AccentWarning}" />
            </DataTrigger>
            <DataTrigger Binding="{Binding Status.State, ElementName=Root}" Value="{x:Static m:FieldState.Error}">
              <Setter Property="Text" Value="&#x2717;" />
              <Setter Property="Foreground" Value="{DynamicResource AccentError}" />
            </DataTrigger>
          </Style.Triggers>
        </Style>
      </TextBlock.Style>
    </TextBlock>

    <!-- Message -->
    <TextBlock Text="{Binding Status.Message, ElementName=Root}"
               FontSize="{DynamicResource FontSizeCaption}"
               TextWrapping="Wrap"
               VerticalAlignment="Center">
      <TextBlock.Style>
        <Style TargetType="TextBlock">
          <Setter Property="Foreground" Value="{DynamicResource ForegroundSecondary}" />
          <Style.Triggers>
            <DataTrigger Binding="{Binding Status.State, ElementName=Root}" Value="{x:Static m:FieldState.Warning}">
              <Setter Property="Foreground" Value="{DynamicResource AccentWarning}" />
            </DataTrigger>
            <DataTrigger Binding="{Binding Status.State, ElementName=Root}" Value="{x:Static m:FieldState.Error}">
              <Setter Property="Foreground" Value="{DynamicResource AccentError}" />
            </DataTrigger>
          </Style.Triggers>
        </Style>
      </TextBlock.Style>
    </TextBlock>
  </StackPanel>
</UserControl>
```

- [ ] **Step 2: Create the control code-behind with the `Status` dependency property**

Create `ReScene.NET/Controls/FieldStatusLine.xaml.cs`:

```csharp
using System.Windows;
using System.Windows.Controls;
using ReScene.NET.Models;

namespace ReScene.NET.Controls;

/// <summary>
/// Renders a <see cref="FieldStatus"/> as a colored glyph (✓/ℹ/⚠/✗) plus its message.
/// Hidden when the status state is <see cref="FieldState.None"/>.
/// </summary>
public partial class FieldStatusLine : UserControl
{
    public static readonly DependencyProperty StatusProperty =
        DependencyProperty.Register(
            nameof(Status),
            typeof(FieldStatus),
            typeof(FieldStatusLine),
            new PropertyMetadata(FieldStatus.None));

    public FieldStatusLine() => InitializeComponent();

    /// <summary>The status to display. Defaults to <see cref="FieldStatus.None"/> (hidden).</summary>
    public FieldStatus Status
    {
        get => (FieldStatus)GetValue(StatusProperty);
        set => SetValue(StatusProperty, value);
    }
}
```

- [ ] **Step 3: Build to verify the control compiles**

Run: `dotnet build ReScene.NET/ReScene.NET.csproj`
Expected: build succeeds (0 errors).

- [ ] **Step 4: Commit**

```bash
git add ReScene.NET/Controls/FieldStatusLine.xaml ReScene.NET/Controls/FieldStatusLine.xaml.cs
git commit -m "feat: add FieldStatusLine control for inline field feedback"
```

---

## Phase B — SRR Creator (reference form)

> This phase is the worked example. Phases C–F apply the same `FieldStatus` /
> `FieldStatusLine` / `FieldGuidance` building blocks to the other four forms with their
> own field specifics.

### Task 5: SRR Creator ViewModel — status properties & auto-fill

**Files:**
- Modify: `ReScene.NET/ViewModels/CreatorViewModel.cs`

> `AutoSetOutputPath` already implements the "fill only when empty" policy (lines 577-585) and `OnInputPathChanged` already runs on input change. We add two status properties and populate `InputStatus` from `FieldGuidance`.

- [ ] **Step 1: Add the status properties**

In `CreatorViewModel.cs`, add `using ReScene.NET.Models;` to the usings, then add these properties next to the existing `InputPath`/`OutputPath` properties (after line 75, the `OutputPath` property):

```csharp
    // Field guidance
    [ObservableProperty]
    public partial FieldStatus InputStatus { get; set; } = FieldStatus.None;

    [ObservableProperty]
    public partial FieldStatus OutputStatus { get; set; } = FieldStatus.None;
```

- [ ] **Step 2: Populate `InputStatus` when the input changes**

In `CreatorViewModel.cs`, replace the existing `OnInputPathChanged` method (lines 132-141):

```csharp
    partial void OnInputPathChanged(string value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            IsSFVInput = Path.GetExtension(value).Equals(".sfv", StringComparison.OrdinalIgnoreCase);
        }

        UpdateStoredNames();
        AutoScanReleaseFiles();
    }
```

with:

```csharp
    partial void OnInputPathChanged(string value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            IsSFVInput = Path.GetExtension(value).Equals(".sfv", StringComparison.OrdinalIgnoreCase);
        }

        UpdateStoredNames();
        AutoScanReleaseFiles();
        UpdateInputStatus(value);
    }

    private void UpdateInputStatus(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            InputStatus = FieldStatus.None;
            return;
        }

        if (!File.Exists(value))
        {
            InputStatus = FieldStatus.Error("This file does not exist.");
            return;
        }

        string releaseDir = Path.GetDirectoryName(value) ?? ".";
        string releaseName = Path.GetFileName(releaseDir);
        int archiveCount = FieldGuidance.CountReleaseArchives(releaseDir);

        InputStatus = archiveCount > 0
            ? FieldStatus.Ok($"Release \"{releaseName}\" — {archiveCount} archive file(s) in this folder.")
            : FieldStatus.Info($"Release folder: \"{releaseName}\". No .rar volumes found here (SFV-only is fine).");
    }
```

- [ ] **Step 3: Set `OutputStatus` after auto-fill**

In `CreatorViewModel.cs`, replace `AutoSetOutputPath` (lines 577-585):

```csharp
    private void AutoSetOutputPath(string inputPath)
    {
        if (string.IsNullOrWhiteSpace(OutputPath))
        {
            string dir = Path.GetDirectoryName(inputPath) ?? ".";
            string name = Path.GetFileNameWithoutExtension(inputPath);
            OutputPath = Path.Combine(dir, name + ".srr");
        }
    }
```

with:

```csharp
    private void AutoSetOutputPath(string inputPath)
    {
        if (string.IsNullOrWhiteSpace(OutputPath))
        {
            OutputPath = FieldGuidance.SuggestSiblingPath(inputPath, ".srr");
            OutputStatus = FieldStatus.Info("Auto-filled next to the input. Change it if you want the SRR elsewhere.");
        }
    }
```

- [ ] **Step 4: Build to verify**

Run: `dotnet build ReScene.NET/ReScene.NET.csproj`
Expected: build succeeds.

- [ ] **Step 5: Commit**

```bash
git add ReScene.NET/ViewModels/CreatorViewModel.cs
git commit -m "feat(srr-creator): add field guidance status to ViewModel"
```

---

### Task 6: SRR Creator View — captions & status lines

**Files:**
- Modify: `ReScene.NET/Views/CreatorView.xaml`

- [ ] **Step 1: Declare the controls namespace**

In `CreatorView.xaml`, add this attribute to the root `<UserControl>` element (after the `xmlns:vm` line):

```xml
             xmlns:c="clr-namespace:ReScene.NET.Controls"
```

- [ ] **Step 2: Add caption + status line to the Input section**

In `CreatorView.xaml`, replace the Input `StackPanel` (Grid.Row="1", lines 24-33):

```xml
    <StackPanel Grid.Row="1">
      <TextBlock Text="Input" FontWeight="SemiBold" Margin="0,0,0,2" />
      <DockPanel Margin="0,0,0,2">
        <Button DockPanel.Dock="Right" Content="Browse"
                Command="{Binding BrowseInputCommand}"
                Style="{StaticResource GhostButton}"
                Margin="4,0,0,0" MinWidth="75" />
        <TextBox x:Name="InputTextBox" Text="{Binding InputPath}" />
      </DockPanel>
    </StackPanel>
```

with:

```xml
    <StackPanel Grid.Row="1">
      <TextBlock Text="Input" FontWeight="SemiBold" Margin="0,0,0,2" />
      <DockPanel Margin="0,0,0,2">
        <Button DockPanel.Dock="Right" Content="Browse"
                Command="{Binding BrowseInputCommand}"
                Style="{StaticResource GhostButton}"
                Margin="4,0,0,0" MinWidth="75" />
        <TextBox x:Name="InputTextBox" Text="{Binding InputPath}" />
      </DockPanel>
      <TextBlock Text="The release's .sfv file or first .rar volume. The folder it sits in is treated as the release directory."
                 Foreground="{DynamicResource ForegroundSecondary}" FontSize="{DynamicResource FontSizeCaption}"
                 TextWrapping="Wrap" Margin="0,0,0,1" />
      <c:FieldStatusLine Status="{Binding InputStatus}" />
    </StackPanel>
```

- [ ] **Step 3: Add caption + status line to the Output section**

In `CreatorView.xaml`, replace the Output `StackPanel` (Grid.Row="0" inside the bottom grid, lines 95-104):

```xml
      <StackPanel Grid.Row="0">
        <TextBlock Text="Output" FontWeight="SemiBold" Margin="0,0,0,2" />
        <DockPanel>
          <Button DockPanel.Dock="Right" Content="Browse"
                  Command="{Binding BrowseOutputCommand}"
                  Style="{StaticResource GhostButton}"
                  Margin="4,0,0,0" MinWidth="75" />
          <TextBox x:Name="OutputTextBox" Text="{Binding OutputPath}" />
        </DockPanel>
      </StackPanel>
```

with:

```xml
      <StackPanel Grid.Row="0">
        <TextBlock Text="Output" FontWeight="SemiBold" Margin="0,0,0,2" />
        <DockPanel>
          <Button DockPanel.Dock="Right" Content="Browse"
                  Command="{Binding BrowseOutputCommand}"
                  Style="{StaticResource GhostButton}"
                  Margin="4,0,0,0" MinWidth="75" />
          <TextBox x:Name="OutputTextBox" Text="{Binding OutputPath}" />
        </DockPanel>
        <TextBlock Text="Where the .srr file will be written."
                   Foreground="{DynamicResource ForegroundSecondary}" FontSize="{DynamicResource FontSizeCaption}"
                   TextWrapping="Wrap" Margin="0,0,0,1" />
        <c:FieldStatusLine Status="{Binding OutputStatus}" />
      </StackPanel>
```

- [ ] **Step 4: Build and launch to verify visually**

Run: `dotnet build ReScene.NET/ReScene.NET.csproj`
Expected: build succeeds. (Optional manual check: run the app, open SRR Creator, pick an input — caption shows and a ✓/ℹ status appears; output auto-fills with an ℹ note.)

- [ ] **Step 5: Commit**

```bash
git add ReScene.NET/Views/CreatorView.xaml
git commit -m "feat(srr-creator): add field captions and status lines"
```

---

### Task 7: SRR Creator — "what's needed" gating hint

**Files:**
- Modify: `ReScene.NET/ViewModels/CreatorViewModel.cs`
- Modify: `ReScene.NET/Views/CreatorView.xaml`

- [ ] **Step 1: Add a computed hint property**

In `CreatorViewModel.cs`, add this property after `OutputStatus` (from Task 5 Step 1):

```csharp
    [ObservableProperty]
    public partial string ActionHint { get; set; } = string.Empty;
```

- [ ] **Step 2: Recompute the hint when inputs or state change**

In `CreatorViewModel.cs`, add a helper and call it from the relevant `OnXxxChanged` hooks. Add the method:

```csharp
    private void UpdateActionHint()
    {
        if (IsCreating)
        {
            ActionHint = string.Empty;
        }
        else if (string.IsNullOrWhiteSpace(InputPath))
        {
            ActionHint = "Select an input file to continue.";
        }
        else if (string.IsNullOrWhiteSpace(OutputPath))
        {
            ActionHint = "Choose where to save the SRR to continue.";
        }
        else
        {
            ActionHint = string.Empty;
        }
    }
```

Then add `UpdateActionHint();` as the last line of `OnInputPathChanged` (after `UpdateInputStatus(value);`), and add these hooks (place near the other `partial void On…Changed` methods):

```csharp
    partial void OnOutputPathChanged(string value) => UpdateActionHint();

    partial void OnIsCreatingChanged(bool value) => UpdateActionHint();
```

- [ ] **Step 3: Show the hint next to the action button**

In `CreatorView.xaml`, in the Action row `StackPanel` (Grid.Row="4", lines 141-162), replace the `ProgressMessage` `TextBlock` (lines 154-156):

```xml
          <TextBlock Text="{Binding ProgressMessage}"
                     Visibility="{Binding ShowProgress, Converter={StaticResource BoolToVisibility}}"
                     VerticalAlignment="Center" />
```

with:

```xml
          <StackPanel Orientation="Horizontal" VerticalAlignment="Center">
            <TextBlock Text="{Binding ActionHint}"
                       Foreground="{DynamicResource ForegroundSecondary}"
                       FontSize="{DynamicResource FontSizeCaption}"
                       VerticalAlignment="Center" />
            <TextBlock Text="{Binding ProgressMessage}"
                       Visibility="{Binding ShowProgress, Converter={StaticResource BoolToVisibility}}"
                       VerticalAlignment="Center" />
          </StackPanel>
```

- [ ] **Step 4: Build to verify**

Run: `dotnet build ReScene.NET/ReScene.NET.csproj`
Expected: build succeeds.

- [ ] **Step 5: Commit**

```bash
git add ReScene.NET/ViewModels/CreatorViewModel.cs ReScene.NET/Views/CreatorView.xaml
git commit -m "feat(srr-creator): add action hint explaining why Create is disabled"
```

---

## Phase C — SRS Creator

### Task 8: SRS Creator — captions, sample detection, auto-fill

**Files:**
- Modify: `ReScene.NET/ViewModels/SRSCreatorViewModel.cs`
- Modify: `ReScene.NET/Views/SRSCreatorView.xaml`

> Read `SRSCreatorViewModel.cs` first to confirm the `InputPath`/`OutputPath`/`BrowseInput` member names and whether an `OnInputPathChanged` hook already exists. The steps below assume the same shape as `CreatorViewModel` (an `InputPath`, `OutputPath`, and a `BrowseInputAsync` that sets `InputPath`). Adjust property names to match if they differ.

- [ ] **Step 1: Add status properties to the ViewModel**

Add `using ReScene.NET.Models;` and, next to the `InputPath`/`OutputPath` properties:

```csharp
    [ObservableProperty]
    public partial FieldStatus SampleStatus { get; set; } = FieldStatus.None;

    [ObservableProperty]
    public partial FieldStatus OutputStatus { get; set; } = FieldStatus.None;
```

- [ ] **Step 2: Detect the sample and auto-fill output on input change**

Add (or extend the existing) `OnInputPathChanged` hook in `SRSCreatorViewModel.cs`:

```csharp
    partial void OnInputPathChanged(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            SampleStatus = FieldStatus.None;
            return;
        }

        if (!File.Exists(value))
        {
            SampleStatus = FieldStatus.Error("This file does not exist.");
            return;
        }

        long size = new FileInfo(value).Length;
        SampleStatus = FieldGuidance.DescribeSample(Path.GetExtension(value), size);

        if (string.IsNullOrWhiteSpace(OutputPath))
        {
            OutputPath = FieldGuidance.SuggestSiblingPath(value, ".srs");
            OutputStatus = FieldStatus.Info("Auto-filled from the sample name. Change it if needed.");
        }
    }
```

> If `SRSCreatorViewModel` already has an `OnInputPathChanged`, merge these statements into it rather than adding a second method (a duplicate partial hook will not compile).

- [ ] **Step 3: Add the controls namespace and captions to the View**

In `SRSCreatorView.xaml`, add to the root `<UserControl>`:

```xml
             xmlns:c="clr-namespace:ReScene.NET.Controls"
```

After the Sample File input `DockPanel` (lines 16-22), insert:

```xml
    <TextBlock DockPanel.Dock="Top"
               Text="The small sample clip (.mkv, .avi, .mp4, …) to store. Usually in a Sample/ subfolder of the release."
               Foreground="{DynamicResource ForegroundSecondary}" FontSize="{DynamicResource FontSizeCaption}"
               TextWrapping="Wrap" Margin="0,1,0,0" />
    <c:FieldStatusLine DockPanel.Dock="Top" Status="{Binding SampleStatus}" />
```

After the Output input `DockPanel` (lines 63-69), insert:

```xml
    <TextBlock DockPanel.Dock="Top"
               Text="Where the .srs file will be written."
               Foreground="{DynamicResource ForegroundSecondary}" FontSize="{DynamicResource FontSizeCaption}"
               TextWrapping="Wrap" Margin="0,1,0,0" />
    <c:FieldStatusLine DockPanel.Dock="Top" Status="{Binding OutputStatus}" />
```

> Note: these views use `DockPanel` with `DockPanel.Dock="Top"`. Insert the new elements immediately after the element they should appear below, and before the following separator `Border`, so dock order is preserved.

- [ ] **Step 4: Build to verify**

Run: `dotnet build ReScene.NET/ReScene.NET.csproj`
Expected: build succeeds.

- [ ] **Step 5: Commit**

```bash
git add ReScene.NET/ViewModels/SRSCreatorViewModel.cs ReScene.NET/Views/SRSCreatorView.xaml
git commit -m "feat(srs-creator): add captions, sample detection, output auto-fill"
```

---

## Phase D — SRS Reconstructor

### Task 9: SRS Reconstructor — captions, media sanity, output auto-fill

**Files:**
- Modify: `ReScene.NET/ViewModels/SRSReconstructorViewModel.cs`
- Modify: `ReScene.NET/Views/SRSReconstructorView.xaml`

> Read `SRSReconstructorViewModel.cs` first to confirm member names (`SRSFilePath`, `MediaFilePath`, `OutputPath`) and how the SRS is read. The view's bound properties are `SRSFilePath`, `MediaFilePath`, `OutputPath` (confirmed in `SRSReconstructorView.xaml`).

- [ ] **Step 1: Add status properties**

Add `using ReScene.NET.Models;` and:

```csharp
    [ObservableProperty]
    public partial FieldStatus SRSStatus { get; set; } = FieldStatus.None;

    [ObservableProperty]
    public partial FieldStatus MediaStatus { get; set; } = FieldStatus.None;

    [ObservableProperty]
    public partial FieldStatus OutputStatus { get; set; } = FieldStatus.None;
```

- [ ] **Step 2: On SRS pick — record expected sample size and auto-fill output**

Add a private field to hold the expected sample size for the media sanity check:

```csharp
    private long _expectedSampleSize;
```

Add/extend the `OnSRSFilePathChanged` hook. Use the existing SRS parsing already used by this ViewModel to obtain the sample name and size; if the ViewModel does not already load the SRS here, call the same service/`SRSFile` API it uses elsewhere. Concretely:

```csharp
    partial void OnSRSFilePathChanged(string value)
    {
        _expectedSampleSize = 0;

        if (string.IsNullOrWhiteSpace(value))
        {
            SRSStatus = FieldStatus.None;
            return;
        }

        if (!File.Exists(value))
        {
            SRSStatus = FieldStatus.Error("This .srs file does not exist.");
            return;
        }

        try
        {
            SRSFile srs = SRSFile.Load(value);
            _expectedSampleSize = srs.FileData.FileSize;
            SRSStatus = FieldStatus.Ok(
                $"Sample: {srs.FileData.FileName} ({FormatUtilities.FormatSize(srs.FileData.FileSize)}).");

            if (string.IsNullOrWhiteSpace(OutputPath))
            {
                string dir = Path.GetDirectoryName(value) ?? ".";
                OutputPath = Path.Combine(dir, srs.FileData.FileName);
                OutputStatus = FieldStatus.Info("Auto-filled from the SRS sample name. Change it if needed.");
            }
        }
        catch (Exception ex)
        {
            SRSStatus = FieldStatus.Error($"Could not read this SRS: {ex.Message}");
        }
    }
```

> Verify against `SRSFile`: the SRS sample name/size accessors may be named differently (e.g. `srs.FileData.FileName` / `.FileSize`). Open `ReScene.Lib/ReScene.Lib/SRR/SRSFile.cs` and use the actual property names. Add `using ReScene.SRS;` and `using ReScene.NET.Helpers;` as needed.

- [ ] **Step 3: On media pick — sanity-check size against the sample**

Add/extend the `OnMediaFilePathChanged` hook:

```csharp
    partial void OnMediaFilePathChanged(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            MediaStatus = FieldStatus.None;
            return;
        }

        if (!File.Exists(value))
        {
            MediaStatus = FieldStatus.Error("This media file does not exist.");
            return;
        }

        long mediaSize = new FileInfo(value).Length;
        MediaStatus = FieldGuidance.EvaluateMediaAgainstSample(mediaSize, _expectedSampleSize);
    }
```

- [ ] **Step 4: Add captions + status lines to the View**

In `SRSReconstructorView.xaml`, add to the root `<UserControl>`:

```xml
             xmlns:c="clr-namespace:ReScene.NET.Controls"
```

After the SRS File `DockPanel` (lines 16-22), insert:

```xml
    <TextBlock DockPanel.Dock="Top"
               Text="The .srs file describing the sample to rebuild."
               Foreground="{DynamicResource ForegroundSecondary}" FontSize="{DynamicResource FontSizeCaption}"
               TextWrapping="Wrap" Margin="0,1,0,0" />
    <c:FieldStatusLine DockPanel.Dock="Top" Status="{Binding SRSStatus}" />
```

After the Media File `DockPanel` (lines 29-35), insert:

```xml
    <TextBlock DockPanel.Dock="Top"
               Text="The full original media file the sample was cut from."
               Foreground="{DynamicResource ForegroundSecondary}" FontSize="{DynamicResource FontSizeCaption}"
               TextWrapping="Wrap" Margin="0,1,0,0" />
    <c:FieldStatusLine DockPanel.Dock="Top" Status="{Binding MediaStatus}" />
```

After the Output `DockPanel` (lines 42-48), insert:

```xml
    <TextBlock DockPanel.Dock="Top"
               Text="Where the rebuilt sample will be written."
               Foreground="{DynamicResource ForegroundSecondary}" FontSize="{DynamicResource FontSizeCaption}"
               TextWrapping="Wrap" Margin="0,1,0,0" />
    <c:FieldStatusLine DockPanel.Dock="Top" Status="{Binding OutputStatus}" />
```

- [ ] **Step 5: Build to verify**

Run: `dotnet build ReScene.NET/ReScene.NET.csproj`
Expected: build succeeds.

- [ ] **Step 6: Commit**

```bash
git add ReScene.NET/ViewModels/SRSReconstructorViewModel.cs ReScene.NET/Views/SRSReconstructorView.xaml
git commit -m "feat(srs-reconstructor): add captions, SRS info, media sanity check"
```

---

## Phase E — SRS Restorer

### Task 10: SRS Restorer — captions, output auto-fill, match summary

**Files:**
- Modify: `ReScene.NET/ViewModels/SampleRestorerViewModel.cs`
- Modify: `ReScene.NET/Views/SampleRestorerView.xaml`

> `SampleRestorerViewModel` already matches media files to samples (`MatchMediaFiles`, lines 278-313) and already has `OnMediaDirectoryPathChanged`. We surface a match summary and add captions; we also auto-fill the output directory.

- [ ] **Step 1: Add status properties**

Add `using ReScene.NET.Models;` and:

```csharp
    [ObservableProperty]
    public partial FieldStatus SRRStatus { get; set; } = FieldStatus.None;

    [ObservableProperty]
    public partial FieldStatus MatchStatus { get; set; } = FieldStatus.None;
```

- [ ] **Step 2: Set `SRRStatus` after loading SRS entries**

In `SampleRestorerViewModel.cs`, at the end of `LoadSRSEntries` (after the `Log($"Found {entries.Count} SRS file(s) in SRR");` line, inside the `try`), add:

```csharp
            SRRStatus = entries.Count > 0
                ? FieldStatus.Ok($"{entries.Count} embedded SRS sample(s) found.")
                : FieldStatus.Warning("No embedded SRS samples found in this SRR.");
```

And in the `catch (Exception ex)` of `LoadSRSEntries`, add:

```csharp
            SRRStatus = FieldStatus.Error($"Could not read this SRR: {ex.Message}");
```

- [ ] **Step 3: Set `MatchStatus` after matching, and auto-fill output dir**

In `SampleRestorerViewModel.cs`, at the end of `MatchMediaFiles` (after `Log($"Matched {found} of {SRSEntries.Count} file(s)...` and before `RestoreCommand.NotifyCanExecuteChanged();`), add:

```csharp
        MatchStatus = found == SRSEntries.Count && found > 0
            ? FieldStatus.Ok($"Matched all {found} sample(s) to media files.")
            : found > 0
                ? FieldStatus.Warning($"Matched {found} of {SRSEntries.Count} sample(s); the rest need a media file.")
                : FieldStatus.Warning("No samples matched a file in this folder.");

        if (string.IsNullOrWhiteSpace(OutputDirectoryPath))
        {
            OutputDirectoryPath = MediaDirectoryPath;
        }
```

- [ ] **Step 4: Add the controls namespace and captions to the View**

In `SampleRestorerView.xaml`, add to the root `<UserControl>`:

```xml
             xmlns:c="clr-namespace:ReScene.NET.Controls"
```

After the SRR File `DockPanel` (lines 16-22), insert:

```xml
    <TextBlock DockPanel.Dock="Top"
               Text="The .srr file containing embedded .srs sample data."
               Foreground="{DynamicResource ForegroundSecondary}" FontSize="{DynamicResource FontSizeCaption}"
               TextWrapping="Wrap" Margin="0,1,0,0" />
    <c:FieldStatusLine DockPanel.Dock="Top" Status="{Binding SRRStatus}" />
```

After the Media Directory `DockPanel` (lines 29-35), insert:

```xml
    <TextBlock DockPanel.Dock="Top"
               Text="Folder holding the full media files the samples were cut from. Files are matched to samples automatically."
               Foreground="{DynamicResource ForegroundSecondary}" FontSize="{DynamicResource FontSizeCaption}"
               TextWrapping="Wrap" Margin="0,1,0,0" />
    <c:FieldStatusLine DockPanel.Dock="Top" Status="{Binding MatchStatus}" />
```

After the Output Directory `DockPanel` (lines 42-48), insert:

```xml
    <TextBlock DockPanel.Dock="Top"
               Text="Folder where restored samples will be written. Defaults to the media folder."
               Foreground="{DynamicResource ForegroundSecondary}" FontSize="{DynamicResource FontSizeCaption}"
               TextWrapping="Wrap" Margin="0,1,0,0" />
```

- [ ] **Step 5: Build to verify**

Run: `dotnet build ReScene.NET/ReScene.NET.csproj`
Expected: build succeeds.

- [ ] **Step 6: Commit**

```bash
git add ReScene.NET/ViewModels/SampleRestorerViewModel.cs ReScene.NET/Views/SampleRestorerView.xaml
git commit -m "feat(srs-restorer): add captions, match summary, output auto-fill"
```

---

## Phase F — RAR Reconstructor

### Task 11: RAR Reconstructor — path captions & WinRAR/Release/Output status

**Files:**
- Modify: `ReScene.NET/ViewModels/ReconstructorViewModel.cs`
- Modify: `ReScene.NET/Views/ReconstructorView.xaml`

> This form is the most complex; its Options block is already richly captioned. We only add captions + status to the four top-level paths (`WinRarPath`, `ReleasePath`, `VerificationPath`, `OutputPath`) and reinforce "Import from SRR" as the recommended first step. Read `ReconstructorViewModel.cs` first to confirm these exact property names (they match `ReconstructorView.xaml`: `WinRarPath`, `ReleasePath`, `VerificationPath`, `OutputPath`).

- [ ] **Step 1: Add status properties**

Add `using ReScene.NET.Models;` and:

```csharp
    [ObservableProperty]
    public partial FieldStatus WinRarStatus { get; set; } = FieldStatus.None;

    [ObservableProperty]
    public partial FieldStatus ReleaseStatus { get; set; } = FieldStatus.None;

    [ObservableProperty]
    public partial FieldStatus VerifyStatus { get; set; } = FieldStatus.None;
```

- [ ] **Step 2: Validate WinRAR on change (existence + version)**

Add/extend the `OnWinRarPathChanged` hook in `ReconstructorViewModel.cs`:

```csharp
    partial void OnWinRarPathChanged(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            WinRarStatus = FieldStatus.None;
            return;
        }

        if (!File.Exists(value))
        {
            WinRarStatus = FieldStatus.Error("This WinRAR executable does not exist.");
            return;
        }

        try
        {
            string? version = System.Diagnostics.FileVersionInfo.GetVersionInfo(value).FileVersion;
            WinRarStatus = string.IsNullOrWhiteSpace(version)
                ? FieldStatus.Ok("WinRAR executable selected.")
                : FieldStatus.Ok($"WinRAR detected (v{version}).");
        }
        catch
        {
            WinRarStatus = FieldStatus.Ok("WinRAR executable selected.");
        }
    }
```

- [ ] **Step 3: Validate Release and Verify paths on change**

Add/extend these hooks:

```csharp
    partial void OnReleasePathChanged(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            ReleaseStatus = FieldStatus.None;
        }
        else if (!Directory.Exists(value) && !File.Exists(value))
        {
            ReleaseStatus = FieldStatus.Error("This path does not exist.");
        }
        else
        {
            ReleaseStatus = FieldStatus.Ok("Source files selected.");
        }
    }

    partial void OnVerificationPathChanged(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            VerifyStatus = FieldStatus.None;
        }
        else if (!File.Exists(value))
        {
            VerifyStatus = FieldStatus.Error("This .srr file does not exist.");
        }
        else
        {
            VerifyStatus = FieldStatus.Info("Reconstructed archives will be verified against this SRR.");
        }
    }
```

- [ ] **Step 4: Add captions + status lines to the four paths in the View**

In `ReconstructorView.xaml`, add to the root `<UserControl>`:

```xml
             xmlns:c="clr-namespace:ReScene.NET.Controls"
```

For each of the four path `DockPanel`s (WinRAR lines 50-57, Release 59-66, Verify 68-75, Output 77-84), insert a caption + status line immediately **after** the `DockPanel` and before the next path `DockPanel`. WinRAR:

```xml
    <TextBlock DockPanel.Dock="Top"
               Text="Path to WinRAR.exe / Rar.exe used to recompress. Older releases need older WinRAR versions."
               Foreground="{DynamicResource ForegroundSecondary}" FontSize="{DynamicResource FontSizeCaption}"
               TextWrapping="Wrap" Margin="84,0,0,2" />
    <c:FieldStatusLine DockPanel.Dock="Top" Status="{Binding WinRarStatus}" Margin="84,0,0,2" />
```

Release:

```xml
    <TextBlock DockPanel.Dock="Top"
               Text="Folder with the extracted original files to recompress into RAR archives."
               Foreground="{DynamicResource ForegroundSecondary}" FontSize="{DynamicResource FontSizeCaption}"
               TextWrapping="Wrap" Margin="84,0,0,2" />
    <c:FieldStatusLine DockPanel.Dock="Top" Status="{Binding ReleaseStatus}" Margin="84,0,0,2" />
```

Verify:

```xml
    <TextBlock DockPanel.Dock="Top"
               Text="The .srr file to verify reconstructed archives against."
               Foreground="{DynamicResource ForegroundSecondary}" FontSize="{DynamicResource FontSizeCaption}"
               TextWrapping="Wrap" Margin="84,0,0,2" />
    <c:FieldStatusLine DockPanel.Dock="Top" Status="{Binding VerifyStatus}" Margin="84,0,0,2" />
```

Output:

```xml
    <TextBlock DockPanel.Dock="Top"
               Text="Folder where reconstructed RAR archives are written."
               Foreground="{DynamicResource ForegroundSecondary}" FontSize="{DynamicResource FontSizeCaption}"
               TextWrapping="Wrap" Margin="84,0,0,2" />
```

> The `84` left margin aligns the caption under the textbox (the labels in this form use `Width="80"` plus spacing). Each of these path rows is a `DockPanel DockPanel.Dock="Top"`; insert the new elements directly after the closing `</DockPanel>` of the corresponding row.

- [ ] **Step 5: Add a "recommended first step" note above Import from SRR**

In `ReconstructorView.xaml`, immediately before the `DockPanel` containing the `Import from SRR` button (the `<DockPanel DockPanel.Dock="Top" Margin="0,0,0,2">` at lines 124-130), insert:

```xml
    <TextBlock DockPanel.Dock="Top"
               Text="Tip: click “Import from SRR” to auto-configure versions, compression, dictionary, timestamps and Host OS from the release's SRR."
               Foreground="{DynamicResource ForegroundSecondary}" FontSize="{DynamicResource FontSizeCaption}"
               TextWrapping="Wrap" Margin="0,0,0,2" />
```

- [ ] **Step 6: Build to verify**

Run: `dotnet build ReScene.NET/ReScene.NET.csproj`
Expected: build succeeds.

- [ ] **Step 7: Commit**

```bash
git add ReScene.NET/ViewModels/ReconstructorViewModel.cs ReScene.NET/Views/ReconstructorView.xaml
git commit -m "feat(rar-reconstructor): add path captions and validation status"
```

---

## Phase G — Verification

### Task 12: Full build + test sweep

- [ ] **Step 1: Run the whole test suite**

Run: `dotnet test ReScene.NET.slnx`
Expected: all tests pass (existing lib tests + new `ReScene.NET.Tests`).

- [ ] **Step 2: Build the app in Release to catch XAML/binding issues**

Run: `dotnet build ReScene.NET/ReScene.NET.csproj -c Release`
Expected: 0 errors.

- [ ] **Step 3: Manual smoke pass (each form)**

Launch the app. For each of the five forms, pick an input and confirm: captions are visible, the ✓/ℹ/⚠/✗ status line updates, output auto-fills only when empty, and the action button hint (SRR Creator) reads correctly. Confirm no binding errors appear in the debug output.

- [ ] **Step 4: Commit any fixes**

```bash
git add -A
git commit -m "fix: address issues found during explain-and-assist verification"
```

(Skip if there were no fixes.)

---

## Self-Review notes

- **Spec coverage:** captions (all 5 forms — Tasks 6, 8, 9, 10, 11); status feedback (`FieldStatusLine` + per-form statuses); assist/detect (RAR volume count T5, sample description T8, SRS info + media sanity T9, match summary T10, WinRAR version T11); auto-fill empty-only (T5, T8, T9, T10); action gating hint (T7); reusable mechanism (T2–T4); testing (T1–T3, T12). All spec sections map to tasks.
- **Type consistency:** `FieldStatus`/`FieldState` defined in T2 and used unchanged in T3, T4, and all VM tasks. `FieldStatusLine.Status` (DP) bound consistently as `Status="{Binding XxxStatus}"`. `FieldGuidance` method names (`SuggestSiblingPath`, `EvaluateMediaAgainstSample`, `DescribeSample`, `CountReleaseArchives`) defined in T3 and called consistently.
- **Assumptions to verify during execution (flagged in-task):** exact ViewModel property names for SRS Creator / SRS Reconstructor / RAR Reconstructor, and the `SRSFile` sample name/size accessor names. Each such task says to confirm against the actual file before editing; the bound view properties were verified against the XAML.
