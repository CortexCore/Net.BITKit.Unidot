# Changelog

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
