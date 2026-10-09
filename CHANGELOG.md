# Changelog

## 0.6.1 — Unreleased

- **Fixed:** exit and await the reusable Unity ILPP worker before deployment. Cecil caches inside NetRpc processors can retain Player DLL read handles; deploying before worker disposal caused repeatable sharing violations even after the game closed.
- Keep the finally cleanup for failed/cancelled builds without double-disposing a completed host.
- Report completed compilation separately when only deployment fails; compiled outputs remain available for retry.
- Regression-test a processor retaining a Player DLL stream until host exit, successful deployment after release, and diagnostics for a genuine external lock.

## 0.6.0 — Alpha

GitHub release including the previously local-only 0.3.1–0.5.1 development milestones below.

- Add `workspace apply <name> [--dry-run]` and `workspace resolve <name> <Src-relative-file>` for Git-independent source return.
- Compare the creation/last-applied baseline, workspace contents and current original source. Any unresolved conflict blocks the entire batch; `--dry-run` writes a diagnostic report without changing source or merge baselines.
- Use DiffPlex 1.9.0 to render line-level main/workspace conflict views with context. Save full conflict/report files under `.unidot`; binary, unsupported and large text files use hash summaries.
- Bind explicit resolution to the exact target, baseline/main/result hashes; reject stale confirmations and textual conflict markers. Resolution prepares a result, while successful apply completes it.
- Back up changed source, preserve copied bytes/BOM/newlines, use sibling temporary-file replacements, recheck inputs, and roll back failed/cancelled batches without overwriting later external edits.
- Advance a separate per-file merge baseline after success or an already-matching result, while retaining the initial creation snapshot for `workspace diff`.
- Freeze physical source origin routes in new manifests; support legacy manifests through their recorded Src/exclusion mapping. Reject linked source/target files and path traversal; serialize overlapping Unidot source updates.
- Keep Managed DLL/PDB, caches and binary build products out of apply. Add regression coverage for conflict classes, expired resolutions, repeated apply, rollback, concurrency, bytes and origin boundaries.
- Bundle the optional experimental Windows x64 UnityMain2 host. Standard EXE remains the default; native fallback is only allowed before engine entry and uses its own correctly forwarded log path. `--keep-alive` restarts a new Player process on request.

## 0.5.1 — Development milestone, included in 0.6.0

- Default `build/run/watch` to concise progress, result/warning counts and log locations; use `--verbose` for full compiler, ILPP and Player output.
- Keep compiler/ILPP errors visible, summarize processing results, and retain raw per-tool logs plus a build journal. External processor output and worker stderr are also logged. Failed/cancelled builds write a current build report instead of leaving stale success diagnostics.
- Preserve full Unity logs and native UTF-8 stdout/stderr sidecars for every Player session. Default console output shows lifecycle status, native stderr and recognized errors with stack traces; verbosity does not change log retention.
- Allow workspace creation while the base Player runs. Retain the base build/deployment lock, verify Managed snapshot metadata across the complete copy, and report actual unreadable/changing inputs with cleanup.
- Regression-test concise/verbose output, warning counts, error visibility, UTF-8 native logs, active base sessions, read-shared DLLs, unreadable inputs and snapshot changes.

## 0.5.0 — Development milestone, included in 0.6.0

- Add Git-independent `workspace create <name> --base <Player>` and `workspace list/diff/remove`, with `--root` to choose the workspace container.
- Create a new lightweight directory directly: copy actual Src files and the complete Managed tree, copy the Player executable and small registration metadata, and link resources/runtime files. Never clone or stage a complete Player resource directory.
- Probe file/directory link support before copying; refuse resource-copy fallback, existing destinations, overlapping paths, and unsafe names. Clean up partial creation and cancellation without following shared links.
- Compile copied source/assembly metadata while excluding original source subtrees; retain external Unity package/toolchain discovery. Snapshot the global compiler response and rebase configured build roots and relative processor executables.
- Store source hashes, link targets, base resource metadata and creation-time copy/shared byte totals in `.unidot/workspace.json`. Diff reports added/modified/deleted paths without merging or storing a second full source copy.
- Validate resource links/base changes before Player commands. Remove only manifest-owned workspaces, refuse active builds/sessions, and unlink resources without deleting targets.
- Add workspace lifecycle/failure/ownership regressions and real Unity compiler coverage verifying private-source compilation and private-Managed deployment.

