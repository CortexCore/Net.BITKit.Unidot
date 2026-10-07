# Unidot user guide

[English README](../README.md) · [中文使用指南](guide.zh-CN.md)

Unidot is an independent C# build, deployment, and runtime CLI for existing Mono Unity Players. It reads Unity assembly definitions, invokes Unity's Roslyn compiler, processes DLL/PDB outputs, and replaces game code in an existing Player.

## Development workflow

Run commands from the built Player's directory:

```powershell
unidot link --source "D:\Games\MyGame.Unity\Assets\Artists\Scripts"
unidot run
```

The default link destination is `Src/Artists/Scripts`, and the default source directory is `Src`. Directory symlinks are used when available; Windows falls back to a junction when symbolic-link privilege is unavailable. Existing unrelated destinations are never overwritten.

On the first run, Unidot follows the source link to infer the Unity project's metadata, locates a matching compiler, binds the Player, and writes `unidot.json`. Runtime source assemblies under `Src` become build targets. The rest of the Unity project remains available for dependency and package metadata discovery without becoming a source build target.

The automatic setup selects the validated NetRpc, Jobs, and Burst ILPP DLLs if they exist. It does not discover every custom processor. Explicit setup is available when inference or those defaults do not fit:

```powershell
unidot init --project "D:\Games\MyGame.Unity" --player "D:\Games\MyGame.Build\MyGame.exe" --src Src
```

All commands accept `--config <file>`. Without it, configuration is located by walking up from the current directory. See [unidot.example.json](../unidot.example.json).

## Toolchain

Set `UNIDOT_UNITY_EDITOR`, or its compatible alias `UNITY_EDITOR`, to a Unity executable, Editor directory, Editor/Data directory, or version installation:

```powershell
$env:UNIDOT_UNITY_EDITOR = "C:\Program Files\Unity\Hub\Editor\2022.3.62f3\Editor"
```

Lookup priority is `--unity-editor`, saved `unityEditor`, `UNIDOT_UNITY_EDITOR`, `UNITY_EDITOR`, then the default Unity Hub directory for the project's version. Initialization saves the selected path.

Compilation uses:

```text
Editor/Data/NetCoreRuntime/dotnet.exe
  exec Editor/Data/DotNetSdkRoslyn/csc.dll
```

The compiler host's .NET version is separate from the game's target API. Game assemblies reference the Player's runtime libraries rather than .NET 8 application libraries. The Unity installation must match the project; no running Editor is required.

The default API profile is `unity-4.8`. Use `--api-profile netstandard2.1` for that Player compatibility profile, and `--development` for a Development Player. These choices must match the base build.

## Source and build scopes

- `sourceDirectory`: Player-side source directory, normally `Src`.
- `sourceRoots`: extra discovery locations.
- `buildRoots`: directories whose assemblies may be compiled from source, relative to the Unity project or absolute.
- `assemblies`: an optional narrower set of default targets.

For direct Unity-project discovery without a Player-side link:

```json
{
  "assemblies": [],
  "buildRoots": ["Assets/Artists/Scripts"],
  "referenceMode": "player"
}
```

Discovery and build selection are separate. Dependencies outside the selected scope remain binary Player references. An explicit Player source directory limits builds to its source-owned assembly definitions. Registry/git cached packages are not rebuilt unless explicitly selected without a conflicting build scope.

### Reference modes

`asmdef` is the default strict mode. Unresolved GUIDs and required missing runtime references prevent project generation or compilation.

`player` mode uses available declared runtime references, current auto-referenced plugin DLLs, and framework libraries. When a target contains unresolved GUIDs, its existing Player DLL can supply actual historical assembly dependencies. Optional or excluded declarations remain diagnostics, while the compiler determines whether a required type is missing.

This mode does not unconditionally reference every game DLL: unrelated namespaces and legacy contracts can shadow the intended types. It checks discovered DLLs and `isExplicitlyReferenced` / RoslynAnalyzer metadata, but does not fully reproduce Unity PluginImporter semantics.

