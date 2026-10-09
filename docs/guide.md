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

## Lightweight agent workspaces

From the directory containing your base Build:

```powershell
unidot workspace create agent-a --base ./Build
unidot workspace list
unidot workspace diff agent-a
unidot workspace remove agent-a
```

The default container is `./Workspaces` relative to the command's current directory. Use `--root <directory>` consistently to select another container. The destination must be outside the base Player and all copied source trees. The base Player may remain running: creation only reads/copies it. The base Unidot build/deployment lock must be available; actual unreadable or changing Managed inputs cause an actionable failure and cleanup, rather than closing the Player.

Creation reads the base Player's `unidot.json`, or infers a binding from its Src link if no configuration exists; it does not save a new base configuration. Use `--config <file>` for a binding stored elsewhere, `--src <directory>` to select source explicitly, and `--unity-editor <installation>` to override the compiler installation. It creates the new directory directly, with no full-resource copy or intermediate Player clone:

```text
Workspaces/agent-a/
  Src/                           real source snapshot
  Game.exe                       private executable copy
  UnityPlayer.dll                file link to base runtime
  MonoBleedingEdge/               directory link to base runtime
  Game_Data/                     real container directory
    Managed/                     complete private DLL/PDB/file copies
    app.info                     private small metadata copy
    ScriptingAssemblies.json     private registration copy, when present
    StreamingAssets/             directory link to base resources
    sharedassets*.assets/.resS    file links to base resources
  unidot.json
  AGENTS.md
  .unidot/workspace.json          manifest and initial source hashes
```

Src links are followed into actual files when taking the snapshot; source assembly definitions, references and `.meta` files are preserved. Source `.git`, `.unidot`, `bin` and `obj` directories are omitted. Sources/Managed use copies rather than hard links. Existing generated projects, caches, logs and agent instructions are not inherited from the base; the new workspace owns its own generated files, backups, locks and instructions.

Original source subtrees are excluded from assembly discovery to avoid duplicate definitions and accidentally compiling the original code. Package/toolchain metadata continues to come from the original Unity project. Build roots are rebased onto the snapshot; the global `Assets/csc.rsp` is copied separately. A copied assembly that still owns external source through an asmref is refused: expand the base Src mapping to include that source first. Do not replace snapshot source files/directories with links to the original.

From inside the created directory, normal commands work:

```powershell
unidot generate
unidot build --no-deploy
unidot build
unidot run
```

Only this workspace's Managed is deployed. `run` remains a persistent foreground session. Shared save locations, network ports and other game-specific external state are not isolated by directory creation.

File/directory linking is probed before copying Src or Managed. On Windows, directories can fall back to junctions, but individual resource files require symbolic-link privilege (Developer Mode or an appropriately privileged account). A failed resource link never falls back to copying large data; partial creation/cancellation is cleaned up by unlinking entries, not traversing resource targets. Creation refuses existing destinations and overlapping paths.

Resources and runtime files are shared, not permission-read-only. Keep the base Build and external link targets available. Before Player commands, Unidot validates link destinations and base file/directory size/timestamp metadata; detected resource changes require recreating the workspace. This is a compatibility check, not a filesystem sandbox or content-hash verification of every large resource.

Creation reports copied and shared payload bytes. `list` also reports current private file bytes, including build caches/logs/backups, without following links. These are logical file sizes, not filesystem allocated/compressed sizes. `diff` lists added/modified/deleted Src paths against initial SHA-256 hashes: it stores no second full source copy, applies no changes, and is not a line-by-line patch. `remove` discards the named workspace after verifying manifest ownership and checking active build/session locks; even broken resource links are removed without deleting their targets.

### Source return and conflict resolution

Run from the directory containing `Workspaces`, or specify its container with `--root`:

```powershell
unidot workspace apply agent-a --dry-run
unidot workspace apply agent-a
# If a file conflicts: edit the final result in Workspaces/agent-a/Src first.
unidot workspace resolve agent-a Artists/Scripts/Foo.cs
unidot workspace apply agent-a
```

For each candidate, apply compares baseline **B** (initial snapshot or last successful apply), workspace **W**, and main/original source **M**:

| Condition | Action |
|---|---|
| W equals B | Preserve main; the agent has no pending change |
| W equals M | Already matched; advance the merge baseline without rewriting source |
| Only W changed, M still equals B | Apply the add/modify/delete |
| Both changed to different contents | Conflict; apply nothing in the batch |

Absence is a state too: differing additions, modified-versus-deleted files and deletion of a modified main file require resolution. `resolve` records the exact physical target, B/M/W hashes and confirmation time. It does **not** write main. Either M or W changing makes that confirmation stale; resolve again after preparing the new result. Choosing main means copying its current contents into the workspace, so they match; an explicit resolved deletion means the workspace file remains absent. Recognized conflict markers must be removed before confirmation.