## 0.4.0 — Development milestone, included in 0.6.0

- Enable Unity Roslyn shared compilation by default to avoid loading the compiler for every assembly.
- Reuse a separate Unity ILPP worker within each build, retaining plugin assemblies while creating fresh processor instances per request. Close the worker on completion, failure, or cancellation.
- Add `sharedCompilation` / `reuseIlppHost` configuration and `--no-shared` / `--isolated-ilpp` overrides for `build`, `run`, and `watch`.
- Report per-assembly compiler/ILPP timings and build timings in `.unidot/build-report.json`.
- Use a locked, stable staging path so deterministic rebuilds preserve DLL/PDB hashes.
- Invalidate cached outputs when compiler sharing or ILPP isolation modes change.
- Regression-test deterministic rebuilds, worker reuse, error recovery, cancellation, and cleanup with the real Unity compiler.
- Local performance measurement: three forced source targets took 3.97s with isolated processes versus 1.47s with reuse; a successful 73-target forced build took 16.49s. These timings exclude CLI discovery/startup and do not imply cross-project performance guarantees.

## 0.3.2 — Development milestone, included in 0.6.0

- Keep the workspace-root solution, but name it `<Product Name>.Unidot.sln` to avoid Unity 2022 Windows native Visual Studio overwrite detection.
- Move a previous, positively identified Unidot-generated conflicting solution into `.unidot/legacy-solutions/`; preserve user/native solutions.
- Regression-test solution naming, migration, and user file protection.

## 0.3.1 — Development milestone, included in 0.6.0

- Embed and synchronize Player `AGENTS.md` instructions using a managed block; preserve user content, encoding, BOM, and line endings.
- Add repository-specific agent guidance separate from game/Player rules.
- Keep help/version free of workspace writes and do not rewrite unchanged instructions.
- Generate the product-named solution and Rider run configuration at the workspace root for IDE/VS Code use; retain projects and artifacts under `.unidot`.
- Preserve existing user-owned solutions and run configurations.

## 0.3.0 — Alpha

首个可发布版本，当前支持 Windows Standalone x64 / Mono。游戏侧完整流程已在 Unity 2022.3.62f3 的 Project B 上验证。

### 构建与部署

- 从 Player 目录开发，默认读取 `Src`，支持可指定的源码目录和符号链接/junction。
- 扫描 `asmdef` / `asmref`，解析名称/GUID 引用、平台、条件符号和包版本定义。
- 独立调用 Unity 安装中的 Roslyn，执行 Source Generator，生成 DLL 和 Portable PDB。
- 支持构建范围、Player 二进制依赖、增量缓存、批量诊断和失败依赖阻断。
- 在独立进程加载配置的 Unity ILPostProcessor，保留诊断并部署最终产物。
- 备份部署、程序集身份和哈希检查、失败回滚、恢复保护。

### IDE 与运行会话

- 按产品名生成解决方案，提供独立的 **Launcher** 启动项目。
- 生成 Visual Studio launchSettings 和 Rider 共享运行配置。
- `unidot run` 默认编译、处理、部署后以前台会话启动游戏。
- 实时跟随 UTF-8 Unity 日志，支持自定义日志文件与原生 stdout/stderr。
- Ctrl+C、游戏退出和 Launcher 停止与本次 Player 生命周期联动。
- Windows Job Object 在会话被强制终止时清理其拥有的 Player 进程树。

### 分发与验证

- 支持独立发布目录以及 `Net.BITKit.Unidot` .NET Tool 全局安装。
- Windows CI 覆盖构建、非 Unity 测试、发布、打包和本地 Tool 安装检查。
- 15 项本地测试（包含可选真实 Unity 编译器测试）通过。
- 73 个游戏侧程序集完成编译、后处理、整组替换，并完成 Player 启动运行验证。

### 当前边界

- 仅支持 Windows x64 Mono Player，IL2CPP 不支持 DLL 替换。
- 需要与项目版本一致的 Unity 安装和已有基础 Player。
- 不是 Unity 完整资源构建、PluginImporter 或全部自定义 ILPP 的复现。
- 编译后的 ILPP DLL 及其依赖需要预先准备；不会生成 Burst AOT 原生库。
- 新程序集注册、资源、场景和运行时初始化清单仍由 Unity 基础构建提供。
- SourceTrace 插桩和 Mono 断点调试未纳入本版本。
