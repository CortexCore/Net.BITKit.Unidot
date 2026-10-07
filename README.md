# Unidot

独立于 Unity Editor 的 C# 构建、部署和运行 CLI。以 Unity 的 `asmdef` 为程序集结构来源，复用已构建的 **Mono Unity Player**，让日常代码修改通过独立 Roslyn 编译完成。

当前版本：**0.3.0 / Windows Standalone x64 / Mono**。不需要启动 Editor；需要与项目版本一致的 Unity 安装，以及已有 Player。

**发布状态：alpha。** 已验证游戏侧编译、ILPP、整组 DLL 替换、前台运行和 IDE Launcher 流程；跨项目兼容性仍需逐项验证。版本说明见 [CHANGELOG.md](CHANGELOG.md)。

仓库：[CortexCore/Net.BITKit.Unidot](https://github.com/CortexCore/Net.BITKit.Unidot) · 许可证：[MIT](LICENSE)

## 从打包目录直接开发

推荐布局：

```text
MyGame.Build/
  MyGame.exe
  MyGame_Data/Managed/
  Src/Artists/Scripts/ -> Unity 工程的 Assets/Artists/Scripts
```

安装全局 .NET Tool 后，在 Player 目录执行一次源码映射：

```powershell
unidot link --source "D:\Games\MyGame.Unity\Assets\Artists\Scripts"
unidot run
```

默认链接位置是 `Src/Artists/Scripts`，默认源码目录是 **`Src`**。Windows 支持符号链接，权限不足时使用目录 junction，不复制源码，也不覆盖已有目标目录。

首次 `run` 会从链接的物理源路径推断 Unity 工程、匹配工具链、绑定当前 Player，并保存本地 `unidot.json`。默认选择已经验证的 NetRpc/Jobs/Burst 编译后处理器 DLL（存在时），只构建 Src 对应的运行时源码程序集；Unity 工程的其他目录用于依赖和包元数据解析，插件仍使用 Player 二进制。

```powershell
unidot run                       # 增量编译 → 后处理 → 备份部署 → 启动
unidot run --no-build            # 跳过构建，启动并跟随现有 Player 日志
unidot run --src "OtherSources"  # 指定其他源码目录
unidot build --no-deploy         # 只生成产物
```

源码位置不是符号链接、无法推断 Unity 元数据，或需要特殊处理器时，可以显式执行 `init --project <Unity 工程> --src <源码目录>`。配置只需建立一次；不是从 DLL 猜测源码，也不保证发现所有自定义 ILPP。

本地包全局安装示例：

```powershell
dotnet pack src/Unidot -c Release -o artifacts/packages
dotnet tool install --global Net.BITKit.Unidot --version 0.3.0 --add-source artifacts/packages
```

命令 shim 位于 `%USERPROFILE%/.dotnet/tools`，该目录需在 PATH 中。升级使用 `dotnet tool update --global`。

## 开发与运行

需要 .NET 8 SDK 或更新版本。

```powershell
dotnet build Unidot.sln
dotnet run --project tests/Unidot.Tests
dotnet run --project src/Unidot -- --help
```

带真实 Unity 编译器的集成测试：

```powershell
dotnet run --project tests/Unidot.Tests -- --unity-editor "C:\Program Files\Unity\Hub\Editor\2022.3.62f3\Editor"
```

发布到独立目录：

```powershell
dotnet publish src/Unidot -c Release -o artifacts/unidot
.\artifacts\unidot\Unidot.exe --help
```

将发布目录加入 `PATH` 后即可从终端调用；默认发布依赖 .NET 8 Runtime。也可以显式发布自包含版本：

```powershell
dotnet publish src/Unidot -c Release -r win-x64 --self-contained true -o artifacts/unidot-win-x64
```

项目已声明 `PackAsTool` 和 `ToolCommandName=unidot`，支持后续打包成 .NET Tool：

```powershell
dotnet pack src/Unidot -c Release -o artifacts/packages
```

源码仓库目前不包含 Unity 安装、第三方处理器、游戏源码、Player、个人 `unidot.json` 或构建产物。上述 NuGet 安装示例使用本地生成的包；本项目尚未宣称已经发布到 nuget.org。

Windows GitHub Actions 会在无需 Unity 安装的环境中构建、运行工具侧测试、生成发布目录和 `.nupkg`，并从纯本地包源验证命令安装。真实 Unity 编译/游戏运行验证按前述可选集成测试和已有 Player 单独执行。

## Unity 安装路径

推荐环境变量名 **`UNIDOT_UNITY_EDITOR`**，兼容 **`UNITY_EDITOR`**。变量指向 `Unity.exe`、`Editor` 目录、`Editor/Data` 或该 Unity 版本的安装目录均可。

```powershell
$env:UNIDOT_UNITY_EDITOR = "C:\Program Files\Unity\Hub\Editor\2022.3.62f3\Editor"
```

查找优先级：

1. `--unity-editor`。
2. `unidot.json` 的 `unityEditor`。
3. `UNIDOT_UNITY_EDITOR`。
4. `UNITY_EDITOR`。
5. 根据 `ProjectVersion.txt` 在默认 Unity Hub 安装位置查找。

`init` 保存选定路径，避免后续被其他 Unity 版本影响。实际编译入口：

```text
Editor/Data/NetCoreRuntime/dotnet.exe
  exec Editor/Data/DotNetSdkRoslyn/csc.dll
```

这里的 .NET 是编译器宿主；游戏 DLL 使用 Player 的基础类库和运行时 API，不会因此变成 .NET 8 游戏程序集。

## 初始化

在你希望存放 Unidot 配置和生成文件的目录执行：

```powershell
unidot init --project "D:\Games\MyGame.Unity" --player "D:\Games\MyGame.Build\MyGame.exe"
```

源码仍在原位置，无复制；扫描会跟随目录符号链接/junction，并按物理目录去重。

可以选择默认构建程序集，选项允许重复：

```powershell
unidot init --project "D:\Games\MyGame.Unity" --player "D:\Games\MyGame.Build" `
  --assembly Game.Core --assembly Game.Unity
```

默认 API 档位是 `unity-4.8`；使用 .NET Standard 2.1 的项目应传入 `--api-profile netstandard2.1`。Development Player 对应 `--development`。它们应与基础 Player 构建配置一致。

生成布局：

```text
unidot.json
.unidot/
  <产品名>.sln          # 例如 Project B.sln
  Launcher/Launcher.csproj
  .run/Launcher.run.xml
  graph.json
  projects/<程序集>/<程序集>.csproj
  bin/<程序集>/<程序集>.dll + .pdb
  state/              # 增量构建输入和输出哈希
  rsp/                # 实际编译参数
  staging/            # 编译及后处理临时产物
  backups/            # 部署前备份和 manifest.json
  logs/               # Player 启动日志
```

配置可以放在其他目录，所有命令支持 `--config <文件>`。不传时，从当前目录向上查找 `unidot.json`。示例见 [unidot.example.json](unidot.example.json)。

### 只编译游戏侧脚本

`sourceRoots` 控制额外扫描位置；**`buildRoots` 控制哪些程序集从源码构建**。二者独立，扫描全项目仍可解析插件/包 GUID，但不必重编插件。

例如只编译 `Assets/Artists/Scripts` 下有独立 `asmdef` 的 Player 脚本：

```json
{
  "assemblies": [],
  "buildRoots": ["Assets/Artists/Scripts"],
  "referenceMode": "player"
}
```

`buildRoots` 相对 Unity 项目根目录，也支持绝对路径。非空时，只生成该范围内有源码的运行时项目；范围外依赖使用现有 Player DLL，依赖遍历不会把插件源码拉入构建。`assemblies` 非空时仍用于进一步选择默认目标。目录内没有独立程序集定义的预定义程序集不被这个模式隐式纳入。

初始化时也可传入 `--build-root Assets/Artists/Scripts --reference-mode player`。

引用模式：

- **`asmdef`**（默认）：严格检查声明引用，未解析 GUID/缺失 DLL 阻止生成。
- **`player`**：使用声明中可用的运行时引用、当前导入的自动引用插件及基础类库；有未解析 GUID 时，从该程序集现有 Player DLL 的实际依赖补充引用。Editor/未启用的可选声明保留为诊断，让编译器确认是否真的缺少类型。不会把所有 Player 游戏 DLL 无条件加入引用，避免旧契约或无关命名空间遮蔽目标类型。

`player` 模式未完整模拟 Unity PluginImporter。它检查当前扫描到的 DLL 文件和 `isExplicitlyReferenced` / RoslynAnalyzer 标记，并要求依赖 DLL 已存在于 Player。此模式是显式选择的运行时构建配置，不保证完全复现 Unity 的程序集引用边界。

```powershell
# 范围内按依赖顺序构建，插件仍然保持二进制
unidot build --no-deploy

# 分别编译并收集整组诊断，任何部分构建都不部署
unidot build --no-dependencies --no-deploy --keep-going
```

`--keep-going` 必须配合 `--no-deploy`，继续编译独立目标；启用依赖遍历时，失败依赖的下游会标记为 blocked，不回退到旧 DLL。结果写入 `.unidot/build-report.json`，编译器日志保存在 `.unidot/logs/build-*/`。Player 模式在同次构建中优先使用已经成功完成、且被声明依赖的选定目标产物。

## 命令

```powershell
unidot graph Game.Unity
unidot generate
unidot build
unidot build Game.Unity
unidot build Game.Core Game.Unity --no-dependencies --no-deploy
unidot run
unidot run -- --mode=no-init
unidot watch Game.Unity --no-dependencies --no-deploy
unidot restore
unidot restore --backup ".unidot/backups/<批次>/manifest.json"
```

- **`graph`**：显示选定程序集及依赖顺序，检测循环。
- **`generate`**：刷新 Player 专用项目和解决方案，不构建或部署。
- **`build`**：按依赖顺序编译，默认部署；`--force` 跳过缓存。
- **`--no-dependencies`**：只编译指定程序集，依赖使用现有 Player DLL。适合先逐个验证，或复用稳定依赖。
- **`--keep-going`**：仅在 `--no-deploy` 下收集完整构建诊断，保留失败/blocked 记录并返回非零退出码。
- **`--no-deploy`**：只生成产物，允许运行中的 Player 保持原 DLL。
- **`run`**：默认增量编译、执行后处理、备份部署后启动 Player，保持前台并实时显示日志；`--no-build` 跳过构建。编译或部署失败时不启动，已有游戏进程需要先关闭。
- **`watch`**：监视源码/程序集定义，去抖后增量构建；重新读取配置。默认仍尝试部署，Player 必须已关闭。运行中开发可以使用 `--no-deploy`。
- **`restore`**：恢复最近一次部署，或指定备份。若目标已被后续修改，则拒绝覆盖。

未指定程序集时，优先使用配置中的 `assemblies`；其次选择 `buildRoots` 内有源码的本地运行时程序集；没有构建范围时，选择 Player 已包含的本地运行时程序集。Registry/git 缓存包默认复用 Player DLL；没有范围限制时，显式指定缓存包名称可以构建该包。

### 生成的 csproj

解决方案按 `Player_Data/app.info` 中的产品名命名，缺失时使用 Player 文件名；配置的 `productName` 可覆盖显示名称。生成的 **Launcher** 是独立 .NET 8 可执行项目，不引用游戏项目，只调用已安装的 `unidot run`。解决方案的默认 Build 配置只构建 Launcher，游戏 DLL/ILPP 仍由 Unidot 管理。

打开 `.unidot/Project B.sln` 后，选择 **Launcher** 点播放。Visual Studio 使用 Launcher 的 `launchSettings.json`；首次打开或已有 IDE 用户配置时，必要时手动将 Launcher 设为启动项目。Rider 提供共享的 `.run/Launcher.run.xml` 配置。IDE Run/Console 显示编译和 Unity 日志，无需断点调试连接。

### 前台运行与日志

```powershell
unidot run
unidot run --no-build --log-file "logs/session.log"
unidot run -- --mode=no-init
```

Unity 使用 `-logFile` 写入文件，Unidot 增量跟随 UTF-8 日志，同时转发原生 stdout/stderr。默认日志仍在 `.unidot/logs/`，`--log-file` 相对当前终端目录，可指定绝对路径。最后一行没有换行也会在退出时收尾。

- 游戏主动退出：Unidot 输出退出码并结束会话。
- Ctrl+C：先请求本次 Player 关闭窗口，最多等待 5 秒，再清理本次进程树；返回 130。
- IDE 停止 Launcher：CLI 监测 Launcher 的父进程生命期并关闭本次 Player。
- Windows 上直接强制终止 CLI：持有的 kill-on-close Job Object 清理其拥有的 Player 子进程，避免残留后台游戏。

这只管理本次启动的进程，不按游戏名称关闭其他进程。Launcher 是启动/日志入口，不提供 Mono 断点调试。游戏的 DLL/PDB 生成说明如下。

项目包含显式源码、`ProjectReference` 和 Player DLL 引用，设置 `NoStdLib`，不包含 `UNITY_EDITOR`。生成 GUID 稳定，未变化的项目不重写。

MSBuild 的编译目标同样显式调用 Unity 自带 .NET 宿主和 Roslyn：

```powershell
dotnet msbuild .unidot/projects/Game.Core/Game.Core.csproj /t:Build
```

要使用已有 Player 依赖，而不编译其他源码项目：

```powershell
dotnet msbuild .unidot/projects/Game.Unity/Game.Unity.csproj /t:Build /p:UnidotUsePlayerReferences=true
```

直接 MSBuild 得到的是**原始编译产物**；后处理、缓存状态和部署由 `unidot build` 负责。复杂项目中，生成过程无法解决的程序集会写入 `graph.json` 的 `generationErrors`，并从解决方案排除；其他程序集仍可使用它们已存在的 Player DLL。

## 依赖解析

已实现：

- `Assets`、embedded/local packages、已解析的 `Library/PackageCache`、额外 `sourceRoots`。
- `asmdef` 名称引用，以及 `.meta` GUID 引用。
- `asmref` 扩展源码归属，嵌套 `asmdef`/`asmref` 边界。
- Windows 平台包含/排除、测试程序集排除。
- `defineConstraints` 的 AND、`||` 和 `!`。
- `versionDefines`，支持最低版本、精确版本 `[x]`、区间 `[x,y)` 等。
- Player 平台/Unity 版本符号、Standalone 自定义符号、输入系统符号、额外 `defines`。
- `allowUnsafeCode`、`noEngineReferences`、`overrideReferences`、`precompiledReferences`。
- 基本的 `Assembly-CSharp` / `Assembly-CSharp-firstpass` 归属和自动引用。
- 依赖拓扑排序与循环诊断。

`overrideReferences=false` 时，当前从 Player 的非源码程序集补充预编译引用；v1 尚未完整复现每个 Unity PluginImporter 的平台/自动引用规则。缺失本地包或失效 `asmref` 会报告；失效 `asmref` 子树不会混入其他程序集。

这不是 Unity 完整构建环境导出的替代品：Player 的 Development/API/功能符号应与配置一致，特殊编译符号可通过 `defines` 补充。平台范围目前限制为 Windows x64 Mono。

## Source Generator 与 IL 后处理

两者是不同阶段：

```text
源码 + Source Generator
    → Unity Roslyn
    → 暂存 DLL/PDB
    → 配置的 IL 后处理命令
    → 最终产物
    → 备份并部署
```

Unity 自带的生成器默认启用；第三方生成器/分析器可以通过 `analyzers` 配置绝对路径或配置目录相对路径。全局和程序集目录下的 `csc.rsp`、配置中的 `compilerArguments` 会补充编译参数；输出路径、引用和 `UNITY_EDITOR` 参数由 Unidot 管理，不允许通过这些参数覆盖。

v1 **不会自动发现 Unity 的全部 ILPostProcessor**。它对已知 Jobs/Burst/网络 weaving 特征做保守检查：没有配置处理器时，拒绝部署；`--no-deploy` 可以生成原始 DLL 供检查。该检查不是完整的 ILPP 需求分析，自定义包仍需要明确配置和验证。

### 独立运行 Unity ILPP

`unityIlppPlugins` 接受已编译的 Unity ILPostProcessor DLL，路径相对 Unity 工程。构建时在独立 CLI 子进程内按配置顺序执行 `WillProcess` / `Process`，无需启动 Editor。每次处理的诊断和 DLL/PDB 结果都经过检查；失败时不部署。

Project B 已验证的配置（插件版本应与基础 Player 一致）：

```json
{
  "unityIlppPlugins": [
    "Library/ScriptAssemblies/Unity.BITKit.Multiplayer.CodeGen.dll",
    "Library/ScriptAssemblies/Unity.Collections.CodeGen.dll",
    "Library/ScriptAssemblies/Unity.Burst.CodeGen.dll"
  ]
}
```

处理器 DLL 来自已经完成的 Unity 基础构建/脚本编译；首次获取或升级这些处理器仍需要准备对应产物。宿主使用 Unity 安装中的 `Unity.CompilationPipeline.Common`，并从项目缓存查找 Cecil/Burst 依赖。此功能不是自动生成 Burst AOT 原生库，也不自动更新资源及运行时初始化清单。

`ilpp` 子命令也可手动处理暂存产物：

```powershell
unidot ilpp --dll path/to/Game.dll --pdb path/to/Game.pdb --processor path/to/CodeGen.dll
```

默认读取 `.unidot/rsp/<程序集>.rsp` 的编译引用与符号，可通过 `--rsp` 指定。`audit` 核对上一组成功构建的 Player 文件、注册信息和程序集身份，并生成 `.unidot/deployment-audit.json`。

外部处理器配置示例（程序路径仅为占位示例）：

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

占位符：`{dll}`、`{pdb}`、`{assembly}`、`{project}`、`{managed}`。参数逐个传递，不经过 shell。处理器必须原地更新暂存 DLL/PDB，并以 0 退出；失败时不部署、不替换该程序集上一份成功产物。SourceTrace 集成留待后续。

## 部署约束

- 仅替换基础 Player 已包含的程序集；新增程序集的注册、场景/资源、原生插件、Burst AOT 和运行时初始化清单更新仍需要 Unity 构建。
- Player 必须关闭。新 DLL/PDB 在所有编译完成后再部署。
- 检查程序集名称、版本和签名身份；部署前备份，并校验 SHA-256。
- 批量部署失败时回滚已写入文件；备份包含原 DLL/PDB，原来不存在的 PDB 在恢复时移除。
- 输入源码、依赖、编译器、生成器、Unidot 实现和后处理配置参与缓存；最终 DLL/PDB 哈希不符时重新构建。

## 验证范围

测试包含 GUID/asmref/平台边界、包版本约束、循环、项目生成、部署恢复及覆盖保护。可选 Unity 集成测试覆盖真实编译、依赖产物使用、缓存命中、依赖修改导致重新编译和后处理失败。

Project B 上已用 CLI 验证 `Net.Project.B.AI` 与 `Net.Project.B.AI.Unity` 的独立编译；普通源码项目、当前 Player 二进制依赖和 Unity Source Generator 均参与流程。完整项目中的其他程序集，尤其 ILPP/Burst 和旧 Player 缺失引用部分，需要逐批接入验证。

后续范围验证：`Assets/Artists/Scripts` 中 **73 个有源码的运行时程序集全部完成 DLL/PDB 编译**；插件作为 Player 二进制引用，未改动游戏源码。

新基础 Player 上进一步验证了完整部署链：73 个程序集均已注册且身份一致，独立执行 NetRpc/Jobs/Burst 后处理链，5 个程序集实际完成 NetRpc 改写，其他处理器按结果跳过或保持不变；73 份产物整体备份、部署后，日志记录 `startup.ready`、`startup.completed`、`Player Created`。用户确认游戏启动、运行成功并正常退出。SourceTrace 插桩仍未纳入本轮流程，具体联机/Burst 功能不是这次启动验证的覆盖范围。
