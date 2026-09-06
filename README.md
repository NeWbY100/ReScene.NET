# ReScene Manager

A cross-platform desktop application for inspecting, creating, and reconstructing [ReScene](https://rescene.wikidot.com/) (SRR/SRS) files, built with [Avalonia UI](https://avaloniaui.net/) and .NET 10. Runs on Windows and Linux (first-class) and macOS (best-effort builds).

*Formerly **ReScene.NET** (WPF, Windows-only) — renamed to avoid colliding with the original 2008 ReScene .NET tool that pyReScene was ported from.*

It runs in two modes, switchable any time from the **Mode** menu (or in Settings); the choice is remembered:

### Beginner

A guided home hub of task cards — each opens a focused, step-by-step wizard (Create an SRR, Create an SRS, Reconstruct RAR archives, Restore a sample, Edit an SRR).

![Beginner mode](docs/resources/beginner.png)

### Advanced

The full tabbed workbench, with every tool on its own tab.

![Advanced mode](docs/resources/advanced.png)

## Features

- **Inspect** the internal block structure of `.srr`, `.srs`, `.rar`, and `.mkv`/`.webm` files — tree view, property grid, and an integrated hex viewer, with block/stored-file export.
- **Create SRR** files from RAR archives (single or multi-volume) or SFV manifests, with stored-file curation and optional OSO hashes.
- **Create SRS** sample files across 7 container formats (AVI, MKV, MP4, WMV, FLAC, MP3, Stream/M2TS), including ISO/IMG input and optional track match offsets.
- **Reconstruct RAR archives** from SRR metadata via brute-force WinRAR version/parameter discovery, with header patching and rename-to-original output.
- **Rebuild and restore samples** from `.srs` (single) or an `.srr`'s embedded samples (batch), with CRC32 verification.
- **Compare** two RAR/SRR/SRS/MKV/WebM files side by side, with differences highlighted (down to byte-level cluster payloads for MKV/WebM).
- Drag & drop (or command-line) opens SRR/SRS/RAR/MKV files straight in the Inspector.

## Download

Self-contained single-file builds are published per release — no .NET runtime required:

| Platform | Asset |
|---|---|
| Windows x64 | `ReSceneManager-<version>-win-x64.zip` |
| Linux x64 | `ReSceneManager-<version>-linux-x64.tar.gz` |
| macOS x64 / Apple Silicon | `ReSceneManager-<version>-osx-x64.tar.gz` / `-osx-arm64.tar.gz` |

## Building from Source

Requires the [.NET 10.0 SDK](https://dotnet.microsoft.com/download/dotnet/10.0).

```bash
# Clone with submodules
git clone --recurse-submodules https://github.com/NeWbY100/ReScene.Manager.git
# (or, if already cloned) git submodule update --init --recursive

dotnet build
dotnet run --project ReScene.Manager
```

## Project Structure

```
ReScene.Manager/
├── ReScene.Manager/         # Avalonia desktop application (.NET 10): Views, platform services
├── ReScene.App.Core/        # Shared UI-framework-free core: ViewModels, services, models
├── ReScene.Cli/             # Command-line interface
└── ReScene.Lib/             # Git submodule — shared library (net8.0 + net10.0)
    ├── ReScene/             # RAR / SRR / SRS parsing & writing, Core reconstruction & compare
    └── ReScene.Tests/       # xUnit tests
```

(App tests live in `ReScene.App.Core.Tests/` and `ReScene.Manager.Tests/` — the latter runs headless Avalonia UI tests.)

`ReScene.Lib` is versioned and released independently (see its [repository](https://github.com/NeWbY100/ReScene.Lib)).

## Dependencies

| Package | Version | Project |
|---|---|---|
| [Avalonia](https://www.nuget.org/packages/Avalonia) (+ Desktop, Fluent, Inter) | 12.1.2 | ReScene.Manager |
| [Avalonia.Controls.DataGrid](https://www.nuget.org/packages/Avalonia.Controls.DataGrid) | 12.1.2 | ReScene.Manager |
| [CommunityToolkit.Mvvm](https://www.nuget.org/packages/CommunityToolkit.Mvvm) | 8.4.2 | ReScene.App.Core |
| [Crc32.NET](https://www.nuget.org/packages/Crc32.NET) | 1.2.0 | ReScene |
| [System.IO.Hashing](https://www.nuget.org/packages/System.IO.Hashing) | 9.0.4 | ReScene |
| [CliWrap](https://www.nuget.org/packages/CliWrap) | 3.10.0 | ReScene |
| [DiscUtils.Iso9660](https://www.nuget.org/packages/DiscUtils.Iso9660) / [.Udf](https://www.nuget.org/packages/DiscUtils.Udf) | 0.16.13 | ReScene |
| [ReScene.Lib](https://github.com/NeWbY100/ReScene.Lib) | submodule | — |

## License

See [LICENSE](LICENSE) for details.