## Commands

```powershell
unidot graph Game.Unity
unidot generate
unidot build
unidot build Game.Unity --no-deploy
unidot build Game.Core Game.Unity --no-dependencies --no-deploy
unidot run
unidot run --no-build --log-file logs/session.log
unidot run -- --mode=no-init
unidot watch --no-deploy
unidot restore
unidot restore --backup ".unidot/backups/<batch>/manifest.json"
unidot audit
```

`build` follows source dependency order and deploys by default. `--force` bypasses the incremental cache. `--no-dependencies` avoids expanding the selected source targets; Player mode can still use already-successful declared targets from the same invocation.

`--keep-going` requires `--no-deploy`. It compiles independent targets after a failure, marks consumers of failed source dependencies as blocked when dependency traversal is enabled, and returns a nonzero exit code. Results go to `.unidot/build-report.json`; compiler output goes to `.unidot/logs/build-*/`.

`watch` debounces source/assembly changes, reloads configuration, and rebuilds. Default deployment requires the Player to be closed; use `--no-deploy` while a game is running.

`audit` checks the previous successful build's Player files, registration, and assembly identity, producing `.unidot/deployment-audit.json`.

## IDE and generated files

```text
unidot.json
.unidot/
  <Product Name>.sln
  Launcher/Launcher.csproj
  .run/Launcher.run.xml
  projects/<assembly>/<assembly>.csproj
  bin/<assembly>/<assembly>.dll + .pdb
  graph.json
  state/
  rsp/
  staging/
  backups/
  logs/
```

The product name comes from `Player_Data/app.info`, falls back to the executable's name, and can be overridden with `productName` in configuration.

Open the solution and choose **Launcher**. Rider receives a shared run configuration; Visual Studio receives `launchSettings.json`. Existing IDE user settings may require setting Launcher as the startup project once.

Launcher is a separate .NET 8 executable with no game project references. Default solution Build selects Launcher only, so IDE Play does not first overwrite game artifacts with a separate raw compilation. The installed CLI owns compilation, ILPP, deployment, and the Player session.

Generated library projects link original source files and explicitly define references, Player symbols, and `NoStdLib`. They include source `ProjectReference` edges and stable GUIDs; unchanged files are not rewritten.

You can build a generated project directly:

```powershell
dotnet msbuild .unidot/projects/Game.Core/Game.Core.csproj /t:Build
dotnet msbuild .unidot/projects/Game.Unity/Game.Unity.csproj /t:Build /p:UnidotUsePlayerReferences=true
```

Direct MSBuild outputs are raw compiler results. `unidot build` owns postprocessing, cache state, and deployment. In strict mode, projects with unresolved references are recorded in `graph.json` and omitted from the solution.

## Foreground sessions and logs

`run` builds, processes, deploys, and launches; `--no-build` skips the build but still waits and follows logs. Build or deployment errors prevent launch.

Unity writes a `-logFile`, and Unidot incrementally follows its UTF-8 content while forwarding native stdout/stderr. `--log-file` is relative to the terminal's current directory or can be absolute. Final lines without a newline are drained on exit.

- Normal game exit ends the CLI session and propagates the exit code.
- Ctrl+C requests window closure, waits up to five seconds, then cleans up this process tree; the command returns 130.
- Launcher exit cancels the CLI lifetime, including a pending build.
- On Windows, a kill-on-close Job Object cleans up the owned Player if the CLI is force-stopped.

Only this session's process is managed; other game processes are not closed by name. Launcher is a startup/logging entry, not a Mono breakpoint debugger.

## Compilation and ILPP

```text
Source + Source Generator
  → Unity Roslyn
  → staged DLL/PDB
  → configured IL postprocessing
  → validated output
  → backup and deployment
```

Unity's generators are enabled by default. Additional generators/analyzers are configured with `analyzers`. Global/assembly `csc.rsp` and `compilerArguments` supplement compiler options; they cannot override managed output paths, references, or inject `UNITY_EDITOR`.

