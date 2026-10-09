# Unidot Player workspace

This directory is an already-built Unity Mono Player with a C# development workspace. It is not the Unidot tool repository or a new .NET application.

## Workspace map

- Player: `{{PLAYER}}`.
- Configuration: `{{CONFIG}}`.
- User source: `{{SOURCE}}`.
- Generated projects, build output, logs, and backups: `{{GENERATED}}`.

`Src` and its children can be symbolic links or directory junctions. Editing linked files edits the original source repository. In a workspace created by `unidot workspace create`, `Src` is instead a private snapshot and `.unidot/workspace.json` records its origin; edits stay in that snapshot. Use the configured source path; do not copy scripts into the Player root or create a second source tree.

Created workspaces have private Managed DLL/PDB copies and shared resource links. Shared resources are not permission-read-only: do not modify them. Keep the base Build available; if it changes, recreate the workspace. `workspace diff` reports added/modified/deleted files against the initial source snapshot, without applying them to the original source.

To return source changes, run `workspace apply <name> --dry-run` from the workspace container's parent (or use `--root`), then `workspace apply <name>`. Conflicts block the whole batch and produce `.unidot/merge-conflicts.diff` plus `merge-report.json`. Prepare the merged result in the private Src, then `workspace resolve <name> <Src-relative-file>` and retry apply. Resolve is a hash-bound confirmation, not a source write; either file changing requires resolving again. Successful apply advances a separate merge baseline and keeps backups. Do not manually copy Managed/build outputs or bypass conflict checks with force overwrites. Validate the original source after return.

## Default workflow

```powershell
unidot build --no-deploy{{CONFIG_ARGUMENT}}
unidot generate{{CONFIG_ARGUMENT}}
unidot run{{CONFIG_ARGUMENT}}
```

- Use `build --no-deploy` for compilation checks. Successful compilation is not proof of gameplay behavior.
- Use `generate` after adding/changing assembly definitions. Generated `.csproj`, `.sln`, and Launcher files are owned by Unidot; edit source/configuration instead of patching them.
- `run` compiles, postprocesses, backs up, deploys, and starts the Player; it stays in the foreground. Default output is concise; add `--verbose` for full live compiler/ILPP/runtime logs. Complete logs are retained in either mode. The target Player must be closed before replacing its DLLs; creating a workspace snapshot from a running base Player is allowed when its files are readable and stable.
- Ctrl+C closes only the Player owned by that session. For automated runtime verification, plan a timeout or explicit stop step; do not wait indefinitely for a game to quit.
- Pass game arguments after `--`, for example `unidot run{{CONFIG_ARGUMENT}} -- --my arg`.

## Agent runtime MCP

- An Agent can launch `unidot mcp{{CONFIG_ARGUMENT}}` as a stdio server to own a new Unity 2022 Mono Player session. It starts the original EXE in the background and injects only that owned process. When no source binding exists, use `--player <Player directory> --unity-editor <matching installation>`.
- Tools are `runtime_status`, `compile_code`, `execute_compiled`, and `execute_code`. Source is a complete C# type with a public static parameterless entry (`Script.Run` by default), returning a value or Task. Calls run on the Unity main thread.
- stdout is protocol-only; logs use stderr/files. Closing the Agent's stdin stops its Player. Do not start a second manual Player or mix human-readable stdout with MCP traffic.
- Snippet compilation adds session-local assemblies; it does not replace existing game assemblies or run game ILPP. Use normal build/run for original source deployment. Execution timeouts do not forcibly interrupt user code already running.

## Testing policy

- Do not scaffold an ad-hoc `dotnet test` project, test runner, or test SDK in this built Player directory merely to validate a game-code change.
- Do not drop test `.cs` files into linked runtime source folders without considering their `asmdef` ownership: they can be compiled into the game and written back to the source repository.
- If a change genuinely needs unit tests, use the existing test project/assembly in the source repository. Unity EditMode/PlayMode tests belong to the original Unity project.
- Player validation uses the configured runtime, application behavior, logs, and exit codes. Report the exact scope verified; do not invent passing tests or treat a build as a runtime test.

## Preserve the base Player

- Do not hand-edit DLLs, PDBs, `Managed/`, scene/resource files, assembly-registration manifests, or Unidot backups to bypass build errors. Use Unidot's deployment and restore commands.
- Plugins outside the configured source build scope remain binary dependencies. Do not start rebuilding unrelated plugins or Unity itself to resolve an optional declaration diagnostic.
- Respect user instructions outside this generated section. These instructions guide agents; they are not a permission sandbox.
