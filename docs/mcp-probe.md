# 官方 MCP Core / Unity Mono 最小验证

验证日期：2026-10-09。结论：`ModelContextProtocol.Core 2.2.0` 的服务端在固定 Unity Mono Player 内通过了外部注入、握手、工具发现和主线程调用验证。后续优先沿此路线接入，TouchSocket.Mcp 保留为备选。

## 环境与边界

- Unity 2022.3.62f3，Windows x64，Mono Development Player：`Unidot.RuntimeProbe/Build`。
- 复用之前的 NativeHost / Mono 导出函数加载原型，加载外部 bootstrap；没有向基础 Player 的 Managed 目录或脚本程序集列表添加文件。
- bootstrap 使用 Unity 自带 Roslyn 和该 Player 的真实框架程序集编译。
- MCP 使用官方 `StreamServerTransport`，底层为一次会话专用的本地命名管道。协议处理由 SDK 完成，外部 Python 客户端发送 JSON-RPC 消息。
- Unity API 调用通过启动时捕获的 `UnitySynchronizationContext.Post` 调度到主线程。
- 每次运行限定 30 秒，随后关闭本次拥有的窗口/进程并检查清理。

## 结果

成功会话：`session-20261009-233621`，PID 52920。

| 检查 | 结果 |
|---|---|
| 外部 bootstrap 加载 | 成功，managed thread 1 |
| `initialize` | 成功，协商协议 `2025-11-25`，服务版本 `2.2.0.0` |
| `tools/list` | 返回 `runtime_status` 和 `probe_cube` |
| `runtime_status` | Unity `2022.3.62f3`，场景 `SampleScene`，thread `1`，`UnityEngine.UnitySynchronizationContext`，Cube 存在 |
| `probe_cube` | 在 thread 1 将 Cube 改绿，并从 `(0,0,0)` 移动到 `(0,0.25,0)` |
| 画面 | `official-mcp.png` 已读取，确认绿色 Cube |
| 停止 | 正常退出，exit code 0；没有残留 Probe 进程 |

## 依赖收集要点

首次运行在 bootstrap 加载后缺少 `System.Threading.Tasks.Extensions, Version=4.2.1.0`，未完成握手。原因是验证工程以 netstandard2.1 收集依赖，未带上 SDK 的 netstandard2.0 兼容 DLL。

改为以 netstandard2.0 工程收集完整依赖，再单独用 Unity 编译器编译 bootstrap 后，验证通过。不需要修改或降级 SDK。SDK 仍有多个依赖 DLL，不能将本结果表述为零依赖或已证明比 TouchSocket 更小。

## 首次 Probe 阶段未覆盖

- HTTP / Streamable HTTP 服务端；本轮验证的是流传输。
- 当时尚未接入正式 CLI / Agent stdio / 通用代码执行；这些已在下方的 0.7.0 验收中补齐。游戏自定义工具注册仍未接入。
- 真实 Project B 的现有依赖版本共存。

原型源码、依赖和两次会话日志位于：`C:/Users/Iris/AppData/Local/Temp/opencode/unidot-official-mcp-probe/`。成功会话的 `result.json` 保存完整握手及工具调用响应。

## 经 Unidot 启动的补充验证

2026-10-10，安装的 Unidot 0.6.1 已通过现有 `--native-host` 扩展入口启动注入 MCP 的 Probe。没有修改正式 CLI 或内置 NativeHost 的实现。

- 临时 `McpNativeHost.csproj` 构建兼容 Unidot 参数的实验 `Unidot.NativeHost.exe`。
- `unidot-probe.json` 指定该宿主，环境变量 `UNIDOT_PROBE_BOOTSTRAP` 指定外部 DLL；`UNIDOT_PROBE_DIR` 和 `UNIDOT_PROBE_PIPE` 指定本次会话目录、管道。
- Unidot 自行准备 `.unidot/native-host` 和 Mono 链接，管理 Player 进程、日志及退出。
- 成功会话 `session-unidot-20261010-001611`：CLI PID 48280、Player PID 71552；握手、两个工具的发现/调用和 thread 1 的 Cube 修改均通过；两进程正常退出，exit code 0。
- 本次 `--no-build` 复用基础 Player，外部 bootstrap 在启动前已单独编译；没有验证游戏源码重编译或热更新。

可重复的限时验证命令：

```powershell
python "C:\Users\Iris\AppData\Local\Temp\opencode\unidot-official-mcp-probe\run_probe.py" --via-unidot
```

该脚本设置实验环境后，实际调用：

