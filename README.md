# Unidot

**Build and run C# code for an already-built Unity Player, just like `dotnet`.**

[![Release](https://img.shields.io/github/v/release/CortexCore/Net.BITKit.Unidot?include_prereleases&style=flat-square)](https://github.com/CortexCore/Net.BITKit.Unidot/releases)
[![Build](https://img.shields.io/github/actions/workflow/status/CortexCore/Net.BITKit.Unidot/ci.yml?branch=main&style=flat-square)](https://github.com/CortexCore/Net.BITKit.Unidot/actions/workflows/ci.yml)
[![License](https://img.shields.io/github/license/CortexCore/Net.BITKit.Unidot?style=flat-square)](LICENSE)
[![.NET](https://img.shields.io/badge/.NET-8-512BD4?logo=dotnet&style=flat-square)](https://dotnet.microsoft.com/download/dotnet/8.0)
[![Platform](https://img.shields.io/badge/platform-Windows_x64-0078D4?style=flat-square)](#requirements)
[![Unity](https://img.shields.io/badge/Unity-Mono-222222?logo=unity&style=flat-square)](#requirements)

**English** · [简体中文](README.zh-CN.md) · [User guide](docs/guide.md) · [Releases](https://github.com/CortexCore/Net.BITKit.Unidot/releases)

Edit your code, run `unidot run`, and reuse your existing game build. Unidot compiles the assemblies, runs configured IL postprocessors, backs up and replaces the DLLs, then launches the Player with live logs—without reopening Unity Editor or rebuilding the entire Player.

```powershell
unidot run
```

## Quick start

### 1. Install

Download `Net.BITKit.Unidot.0.3.0.nupkg` from the [v0.3.0 Alpha release](https://github.com/CortexCore/Net.BITKit.Unidot/releases/tag/v0.3.0) into a local `packages` directory, then install:

```powershell
dotnet tool install --global Net.BITKit.Unidot --version 0.3.0 --add-source ./packages
```

Or extract `Unidot-v0.3.0-windows.zip` from the same release and add its directory to `PATH`. The ZIP requires .NET 8 Runtime; .NET Tool installation requires the SDK. Packages are distributed through GitHub Releases, not currently through nuget.org.

### 2. Link your source

From your built Player's directory:

```powershell
unidot link --source "D:\Games\MyGame.Unity\Assets\Artists\Scripts"
```

```text
MyGame.Build/
├─ MyGame.exe
├─ MyGame_Data/Managed/
└─ Src/Artists/Scripts/ → Unity Assets/Artists/Scripts
```

The default source directory is **`Src`**. Sources stay linked to the original files; `--src <directory>` selects a different location.

### 3. Run

```powershell
unidot run
```

The first run infers Unity project metadata from the link and saves `unidot.json`. Later runs reuse that configuration and incremental build cache. Close the game before rebuilding its DLLs.

```text
Source → Unity Roslyn → ILPP → Backup & deploy → Unity Player
                                                   ↓
                                            Live console logs
```

**Ctrl+C closes this Player.** When the game exits, Unidot finishes the log and returns its exit code. Logs are also saved under `.unidot/logs/`.

## Everyday commands

| Command | What it does |
|---|---|
| `unidot run` | Build, postprocess, deploy, launch, and follow logs |
| `unidot run --no-build` | Launch existing DLLs and follow logs |
| `unidot run --log-file logs/session.log` | Use a specific persistent log file |
| `unidot build` | Build and deploy without launching |
| `unidot build Game.Unity --no-deploy` | Compile selected code without replacing Player files |
| `unidot generate` | Generate the product-named solution and IDE Launcher |
| `unidot watch --no-deploy` | Rebuild source changes while keeping Player files untouched |
| `unidot restore` | Restore a verified deployment backup |

Player arguments go after `--`, for example `unidot run -- --mode=no-init`. See `unidot --help` for all options.

## IDE playback

```powershell
unidot generate
```

Open `.unidot/<Product Name>.sln`, select **Launcher**, and press Play in Rider or Visual Studio. Launcher calls `unidot run`; the IDE console receives build output and Unity logs. Stopping Launcher ends its Player session.

## Requirements

- **Windows x64, Mono Player.** DLL replacement is not supported for IL2CPP.
- An existing Unity Player and a matching Unity installation for its compiler/toolchain.
- .NET 8 Runtime for the ZIP, or .NET 8+ SDK for .NET Tool installation, development, and Launcher.
- Compiled ILPP plugins when your assemblies require them. Unidot executes the original processors; it does not generate Burst AOT native libraries.

**Alpha:** validated with Unity 2022.3.62f3 and 73 game-side assemblies, including ILPP, whole-set deployment, and Player startup. Assets, scenes, new assembly registration, and cross-project compatibility remain outside that validation.

## More

- [English user guide](docs/guide.md) / [中文使用指南](docs/guide.zh-CN.md)
- [Configuration example](unidot.example.json)
- [Changelog](CHANGELOG.md)
- [MIT license](LICENSE)

```powershell
dotnet build Unidot.sln
dotnet run --project tests/Unidot.Tests
```