Conflicts return a nonzero exit code and print a bounded main/workspace diff with line context, computed by **DiffPlex**. Full diagnostics are `.unidot/merge-conflicts.diff` and `.unidot/merge-report.json`. Text decoding supports UTF-8 and BOM-marked UTF-16/32; binary/unsupported files and text over 1 MiB are summarized by hashes. These views are diagnostic, not patches or automatic three-way text merges. Initial file contents are not duplicated just to produce the diff.

**Resolved** means an agent prepared and confirmed a result. **Applied** means source writes and hash verification succeeded and the separate `.unidot/workspace-apply.json` baseline advanced. Original `.unidot/workspace.json` creation hashes remain intact, so `workspace diff` still shows changes relative to creation, while subsequent apply compares against the last successful merge. Apply itself does not establish that code builds or behaves correctly; use normal source validation afterward.

Apply visits Src changes and metadata only. `.git`, `.unidot`, `bin`, `obj`, and DLL/PDB/EXE/NuGet build binaries are excluded; Managed/resource/log files are never returned. File bytes, BOM and line endings are copied without text rewriting. New manifests freeze physical source roots so retargeting the old Player-side Src link cannot redirect return; legacy 0.5 workspaces resolve through their recorded original Src/exclusion mapping and refuse targets outside those roots.

`--dry-run` may update the local diagnostic report but never source or merge state. Actual apply locks workspace management/build activity and serializes overlapping Unidot source updates, preflights all candidates, keeps backups/journals under `.unidot/apply-backups/`, stages results, and rechecks files before and after writes. Failed/cancelled writes roll back; if an external editor changes an already-written file before rollback, its edit is preserved and the command reports manual recovery with the backup path. These checks do not lock out arbitrary external editors or provide crash-atomic multi-file filesystem transactions. There is no force-overwrite option.

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
AGENTS.md
<Product Name>.Unidot.sln
.run/Launcher.run.xml
.unidot/
  Launcher/Launcher.csproj
  projects/<assembly>/<assembly>.csproj
  bin/<assembly>/<assembly>.dll + .pdb
  graph.json
  state/
  rsp/
  staging/
  backups/
  logs/