```powershell
unidot run --no-build --backend native --config "C:\Users\Iris\AppData\Local\Temp\opencode\unidot-official-mcp-probe\unidot-probe.json" --log-file <本次日志> -- -screen-width 800 -screen-height 600 -screen-fullscreen 0 -force-d3d11
```

只复制上面的实验 CLI 命令、不设置实验环境变量，不会启用此 bootstrap。此阶段验证的是扩展宿主路径；普通 `unidot run` 默认仍不注入 MCP。

## 正式 stdio 入口与编译/执行验收（0.7.0）

2026-10-10，本机安装的 Unidot 0.7.0 已用正式 `unidot mcp` 验收。不设置任何 `UNIDOT_PROBE_*` / `UNIDOT_RUNTIME_*` 环境变量，不引用之前的临时宿主或 bootstrap；所有运行资源由安装包准备。

```powershell
unidot mcp --player "D:\Iris\Documents\GitHub\Unidot.RuntimeProbe\Build" --unity-editor "C:\Program Files\Unity\Hub\Editor\2022.3.62f3"
```

Agent 测试客户端通过标准 stdin/stdout 发起请求，整个测试限定 45 秒并安排失败清理。成功会话 `unidot-mcp-acceptance-20261010-010720`：CLI PID 76280，Player PID 88296。

- 握手确认服务为 `Unidot 0.7.0`；工具列表包含 `runtime_status`、`compile_code`、`execute_compiled`、`execute_code`。
- `compile_code` 编译 `1 + 2`，通过返回的 compilationId 调用 `execute_compiled`，结果为 `3`。
- `execute_code` 编译并执行一个 `async Task<string>` 入口；`Task.Yield` 后仍在 managed thread 1，将原 Cube 改绿、上移 0.25，并创建青色 `AgentMcpCube`。截图 `agent-mcp.png` 已读取确认。
- 错误源码返回 CS0708 / CS0246；执行异常返回 `InvalidOperationException: agent-execution-test`；随后状态查询仍正常。
- `async void` 入口被明确拒绝。
- stdout 的每行均能解析为 MCP JSON-RPC；编译/会话/Player 日志保留在 stderr 和文件。
- 关闭 Agent stdin 后，Unidot 自动停止 Player，CLI 与 Player 均正常退出，exit code 0；没有残留拥有的子进程。

完整响应、日志和截图位于 `C:/Users/Iris/AppData/Local/Temp/opencode/unidot-mcp-acceptance-20261010-010720/`。工具侧回归 34/34，通过 .NET 8 SDK Release 构建；Roslyn 编译检查无错误/警告。包位于本机 `artifacts/packages/Net.BITKit.Unidot.0.7.0.nupkg`，尚未发布 GitHub Release 或 nuget.org。

当前使用方式见 [Agent MCP sessions](mcp.md)。原游戏程序集替换/玩家热更新、游戏自定义工具扫描、HTTP 和真实 Project B 依赖共存不属于这次验收。

## 旧 BITFALL 包兼容与只读访问（0.7.1）

2026-10-10，目标：`D:/Iris/Documents/Workspace/BITFALL_Build_Win64`，Unity 2022.3.20f1 Mono。本机使用 2022.3.62f3 的 Roslyn，引用该旧 Player 自己的 Managed 程序集；证明了这组同系列补丁版本组合可用，不代表任意跨版本兼容。

首次启动在引擎入口前退出：旧 UnityPlayer.dll 没有 UnityMain2。0.7.1 增加 UnityMain 回退，按 Unity 官方 Exports.h 的四参数 ABI 传入 null hPrevInstance，并用 `.unidot/native-host/Unidot.NativeHost_Data` 链接原 BITFALL_Data。没有替换原游戏 EXE、UnityPlayer.dll 或 Managed DLL。

安装版成功会话：`unidot-bitfall-probe-20261010-012918`，CLI PID 89564、Player PID 81272。测试限时 70 秒，只读访问。

- MCP 握手、工具发现、runtime_status、Agent 代码编译/执行成功，读取操作在 thread 1。
- 查询期间场景从 Initialize 进入 Scene_Menu；截图确认真实 BITFALL 主菜单。
- 常驻根物体包括 Framework(Clone)、BITApp、FrameworkLoader、[YooAssets]；可枚举上百个 BITFALL/BITKit 程序集。
- 已访问 PlayerSpawnService、PlayerGraphics、库存 UI、Mod 服务等旧组件实例及字段元数据。
- 读取 Mod HttpListener 的 BITKit.Entities.Entity 实际字段：`_initialized=true`、`isInitialized=true`，以及运行时 id。
- 日志存在旧资源地址 localhost:27804 连接失败；本次没有启动该资源服务、改变资源配置或验证进入玩法场景。
- 关闭 stdin 后 CLI / Player 均正常退出（code 0），本次拥有的子进程全部退出。为让测试的进程清理验收不包含共享 Roslyn 服务，最后一次使用 `--no-shared`；共享编译服务不属于游戏进程生命周期。

