# Unidot

**像 `dotnet` 一样，为已打包的 Unity Player 编译并运行 C# 代码。**

[![版本](https://img.shields.io/github/v/release/CortexCore/Net.BITKit.Unidot?include_prereleases&style=flat-square)](https://github.com/CortexCore/Net.BITKit.Unidot/releases)
[![构建](https://img.shields.io/github/actions/workflow/status/CortexCore/Net.BITKit.Unidot/ci.yml?branch=main&style=flat-square)](https://github.com/CortexCore/Net.BITKit.Unidot/actions/workflows/ci.yml)
[![许可证](https://img.shields.io/github/license/CortexCore/Net.BITKit.Unidot?style=flat-square)](LICENSE)
[![.NET](https://img.shields.io/badge/.NET-8-512BD4?logo=dotnet&style=flat-square)](https://dotnet.microsoft.com/download/dotnet/8.0)
[![平台](https://img.shields.io/badge/platform-Windows_x64-0078D4?style=flat-square)](#使用条件)
[![Unity](https://img.shields.io/badge/Unity-Mono-222222?logo=unity&style=flat-square)](#使用条件)

[English](README.md) · **简体中文** · [使用指南](docs/guide.zh-CN.md) · [版本下载](https://github.com/CortexCore/Net.BITKit.Unidot/releases)

修改代码，执行 `unidot run`，继续使用已有的游戏包。Unidot 编译程序集、执行配置的 IL 后处理、备份并替换 DLL，然后启动游戏并实时输出日志，无需重新打开 Unity Editor，也无需完整 Build Player。

```powershell
unidot run
```

## 快速开始

### 1. 安装

从 [v0.3.0 Alpha Release](https://github.com/CortexCore/Net.BITKit.Unidot/releases/tag/v0.3.0) 下载 `Net.BITKit.Unidot.0.3.0.nupkg`，放入本地 `packages` 目录，然后执行：

```powershell
dotnet tool install --global Net.BITKit.Unidot --version 0.3.0 --add-source ./packages
```

也可以下载同一 Release 中的 `Unidot-v0.3.0-windows.zip`，解压后将其目录加入 `PATH`。ZIP 需要 .NET 8 Runtime；安装 .NET Tool 需要 SDK。当前安装包通过 GitHub Releases 提供，尚未发布到 nuget.org。

### 2. 链接源码

在打包后的 Player 目录执行：

```powershell
unidot link --source "D:\Games\MyGame.Unity\Assets\Artists\Scripts"
```

```text
MyGame.Build/
├─ MyGame.exe
├─ MyGame_Data/Managed/
└─ Src/Artists/Scripts/ → Unity Assets/Artists/Scripts
```

默认源码目录是 **`Src`**。链接始终使用原来那份源码，也可以通过 `--src <目录>` 指定其他位置。

### 3. 运行

```powershell
unidot run
```

首次运行会从链接推断 Unity 工程元数据并保存 `unidot.json`，以后复用配置和增量构建缓存。重新编译替换 DLL 前，需要先关闭游戏。

```text
源码 → Unity Roslyn → ILPP → 备份部署 → Unity Player
                                            ↓
                                      实时控制台日志
```

**Ctrl+C 会关闭本次启动的游戏。** 游戏主动退出后，Unidot 收尾日志并返回退出码。完整日志同时保存在 `.unidot/logs/`。

## 常用命令

| 命令 | 用途 |
|---|---|
| `unidot run` | 编译、后处理、部署、启动并持续显示日志 |
| `unidot run --no-build` | 使用现有 DLL 启动并显示日志 |
| `unidot run --log-file logs/session.log` | 指定持久日志文件 |
| `unidot build` | 编译并部署，不启动游戏 |
| `unidot build Game.Unity --no-deploy` | 编译指定代码，不替换 Player 文件 |
| `unidot generate` | 生成为产品命名的解决方案和 IDE Launcher |
| `unidot watch --no-deploy` | 监视源码变更并重编译，保留 Player 当前文件 |
| `unidot restore` | 恢复经过校验的部署备份 |

游戏参数放在 `--` 后，例如 `unidot run -- --mode=no-init`。全部选项见 `unidot --help`。

## IDE 点播放

```powershell
unidot generate
```

打开 `.unidot/<产品名>.sln`，选择 **Launcher**，在 Rider 或 Visual Studio 点播放。Launcher 调用 `unidot run`，IDE 控制台显示编译输出和 Unity 日志；停止 Launcher 会结束它的 Player 会话。

## 使用条件

- **Windows x64、Mono Player**。IL2CPP 不支持这种 DLL 替换方式。
- 已构建的 Unity Player，以及与工程版本匹配的 Unity 安装，用于提供编译工具链。
- ZIP 使用 .NET 8 Runtime；安装 .NET Tool、开发和运行 Launcher 使用 .NET 8 或更新的 SDK。
- 程序集需要 ILPP 时，需准备已编译的处理器插件。Unidot 执行原有处理器，不生成 Burst AOT 原生库。

**Alpha 阶段：**已在 Unity 2022.3.62f3 和 73 个游戏侧程序集上验证编译、ILPP、整组部署与 Player 启动；资源、场景、新程序集注册和跨项目兼容性不属于这次验证范围。

## 更多

- [中文使用指南](docs/guide.zh-CN.md) / [English user guide](docs/guide.md)
- [配置示例](unidot.example.json)
- [版本记录](CHANGELOG.md)
- [MIT 许可证](LICENSE)

```powershell
dotnet build Unidot.sln
dotnet run --project tests/Unidot.Tests
```
