# Working on Unidot

This repository contains the **Unidot CLI**, not a built game or its user scripts.

## Product direction

- Read [development intent](docs/development-intent.md) before expanding MCP, runtime execution, or hot-update capabilities.
- Prioritize a fast, reliable Agent source-edit-to-visible-result loop. Keep tool scope minimal; player recreation/hot-update architecture is a deferred candidate, not an MCP prerequisite.

## Code and generated files

- CLI implementation: `src/Unidot/`.
- Tool-side regression tests: `tests/Unidot.Tests/`.
- Player agent instructions: `templates/player-AGENTS.md`, embedded in the CLI.
- Keep local `unidot.json`, `.unidot/`, `artifacts/`, and game/Unity binaries out of source control.
- Preserve existing user changes. Prefer focused patches consistent with nearby code.

## Validation

```powershell
dotnet build Unidot.sln
dotnet run --project tests/Unidot.Tests
```

- Add meaningful tool-side tests for changes to parsing, generation, deployment, or process lifecycle.
- The optional Unity compiler integration test requires an explicit matching Unity installation.
- Use compiler-aware navigation and diagnostics when available, followed by a real build.
- Do not launch a game merely to validate documentation or configuration changes.
- Runtime verification must be explicitly requested and bounded, with a planned stop step; `unidot run` is intentionally a persistent foreground session.

## Agent instructions

- Repository instructions and generated Player instructions have different scopes. Do not put Player-only rules such as avoiding ad-hoc game test projects into this repository's test policy.
- Update only Unidot's managed section in an existing Player `AGENTS.md`; preserve user content, encoding, and line endings.
- `--help` and `--version` must not create workspace files.
- Do not commit, tag, push, or create GitHub releases unless requested.