### Unity processor plugins

```json
{
  "unityIlppPlugins": [
    "Library/ScriptAssemblies/Unity.BITKit.Multiplayer.CodeGen.dll",
    "Library/ScriptAssemblies/Unity.Collections.CodeGen.dll",
    "Library/ScriptAssemblies/Unity.Burst.CodeGen.dll"
  ]
}
```

Paths are relative to the Unity project. Unidot loads compiled processor DLLs in a separate CLI process and calls their original `WillProcess` / `Process` implementations in configured order. It validates diagnostics and DLL/PDB outputs; processor failures prevent deployment.

These DLLs come from an existing Unity base build/script compilation. Preparing or upgrading them remains a prerequisite. The host uses Unity's `Unity.CompilationPipeline.Common` and project Cecil/Burst dependencies; it does not generate Burst AOT native libraries or update resources and initialization manifests.

Manual staged processing is available:

```powershell
unidot ilpp --dll path/to/Game.dll --pdb path/to/Game.pdb --processor path/to/CodeGen.dll
```

The default compiler input is `.unidot/rsp/<assembly>.rsp`; override it with `--rsp`.

Known Jobs/Burst/network weaving features are checked conservatively. Without configured processing, deployment is refused; `--no-deploy` can produce raw DLLs for inspection. This is not complete automatic analysis of all custom ILPP requirements.

### External processors

```json
{
  "postProcessors": {
    "Game.Network": [
      {
        "executable": "D:/Tools/MyIlProcessor.exe",
        "arguments": ["--assembly", "{dll}", "--symbols", "{pdb}", "--references", "{managed}"]
      }
    ]
  }
}
```

Supported placeholders are `{dll}`, `{pdb}`, `{assembly}`, `{project}`, and `{managed}`. Arguments are passed separately without a shell. Processors update staged DLL/PDB files in place and must exit with code 0. Failure preserves the previous successful output for that assembly and prevents deployment.

## Deployment boundaries

- The base Player must already contain and register the target assemblies.
- New assembly registration, assets, scenes, native plugins, Burst AOT, and runtime initialization manifest changes still require a Unity base build.
- The Player must be closed before DLL replacement. Deployment happens after the entire selected build succeeds.
- Assembly name/version/signing identity and SHA-256 hashes are checked; original DLL/PDB files are backed up.
- A failed batch rolls back written files. Restore verifies that deployed destinations and backups have not changed; newly introduced PDBs are removed when restoring.
- Sources, dependencies, compiler, generators, Unidot implementation, and processor configuration participate in caching. Modified final DLL/PDB outputs cause a rebuild.

## Build, test, and distribute

From the repository root, with .NET 8+ SDK:

```powershell
dotnet build Unidot.sln
dotnet run --project tests/Unidot.Tests
dotnet run --project tests/Unidot.Tests -- --unity-editor "C:\Program Files\Unity\Hub\Editor\2022.3.62f3\Editor"
dotnet publish src/Unidot -c Release -o artifacts/unidot
dotnet pack src/Unidot -c Release -o artifacts/packages
```

The default publish requires .NET 8 Runtime. A self-contained directory can be built with `-r win-x64 --self-contained true`.

Windows CI checks build, non-Unity tests, publishing, packaging, and installation from a local feed. The repository does not include Unity, game source, a Player, compiled third-party processors, or personal configuration.

Validated locally: Unity 2022.3.62f3; 73 game-side assemblies compiled, postprocessed, backed up, replaced, and launched. NetRpc rewrote five assemblies. Tests cover discovery, references, caching, processing failures, generated projects, UTF-8 logs, cancellation, parent lifetime, and Windows job cleanup. Cross-project behavior and actual networking/Burst features require their own verification; SourceTrace instrumentation and Mono breakpoint debugging are not part of this release.

See [CHANGELOG.md](../CHANGELOG.md) and the [MIT license](../LICENSE).
