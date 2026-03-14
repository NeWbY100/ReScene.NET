# ReScene.NET

A Windows desktop application for inspecting, creating, and reconstructing [ReScene](https://rescene.wikidot.com/) (SRR/SRS) files, built with WPF and .NET 10.

![Home](docs/resources/home.png)

## Features

### Inspector

Explore the internal block structure of `.srr`, `.srs`, and `.rar` files with a tree view, property grid, and hex viewer. Export individual blocks or stored files directly from the UI.

![Inspector](docs/resources/inspector.png)

### SRR Creator

Create `.srr` files from RAR archives (single or multi-volume) or SFV manifests, with support for stored files, OSO hashes, and app name tagging.

![SRR Creator](docs/resources/srr_creator.png)

### SRS Creator

Create `.srs` sample reconstruction files from media files across 7 container formats: AVI, MKV, MP4, WMV/ASF, M4V, FLAC, MP3, VOB, M2TS, TS, MPG, MPEG, and EVO.

![SRS Creator](docs/resources/srs_creator.png)

### RAR Reconstructor

Reconstruct RAR archives from SRR metadata using brute-force WinRAR version and parameter discovery. Supports two-phase matching (comment block filtering + full RAR creation), host OS and attribute patching, LARGE flag patching, custom packer detection, SRR import with automatic setting configuration, file copy/verify with timing stats, and rename-to-original output.

![RAR Reconstructor](docs/resources/rar_reconstructor.png)

### Compare

Side-by-side comparison of SRR and RAR files with dual tree views, property grids, hex viewer, and difference highlighting. Differing fields and tree nodes are highlighted in red for quick identification.

![Compare](docs/resources/compare.png)

## Requirements

- [.NET 10.0](https://dotnet.microsoft.com/download/dotnet/10.0)
- Windows (WPF)

## Getting Started

Clone with submodules:

```bash
git clone --recurse-submodules https://github.com/prijkes/ReScene.NET.git
```

If already cloned without submodules:

```bash
git submodule update --init --recursive
```

Build and run:

```bash
dotnet build
dotnet run --project ReScene.NET
```

## Project Structure

```
ReScene.NET/
├── ReScene.NET/            # WPF desktop application (.NET 10)
│   ├── Views/              # XAML views
│   ├── ViewModels/         # MVVM view models (CommunityToolkit.Mvvm)
│   ├── Models/             # Data models
│   ├── Services/           # Business logic (SRR/SRS creation, brute-force, compare)
│   ├── Controls/           # Custom controls (HexViewControl)
│   ├── Helpers/            # Utilities (dark title bar, etc.)
│   └── Resources/          # Themes, design tokens, icons
└── ReScene.Lib/            # Git submodule — shared libraries
    ├── RARLib/             # RAR 4.x/5.x header parsing, patching, decompression
    ├── SRRLib/             # SRR/SRS file format reading and writing
    ├── ReScene.Core/       # Reconstruction, comparison, brute-force orchestration
    ├── RARLib.Tests/       # xUnit tests for RARLib
    └── SRRLib.Tests/       # xUnit tests for SRRLib
```

## Libraries

### RARLib

Low-level RAR archive header parsing and patching. Supports RAR 4.x and RAR 5.x block structures, file metadata extraction (names, sizes, CRCs, timestamps), comment decompression, in-place header patching with CRC recalculation, and custom scene packer detection.

### SRRLib

SRR and SRS file format support. Reads and writes SRR files (scene release reconstruction metadata) and SRS files (sample reconstruction data) across 7 container formats: RIFF (AVI), MKV, MP4, WMV/ASF, FLAC, MP3, and Stream/M2TS.

### ReScene.Core

High-level reconstruction and comparison logic. Orchestrates brute-force WinRAR version discovery, RAR archive reconstruction from SRR metadata, host OS / attribute / LARGE flag patching, and file-level comparison between SRR/RAR archives.

## Dependencies

| Package | Version | Project |
|---|---|---|
| [CommunityToolkit.Mvvm](https://www.nuget.org/packages/CommunityToolkit.Mvvm) | 8.4.0 | ReScene.NET |
| [Crc32.NET](https://www.nuget.org/packages/Crc32.NET) | 1.2.0 | RARLib, ReScene.Core |
| [System.IO.Hashing](https://www.nuget.org/packages/System.IO.Hashing) | 9.0.4 | SRRLib |
| [CliWrap](https://www.nuget.org/packages/CliWrap) | 3.10.0 | ReScene.Core |
| [ReScene.Lib](https://github.com/prijkes/ReScene.Lib) | submodule | — |

## License

See [LICENSE](LICENSE) for details.
