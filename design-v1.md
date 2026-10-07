# Unidot

Unidot 是一套独立于 Unity Editor 的 C# 构建与运行工具，目标是让 Unity 项目的纯代码开发体验更接近普通 .NET / Godot / Source Engine：修改代码后无需重新完整 Build Player，也无需启动 Unity Editor。

核心思路：

- 以 Unity 项目中的 `asmdef` 作为程序集定义来源。
- 一个 `asmdef` 对应一个独立 DLL / C# 项目。
- 扫描 `asmdef` 之间的引用关系，建立依赖图，自动决定编译顺序。
- 自动生成或维护对应的 `csproj` / `sln`。
- 从已经打包好的 Unity Player 中读取 `Managed` 目录，自动引用 UnityEngine、第三方库以及其他运行时 DLL。
- 使用标准 .NET / MSBuild 工具链完成编译。
- 编译成功后，将新 DLL 部署到已有 Unity Player 中并直接启动游戏。
- 源码目录可以通过符号链接同时暴露给 Unity 工程和 Unidot，保证始终只有一份源码。

期望使用方式类似：

```bash
unidot init
unidot build
unidot build Game.AI
unidot run
unidot watch
```

整体流程：

```text
Unity asmdef + C# Source
        ↓
      Unidot
        ↓
解析程序集和依赖关系
        ↓
生成 .NET project graph
        ↓
   dotnet / MSBuild
        ↓
    独立 DLL
        ↓
部署到已有 Unity Player
        ↓
      启动游戏
```

Unidot 本身应当是普通的 .NET CLI/TUI 工具，不依赖 Unity Editor。

后续可以与 SourceTrace 集成，在编译完成后自动进行 IL 处理、调用跟踪和源码诊断，形成完整的：

```text
Source
→ Unidot
→ Build
→ SourceTrace
→ Unity Player
→ Trace
```

核心目标只有一个：

**把 Unity Player 当作已经构建好的游戏引擎运行时，而把日常 C# 代码开发重新变成标准、快速、独立的 .NET 构建流程。**