# Agent MCP sessions

`unidot mcp` starts and owns the original Unity Mono Player EXE, injects a runtime server on its message/main thread, and exposes standard MCP over stdin/stdout. The Agent starts this command as a child process; no second terminal, manual game launch, TCP port, or post-launch attachment is needed. Since 0.8.0, the game starts minimized/background by default and its original executable identity, working directory and data path are preserved. `Application.runInBackground` is enabled for the Agent-owned session.

Introduced during 0.7.0 development and distributed in the 0.8.0 Alpha release.

## Launch

```powershell
unidot mcp --player "D:\Games\MyGame.Build" --unity-editor "C:\Program Files\Unity\Hub\Editor\2022.3.62f3"
```

An existing workspace can use `unidot mcp --config <unidot.json>`. Without source-project metadata, supply a matching Unity installation explicitly or through `UNIDOT_UNITY_EDITOR` / `UNITY_EDITOR`. `--player` does not create a source binding or configuration file.

Player arguments follow `--`. Startup readiness is bounded by `--startup-timeout <seconds>` (default 60, range 1-300). The current runtime path targets **Windows x64, Unity 2022, Mono**, with a compiler from the same major/minor family. Live acceptance covers 2022.3.14f1c1, 2022.3.20f1 and 2022.3.62f3; 2022.1/2022.2 are eligible but not runtime-tested here. Development Build and Script Debugging are not prerequisites. IL2CPP, headless/windowless launch, anti-cheat/protected-process injection and other engine years are not supported by this route. Unity Editor does not need to be running. The custom UnityMain/UnityMain2 NativeHost remains an explicit `run --backend native` option, not the default MCP route.

The command uses the existing game DLLs. It compiles the external bootstrap and Agent snippets, not the game's entire source tree. Use the existing `build`/`run` workflow for original assembly replacement; this feature is not a general hot-update system.

### Player identification (0.7.2)

- Read the actual engine version from **UnityPlayer.dll ProductVersion**, not from a resource filename or the supplied compiler. Preserve variants such as `2022.3.14f1c1`; confirm the native DLL is x64.
- Identify Mono through the data directory's **Managed/mscorlib.dll** and the sibling **MonoBleedingEdge** directory, rejecting IL2CPP's GameAssembly.dll. Assembly-CSharp.dll is optional because asmdef-only games have differently named assemblies.
- NativeHost and CLI share this layout validation. Neither requires globalgamemanagers or globalgamemanagers.assets. Resource packing and loader dependencies are left to the actual engine startup.
- Launch the original EXE and install an owned-process x64 message hook. Compile against the target's Managed assemblies with a compiler from the same supported engine family; existing processes are never selected for attachment.
- Engine identity, Mono exports, a usable owned Unity window/thread and successful MCP readiness are separate checks; none alone guarantees every customized game's startup works. 2022.1 / 2022.2 remain untested even though the version gate accepts Unity 2022.

CYANBRAIN was identified as 2022.3.14f1c1 Mono/x64, with UnityMain exported and a native dependency on Cospirazione.dll. Earlier substitute-host runs failed before MCP readiness. The original EXE baseline reached MainMenu; the 0.8.0 original-EXE hook route then completed MCP startup in about 6.2 seconds and read its real scene/object state on thread 1. No game asset or original EXE/DLL was changed.

## Agent configuration

For clients using the conventional `mcpServers` configuration:

```json
{
  "mcpServers": {
    "unidot": {
      "command": "unidot",
      "args": [
        "mcp",
        "--player", "D:/Games/MyGame.Build",
        "--unity-editor", "C:/Program Files/Unity/Hub/Editor/2022.3.62f3"
      ]
    }
  }
}
```

The client must allow enough initialization time for bootstrap compilation and Player startup. The executable must be on its PATH, or replace `unidot` with an absolute path.

For OpenCode, the equivalent `mcp` entry uses a local command array:

```json
{
  "mcp": {
    "unidot": {
      "type": "local",
      "command": ["unidot", "mcp", "--player", "D:/Games/MyGame.Build", "--unity-editor", "C:/Program Files/Unity/Hub/Editor/2022.3.62f3"],
      "enabled": true,
      "timeout": 90000
    }
  }
}
```

## Tools

| Tool | Purpose |
|---|---|
| `runtime_status` | Read live Unity version, scene, main-thread identity, session and log paths |
| `compile_code` | Compile complete C# source and return diagnostics plus a session-local `compilationId` |
| `execute_compiled` | Execute a successful `compilationId` on the Unity main thread |
| `execute_code` | Compile and execute in one call |

`compile_code` and `execute_code` take `code`, optional `typeName` (default `Script`), `methodName` (default `Run`), and `timeoutMs` (default 30000, range 1-120000). `execute_compiled` takes `compilationId` and `timeoutMs`.

The entry point is **public static and parameterless**, with a closed type/method. It may return a JSON-serializable value, `void`, `Task` or `Task<T>`. Use fully qualified `typeName` for a namespace. `async void` and `ValueTask` entries are not supported. Unity API calls and the initial async entry invocation run on Unity's main thread; normal awaits capture its synchronization context unless user code opts out.

Example `execute_code` argument:

```json
{
  "code": "using UnityEngine; public static class Script { public static string Run() { var cube = GameObject.Find(\"Cube\"); if (cube == null) return \"Cube not found\"; cube.GetComponent<Renderer>().material.color = Color.green; return cube.name; } }"
}
```

Compilation uses the supplied Unity Roslyn compiler and the target Player's Managed references. Every compilation gets a unique assembly identity; compiler source, response files, DLL/PDB and logs are retained under the session directory. Compile failures and execution exceptions return MCP tool errors without requiring a new session. Snippets do not run the game's configured IL postprocessors or produce Burst AOT code.

## Lifetime and output

- stdout is MCP-only. Compiler, startup and Player diagnostics use stderr and files.
- A private random named pipe connects Unidot to the injected runtime; the Agent never needs its name.
- Closing Agent stdin / the MCP transport stops the owned Player. Player exit also ends the Agent session. Windows Job Object ownership covers forced CLI termination.
- Logs and execution artifacts are retained under `.unidot/mcp/session-*` beside the configuration (or inside the specified Player when no configuration is used). `--log-file` overrides the Player log location.
- A timeout cancels compilation or waiting for a response. It cannot forcibly interrupt synchronous code already running on Unity's main thread. Such code may continue; ending the MCP session stops the Player.
- This is a trusted development-code execution channel, with the same permissions as the Player, not a sandbox. SDK dependency versions must coexist with the game's existing libraries; acceptance covers RuntimeProbe, CYANBRAIN and the tested BITFALL build, not every game dependency set.

## Verification

The real stdio acceptance test starts an unmodified RuntimeProbe without experiment environment variables, performs MCP initialization/tool discovery, compiles `1 + 2`, invokes the compiled ID, executes an async Unity script after `Task.Yield` on thread 1, observes modified/created Cube geometry, returns compiler/runtime errors, and checks Player/CLI exit on stdin EOF. See [probe evidence](mcp-probe.md).

## Building the original-EXE bridge

End users receive `native/Unidot.RuntimeHook.dll` in the .NET Tool / published directory and do not need a C compiler. Source builds use x64 TinyCC 0.9.27: set `UNIDOT_TCC` to its `tcc.exe` or place it on PATH. The official Windows archive SHA-256 is `34A721949A2583FDFF725312DA092FA0F5F1F284B702E6F811C6954714FAABB2`. CI verifies and extracts that development-only compiler; it is not shipped with Unidot. Rebuild compiles the small Win32 bridge, with no second Mono/CLR initialized in the game.