```

The product name comes from `Player_Data/app.info`, falls back to the executable's name, and can be overridden with `productName` in configuration. The solution and shared `.run` profile live beside `unidot.json`; generated projects, binaries, caches, logs, and backups remain in `.unidot`. Existing user-owned solutions or run configurations are preserved.

Unity 2022's Windows overwrite checks interpret a root `<Product Name>.sln` or `UnityCommon.props` as an earlier native "Create Visual Studio Solution" build. The `.Unidot.sln` name avoids this collision. A previous Unidot-generated conflicting solution is moved into `.unidot/legacy-solutions/`; user/native solutions are never moved automatically and may still require choosing a different Unity build directory.

### Agent instructions

Unidot's repository `AGENTS.md` describes development of the CLI and its regression tests. The separate `templates/player-AGENTS.md` is embedded in the tool and describes game-code work in a built Player.

Initialization, first automatic binding, and `generate` create/update the Player root's instructions. `build`, `run`, and `watch` check them after binding the configuration. Commands do not write instructions into arbitrary current subdirectories. Help/version remain read-only.

Only the section between `<!-- unidot:agents:start -->` and `<!-- unidot:agents:end -->` is maintained. Existing user sections, encoding/BOM, and line endings remain intact; unchanged instructions are not rewritten. Malformed markers, unsupported encoding, or externally linked AGENTS files are preserved with a diagnostic.

The Player instructions direct agents to linked source and Unidot commands instead of scaffolding ad-hoc game test projects in the built output. They do not restrict tool-side regression tests in the Unidot repository, and are guidance rather than a permission sandbox.

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

Unity writes a `-logFile`, and Unidot incrementally follows its UTF-8 content. Full native stdout/stderr are saved separately to `<logFile>.stdout.log` and `<logFile>.stderr.log`. `--log-file` is relative to the terminal's current directory or can be absolute. Final lines without a newline are drained on exit.

Default console output shows startup/exit status, native stderr, and recognized Unity/stdout errors with stack traces. Ordinary runtime messages stay in the logs. Use `unidot run --verbose` for full live output; verbosity never changes what is saved. Unity text logs do not always have severity markers, so recognition is best-effort and the full log remains the authoritative diagnostic source.

- Normal game exit ends the CLI session and propagates the exit code.
- Ctrl+C requests window closure, waits up to five seconds, then cleans up this process tree; the command returns 130.
- Launcher exit cancels the CLI lifetime, including a pending build.
- On Windows, a kill-on-close Job Object cleans up the owned Player if the CLI is force-stopped.

Only this session's process is managed; other game processes are not closed by name. Launcher is a startup/logging entry, not a Mono breakpoint debugger.

## Optional native host (experimental)

Standard EXE launch remains the default. The Windows x64 distribution also contains a thin host for Unity 2022.3's `UnityMain2` entry:

```powershell
unidot run --backend native --fallback-exe
unidot run --keep-alive
```

The native host uses your existing `UnityPlayer.dll`, Mono runtime and data directory; Unity still owns engine initialization. A pre-entry startup failure can fall back to the original EXE when explicitly enabled and available. After engine entry, no automatic second launch is attempted. Startup status is written beside the Player log as `.native.json`; fallback logs use `.fallback.log`. This opt-in path was locally exercised on Unity 2022.3.62f3 and is not a cross-version native embedding API.

`--keep-alive` keeps the CLI available after exit and accepts `restart`/`r` or `quit`/`q`. Restart creates a new Player process and performs the normal build/deploy preparation unless `--no-build` is set; it is not in-process engine restart or running-DLL hot reload.

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

Paths are relative to the Unity project. Unidot loads compiled processor DLLs in a separate CLI worker and calls their original `WillProcess` / `Process` implementations in configured order. By default, the worker is reused within one build, with fresh processor instances for each assembly. It validates diagnostics and DLL/PDB outputs; processor failures prevent deployment.

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

## Build performance

The source build defaults are:

```json
{
  "sharedCompilation": true,
  "reuseIlppHost": true
}
```

- Shared compilation passes `/shared` to Unity's Roslyn compiler. Roslyn manages the compiler server and its idle lifetime; a small compiler client still runs for each assembly. The first request may pay server startup cost.
- ILPP starts a separate worker only when an assembly needs processing. One build reuses the loaded plugin assemblies, but creates a fresh processor instance for each request. After processing, the worker exits before deployment so plugin/Cecil reference handles cannot lock Player DLLs. Failures/cancellation also clean it up. All-cache builds start no ILPP worker.
- Plugin static state can persist across assemblies within that build. For a processor requiring process-level isolation, set `reuseIlppHost` to `false` or use `--isolated-ilpp`.
- A build lock serializes the workspace. Stable `.unidot/staging/current` paths keep deterministic compiler outputs stable between forced rebuilds; staged files are cleaned after the build.

Compare the execution strategies with the same targets and force recompilation:

```powershell
unidot build Game.Core Game.Unity --no-deploy --force
unidot build Game.Core Game.Unity --no-deploy --force --no-shared --isolated-ilpp
```

Both overrides also work with `run` and `watch`; they apply to that invocation without changing saved configuration. Changing either mode invalidates the input fingerprint. `--force` ensures repeated comparisons still compile instead of using cached outputs. Compiler hosts may select different runtime patch versions recorded in the PDB, so byte-for-byte equality across modes is not guaranteed. External `postProcessors` keep their existing per-assembly process model.

With `--verbose`, each compiled assembly prints `[time]` compiler and Unity ILPP durations. Timings are always recorded in the build journal and `.unidot/build-report.json`, which includes `elapsedMs` and `timings` entries with `cached`, `fingerprintMs`, `compileMs`, `ilppMs`, and `totalMs`. Compiler/ILPP times include client communication and worker startup where applicable. Assembly totals include other work such as output validation and external processors; build elapsed time starts after discovery and toolchain selection.

On the local Unity 2022.3.62f3 project, three forced targets took **3.97s isolated versus 1.47s with reuse** (whole CLI invocation: 4.83s versus 2.38s). A successful 73-assembly forced build took **16.49s**, or 17.68s including CLI startup/discovery. These are local measurements, not general performance guarantees. A later full isolated comparison stopped at a missing `RelayRoomMetadata` source dependency and is not a valid whole-build comparison.

## Console output and diagnostics

```powershell
unidot build --no-deploy
unidot build --no-deploy --verbose
unidot run --verbose
unidot watch --no-deploy --verbose
```

`build/run/watch` default to concise progress and final results. Compiler/discovery warnings, per-assembly cache/timing details and ILPP skip messages are retained in logs instead of repeated in the console. Compiler/ILPP errors remain visible; tool failures without a recognized error diagnostic show a bounded output tail. Builds summarize compiled/cached/failed targets, current-invocation warning counts and ILPP processing results, then print the log directory. Cached targets do not replay old warnings.

`.unidot/logs/build-*/` contains `build.log` (stages, discovery diagnostics and full errors), `<assembly>.log` (raw compiler output), `<assembly>.ilpp.log`, `ilpp-worker.stderr.log` when applicable, and external processor logs. `.unidot/build-report.json` records status, warning counts, timings, failures and log location, including failed/cancelled builds. `--verbose` changes display only, not saved configuration, compilation inputs or cache identity.

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