使用命令：

```powershell
unidot mcp --player "D:\Iris\Documents\Workspace\BITFALL_Build_Win64" --unity-editor "C:\Program Files\Unity\Hub\Editor\2022.3.62f3"
```

完整响应、日志和截图位于 `C:/Users/Iris/AppData/Local/Temp/opencode/unidot-bitfall-probe-20261010-012918/`。兼容修复通过 35/35 回归测试，Roslyn 和 Release 构建无错误/警告；本机已安装 0.7.1，尚未提交或发布。

## 原 EXE 注入 / 非 Development Build 验收（0.8.0）

2026-10-10，CYANBRAIN 的原 EXE 对照启动成功进入 MainMenu；替代 NativeHost 的旧路径则报宿主数据目录错误。由此改为启动原 EXE，再对本次拥有的窗口/主线程安装小型 x64 Win32 消息钩子，加载同一个外部 Mono bootstrap。该路线保留原进程身份、工作目录、原生依赖和 Application.dataPath，不改原 EXE、UnityPlayer.dll 或 Managed DLL。

- 原 EXE 单次 MCP 验收 `cyanbrain-mcp-20261010-024255`：约 6.19 秒完成 stdio MCP 初始化，thread 1 查询 MainMenu、16 个根对象以及 GameState、Saves、SceneLoader、GameAudio 等真实组件；stdin EOF 后正常退出。
- 安装版 Unidot 0.8.0 的会话通过同一条持续 stdio 连接查询 MainMenu，随后执行只读 C#，读到 Home0 和之后的 2_EnergyStation0。该对话验证使用临时测试客户端保存 stdio 连接，最多 10 分钟，不作为发布版的额外协议。
- CYANBRAIN runtime_status 明确返回 `developmentBuild=false`、`runInBackground=true`、`managedThreadId=1`。因此开发者构建/Script Debugging 不是该注入机制的前提。
- RuntimeProbe 原 EXE 回归 `unidot-mcp-acceptance-20261010-030010`：返回 `developmentBuild=true`；编译、compiledId 执行、async Task.Yield 后的 Unity 修改、异常返回、async void 拒绝和 EOF 清理全部通过。原 EXE 路线同时覆盖非开发与开发构建。
- 37/37 工具回归通过，Roslyn 无错误/警告；桥接 DLL 构建并随 0.8.0 工具包分发，开发用 TinyCC 不需要安装在最终用户机器上。

当前目标为 Windows x64、Unity 2022 Mono；实际运行验收小版本仍为 2022.3.14f1c1 / 2022.3.20f1 / 2022.3.62f3。2022.1、2022.2 只放开版本资格检查，尚未运行验收。无可用 Unity 窗口、IL2CPP、不同位数或受保护进程不在当前路径范围内，不能把上述结果表述为所有游戏百分之百兼容。

### 提交前旧 BITFALL 原 EXE 验收

2026-10-10，使用已安装的 Unidot 0.8.0，成功会话 `unidot-bitfall-probe-20261010-050557`：CLI PID 92516、原 BITFALL.exe PID 71268。测试限时 70 秒并安排 EOF 清理。

- `runtime_status` 返回 Unity 2022.3.20f1、`developmentBuild=false`、`runInBackground=true`、managed thread 1。
- 执行只读代码枚举场景、常驻 Framework/BITApp/YooAssets 根物体、BITFALL/BITKit 程序集和旧组件字段。
- 编译 `21 + 21`，通过 compilationId 独立执行，结果为 `42`。
- 原游戏进入 Scene_Menu；原 Entity 实际字段 `_initialized` / `isInitialized` 为 true。
- stdout 保持 JSON-RPC；关闭 stdin 后原游戏和 CLI 正常退出，code 0，本次拥有的进程无残留。

完整响应、截图和日志保存在 `C:/Users/Iris/AppData/Local/Temp/opencode/unidot-bitfall-probe-20261010-050557/`。本次没有修改游戏 EXE / Managed DLL，也没有注入此前的实验 FlyCamera。

提交前使用 .NET SDK 8.0.301 再次完成 Release 构建（0 errors / 0 warnings）和 37/37 工具回归测试，并确认上述 BITFALL / CLI 进程均已退出。
