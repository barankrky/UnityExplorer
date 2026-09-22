# AGENTS.md — UnityExplorer (MCP fork)

Guidance for AI agents working in this repository. Read this before building,
changing, or debugging anything here.

This is a fork of [yukieiji/UnityExplorer](https://github.com/yukieiji/UnityExplorer)
(a fork of [sinai-dev/UnityExplorer](https://github.com/sinai-dev/UnityExplorer))
that adds two things on top:

1. **A Unity 6.3+ compatibility patch** (`Scene.handle` became a struct).
2. **A built-in MCP server** that lets an external agent drive the running game.

Everything else is upstream UnityExplorer: an in-game UI for exploring, debugging
and modifying Unity games, supporting Unity 5.2 through 2021+ on both Mono and
IL2CPP.

---

## 1. Repository layout

```
src/                     the only compiled tree
  MCP/                   ← this fork's MCP server (see §5)
    Runtime/             protocol handler, executor, JSON, reflection helpers
    Transport/           HTTP bridge, JSON-RPC framing, router
    McpManager.cs        lifecycle + config surface
  Loader/                entry point per mod framework
    BepInEx/             ExplorerBepInPlugin.cs
    MelonLoader/         ExplorerMelonMod.cs
    Standalone/          ExplorerStandalone.cs (+ Editor/)
  Config/                ConfigManager.cs — all settings live here
  ObjectExplorer/        Scene Explorer, Object Search
  Inspectors/            GameObject / reflection / mouse inspectors
  UI/                    panels, widgets, UIManager (tab registration)
  CSConsole/             Mono.CSharp REPL
  Hooks/                 Harmony hook manager
  CacheObject/           reflection value cache and views
  Runtime/               runtime helpers
  Tests/                 upstream scratch code, NOT a test suite
  ExplorerCore.cs        core constants (NAME, VERSION, GUID), init
  ExplorerBehaviour.cs   Unity MonoBehaviour; per-frame pump
UniverseLib/             git submodule — see §3
lib/                     vendored build dependencies (ILRepack, interop, BepInEx)
Release/                 build output per configuration (gitignored)
docs/                    MCP.md, MCP_CAPABILITIES.md, MCP_SECURITY.md
UnityEditorPackage/      Unity Editor package variant
build.ps1                the supported full build
status.md                session notes — see §9
```

`src/UnityExplorer.sln` is the solution. There is **no** `.editorconfig`,
`Directory.Build.props`, or `global.json`; all settings are in
`src/UnityExplorer.csproj`.

---

## 2. Identity and constants

| Thing | Value |
|---|---|
| Product name | `UnityExplorer` |
| Version | `4.13.5` (`ExplorerCore.VERSION`) |
| BepInEx GUID | `com.sinai.unityexplorer` |
| BepInEx config file | `BepInEx/config/com.sinai.unityexplorer.cfg` |
| Plugin folder | `BepInEx/plugins/sinai-dev-UnityExplorer/` |
| MelonLoader config | `UserData/MelonPreferences.cfg` |
| Standalone config | `{DLL_location}/sinai-dev-UnityExplorer/config.cfg` |

---

## 3. Git workflow — read this first

There are **two remotes** and they mean different things:

| Remote | URL | Meaning |
|---|---|---|
| `fork` | `https://github.com/barankrky/UnityExplorer` | **your** fork — push here |
| `origin` | `https://github.com/yukieiji/UnityExplorer` | upstream — **never push** |

Work happens on branch **`fix/unity-6.3-scene-handle`**. The local branch is
misleadingly named `master` but tracks `fork/fix/unity-6.3-scene-handle`, so a
bare `git push` goes to the right place. Verify with `git branch -vv` before
pushing anything.

```bash
git push fork HEAD:fix/unity-6.3-scene-handle
```

### UniverseLib is a submodule

`UniverseLib/` is a submodule pinned to a commit on
`barankrky/UniverseLib` branch `fix/unity-6.3-scene-handle`. It carries the
version-agnostic scene helpers this fork depends on.

- Fresh clone needs `--recurse-submodules` (or `git submodule update --init`).
- Commit submodule bumps as part of the change that needs them.
- **`UniverseLib/build.ps1` writes into `UniverseLib/Release/`, which the main
  project references — see §4.1.**

---

## 4. Building from source

### 4.1 The critical ordering trap

**Build UniverseLib before UnityExplorer.** A fresh clone does *not* build
directly:

```bash
dotnet build src/UnityExplorer.sln -c Release_BIE_Unity_Cpp
# → 385 errors: 'InputFieldRef', 'UIBase', 'PanelBase', 'ButtonRef' not found
```

The cause is not a code problem. `src/UnityExplorer.csproj` references
`..\UniverseLib\Release\UniverseLib.Il2Cpp.Interop\UniverseLib.BIE.IL2CPP.Interop.dll`,
which **does not exist in a fresh clone** — it is a build output, not a source
file.

**The csproj tries to build UniverseLib for you, but the hook is dead.** It
defines:

```xml
<Target Name="PreBuild" BeforeTargets="PreBuildEvent">
  <Exec Command="dotnet build ..\UniverseLib\src\UniverseLib.sln -c Release_IL2CPP_Interop_BIE" />
</Target>
```

`PreBuildEvent` is a **legacy (non-SDK) project hook** and nothing in this
SDK-style project defines it, so the target never runs. Verified on a clean
clone: `UniverseLib/Release/` stays absent after a build attempt, and the 385
errors are unchanged. Do not rely on it — build UniverseLib yourself.

`build.ps1` handles this correctly as its very first step, which is why the
problem only appears when building by hand.

The fix, and the minimum viable build for the IL2CPP CoreCLR target:

```bash
# 1. UniverseLib first (this is the step everyone misses)
dotnet build UniverseLib/src/UniverseLib.sln -c Release_IL2CPP_Interop_BIE

# 2. UnityExplorer
dotnet build src/UnityExplorer.sln -c Release_BIE_Unity_Cpp
```

Useful UniverseLib configs, matched to the UnityExplorer config:

| UnityExplorer config | UniverseLib config |
|---|---|
| `Release_BIE_Unity_Cpp` | `Release_IL2CPP_Interop_BIE` |
| `Release_BIE_CoreCLR` | `Release_IL2CPP_Interop_BIE` |
| `Release_ML_Cpp_*` | `Release_IL2CPP_Interop_ML` |
| `Release_BIE_Cpp` (Unhollower) | `Release_IL2CPP_Unhollower` |
| `Release_*_Mono` | `Release_Mono` |

### 4.2 The full build

`build.ps1` runs all 13 configurations, each ending in an ILRepack merge and a
zip. Run it from the repository root in PowerShell:

```powershell
.\build.ps1
```

Artifacts land in `Release/<ConfigurationName>/` plus a `.zip` per config.

### 4.3 The 13 configurations

```
Release_BIE_Unity_Cpp            BepInEx 6 Unity IL2CPP CoreCLR  ← most used
Release_BIE_CoreCLR              BepInEx 6 IL2CPP CoreCLR
Release_BIE_Cpp                  BepInEx 6 IL2CPP (Unhollower)
Release_BIE5_Mono                BepInEx 5 Mono
Release_BIE6_Mono                BepInEx 6 Mono
Release_BIE6_Unity_Mono          BepInEx 6 Unity Mono
Release_ML_Cpp_CoreCLR           MelonLoader IL2CPP CoreCLR
Release_ML_Cpp_net6preview       MelonLoader IL2CPP net6 preview
Release_ML_Cpp_net472            MelonLoader IL2CPP net472
Release_ML_Mono                  MelonLoader Mono
Release_STANDALONE_Mono          Standalone Mono
Release_STANDALONE_Cpp           Standalone IL2CPP (Unhollower)
Release_STANDALONE_Cpp_CoreCLR   Standalone IL2CPP CoreCLR
```

Target frameworks: `net35`, `net472`, `net6`. The toolchain used here is .NET SDK
8.x.

### 4.4 The merge step is mandatory

**`dotnet build` alone produces a DLL that will not work.** The deployable
artifact is the output of the ILRepack step, which merges `mcs.dll` (the C#
console's compiler) and `Tomlet.dll` (config) into the main assembly with
`/internalize`.

A raw `dotnet build` output is ~450 KB; the merged DLL is ~1.95 MB. If the size
looks wrong, the merge did not run.

For `Release_BIE_Unity_Cpp`:

```bash
R="Release/UnityExplorer.BepInEx.Unity.IL2CPP.CoreCLR"

MSYS_NO_PATHCONV=1 ./lib/ILRepack.exe /target:library \
  /lib:lib/net472/BepInEx/build647+ /lib:lib/net6/ /lib:lib/interop/ \
  /lib:"$R" /internalize \
  /out:"$R/UnityExplorer.BIE.Unity.IL2CPP.CoreCLR.dll" \
  "$R/UnityExplorer.BIE.Unity.IL2CPP.CoreCLR.dll" "$R/mcs.dll" "$R/Tomlet.dll"
```

Two traps in that command:

- **`MSYS_NO_PATHCONV=1` is required under Git Bash.** Without it, MSYS rewrites
  `/internalize` into a Windows path and ILRepack fails with
  `Failed to load assembly C:/Program Files/Git/internalize`.
- **`/lib:` paths must include every reference root** the merge needs
  (`lib/interop/`, `lib/net6/`, the config's own output folder). Omitting one
  produces unresolved-reference warnings and a broken DLL.

### 4.5 After a build: `lib/interop/` is mutated

The build moves `Il2CppInterop.Common.dll` and `Il2CppInterop.Runtime.dll` out of
`lib/interop/` and into `Release/`. A second build then fails until they are
restored:

```bash
cp Release/UnityExplorer.BepInEx.Unity.IL2CPP.CoreCLR/Il2CppInterop.*.dll lib/interop/
```

These two are also `PackageReference`s in the csproj, so a clean clone restores
them via NuGet — but the local `lib/interop/` copies are what the ILRepack
`/lib:` path uses.

---

## 5. The MCP server

This is the fork's main addition. It runs **inside the game process** — no
Node.js, no stdio adapter, no external bridge.

### 5.1 Architecture

```
MCP client
    │ JSON-RPC 2.0 over SSE or Streamable HTTP
    ▼
McpHttpBridge          loopback-only TcpListener, bearer auth, size/queue limits
    │ validate → enqueue
    ▼
McpManager             per-frame pump, config surface, lifecycle
    │ drain on Unity main thread
    ▼
McpGameCapabilityExecutor   the 13 tools; permission checks; registry
    ├── McpValueCodec        JSON ⇄ CLR/Unity/IL2CPP conversion, serialization
    ├── McpMemberPath        nested paths, `items[0].value`, index read/write
    ├── McpReflection        IL2CPP-aware member discovery
    └── McpObjectRegistry    session-local handles
```

**All UnityEngine access happens on the main thread.** Network threads only
parse, authorize and enqueue. Never touch Unity APIs from a worker thread.

### 5.2 Files

| File | Responsibility |
|---|---|
| `McpManager.cs` | Lifecycle, config, per-frame pump, public API |
| `Runtime/McpNativeProtocolHandler.cs` | `initialize`, `ping`, `tools/list`, `tools/call`; **tool schemas live here** |
| `Runtime/McpGameCapabilityExecutor.cs` | Command dispatch, permission enforcement (`EnforcePolicy`), parameter normalization/aliases |
| `Runtime/McpGameQueryCommands.cs` | `list_scenes`, `search_objects`, `get_object`, `get_member`, `list_methods` |
| `Runtime/McpGameMutationCommands.cs` | `set_member`, `set_transform`, `set_enabled` |
| `Runtime/McpGameInvokeCommand.cs` | `invoke_method`, overload selection, generics |
| `Runtime/McpGameLifecycleCommands.cs` | `create_object`, `destroy_object` |
| `Runtime/McpValueCodec.cs` | Serialization + `ConvertTo`; also hosts `McpReflection` |
| `Runtime/McpMemberPath.cs` | Path parsing, index get/set |
| `Runtime/McpObjectRegistry.cs` | Handles, liveness, pruning, caps |
| `Runtime/McpJsonValue.cs` | Dependency-free JSON value model |
| `Transport/McpHttpBridge.cs` | HTTP server, auth, limits |
| `Transport/JsonWire.cs` | JSON scanner (works on net35 / IL2CPP) |
| `Transport/McpRequestRouter.cs` | Method → handler routing |

### 5.3 The 13 tools

`get_status`, `list_scenes`, `search_objects`, `get_object`, `get_member`,
`list_methods`, `set_member`, `set_transform`, `set_enabled`, `create_object`,
`destroy_object`, `invoke_method`, `execute_batch`.

**`tools/list` is authoritative** for the running build. Parameter-level detail,
response shapes and error codes are in
**[docs/MCP_CAPABILITIES.md](docs/MCP_CAPABILITIES.md)** — read that before
changing or calling tools.

### 5.4 Permission model

| Operation | Read-only on | Read-only off, dangerous off | Both off |
|---|---:|---:|---:|
| Reads (`get_*`, `list_*`, `search_*`) | Allowed | Allowed | Allowed |
| `set_member`, `set_transform`, `set_enabled` | Denied | Allowed | Allowed |
| `invoke_method`, `create_object`, `destroy_object` | Denied | Denied | Allowed |
| `execute_batch` | Per sub-operation | Per sub-operation | Per sub-operation |

Enforced in `McpGameCapabilityExecutor.EnforcePolicy`, **not** by the schema, so
`execute_batch` cannot bypass it. `MCP Allow Dangerous Operations` resets to
`false` on every process start.

### 5.5 MCP configuration keys

All in `ConfigManager.cs`, surfaced in the MCP panel:

```
MCP Enabled                      MCP Read Only
MCP Transport Mode               MCP Allow Dangerous Operations
MCP Bind Address                 MCP Request Logging
MCP Port                         MCP Request Timeout Milliseconds
MCP RPC Path                     MCP Max Request Body Bytes
MCP Health Path                  MCP Max Pending Requests
MCP Auth Token                   MCP Max Requests Per Frame
MCP Require Token For Health
```

Defaults: disabled, `127.0.0.1`, port `17891`, `/mcp`, `/health`, read-only on,
dangerous off, timeout 30000 ms, 1 MiB body, 128 pending, 16 per frame.

### 5.6 Runtime limits

Set in `McpGameExecutorOptions` (`McpObjectRegistry.cs`):

| Limit | Value |
|---|---|
| Search results / scene list | 200 |
| Snapshot depth | 3 |
| Shared serialization budget | 1024 items |
| Members per object | 512 |
| Children / components per object | 128 |
| Batch commands | 64 |
| Registry handles | 8192 (oldest evicted first) |

Tool schemas derive their advertised bounds from these, so the schema and the
executor cannot disagree.

---

## 6. Conventions and traps learned the hard way

### 6.1 Schema names must be aliased or they are silently ignored

The tool schemas use **snake_case**; the implementation reads **camelCase**.
`NormalizeParameters` in `McpGameCapabilityExecutor.cs` bridges them with
`CopyAlias`. A parameter declared in a schema but never aliased is **silently
dropped** — no error, the default is used instead.

This bug appeared **five times** in one session (`value_type`,
`generic_type_arguments`, `include_members`, `member_filter`,
`max_collection_items`). When adding a parameter:

1. Add the `Prop(...)` to the schema in `McpNativeProtocolHandler.cs`.
2. Add a `CopyAlias(copy, "snake_name", "camelName")` if it is multi-word.
3. Read it in the command.
4. **Verify live** — a passing build proves nothing here.

### 6.2 IL2CPP collections do not implement BCL interfaces

`Il2CppSystem.Collections.Generic.List<T>` and `Il2CppArrayBase<T>` subclasses
implement a **shadow** `Il2CppSystem.Collections.IList`, unrelated to
`System.Collections.IList`. `owner as IList` returns null. Indexing goes through
reflected `Count` + int indexer — see `McpReflection.IsIl2CppIndexable` and
`McpMemberPath.TryReadIl2CppIndex`.

### 6.3 Casting IL2CPP objects

`obj as GameObject` is a plain CLR cast and **fails** for IL2CPP wrappers whose
managed type is not exactly `GameObject`. Use:

```csharp
Type actual = McpReflection.GetActualType(obj);   // asks UniverseLib for the native type
GameObject go = obj.TryCast<GameObject>();        // native interop cast
```

This is why `GetActualType` exists: IL2CPP frequently returns a base wrapper, and
members declared on the derived generated class are invisible without it.

### 6.4 Emit no case-colliding JSON keys

A property and its backing field (`AlertNotifEnabled` / `_alertNotifsEnabled`),
or a field `Method` beside a property `method`, produce keys differing only in
case. **PowerShell 7.6 `ConvertFrom-Json` throws** on this, and so do strict
parsers in Rust, Go and some JS configs. `McpValueCodec.AddMembers` de-duplicates
case-insensitively.

### 6.5 Do not hold strong references to IL2CPP wrappers

`McpObjectRegistry` pins handles. `IsDestroyed` only recognises
`UnityEngine.Object`, so a **managed** wrapper is never reported destroyed and
`Prune()` cannot reclaim it. An unbounded registry grows until the game dies with
**no managed exception and no log stack**. The registry is capped at 8192
entries with oldest-first eviction — keep it bounded if you change it.

### 6.6 Unity overloads `==` for destroyed objects

```csharp
// WRONG — returns a destroyed reference, not the fallback
var x = maybeDestroyed ?? fallback;
// RIGHT
var x = maybeDestroyed != null ? maybeDestroyed : fallback;
```

### 6.7 Reflection safety

`McpReflection.IsUnsafeMember` filters UnityExplorer's blacklist and the
`NativeFieldInfoPtr_*` / `NativeMethodInfoPtr_*` interop members. **Do not bypass
it.** Property getters and `ToString()` execute arbitrary game code — treat
"read" as "no writes", not "no side effects".

### 6.8 `includeInherited` needs `FlattenHierarchy` cleared

`BindingFlags.DeclaredOnly` OR-ed onto flags that already contain
`FlattenHierarchy` is ignored by the binder, making the filter a silent no-op:

```csharp
if (!includeInherited) flags = (flags & ~BindingFlags.FlattenHierarchy) | BindingFlags.DeclaredOnly;
```

### 6.9 Out-of-range integers clamp, do not revert

`McpJsonValue.GetInt32` clamps to `[minimum, maximum]`. Reverting to the default
made larger requests return **fewer** results (`limit=201` → 50 while `limit=200`
→ 200).

---

## 7. Deploying to a game

### 7.1 What to copy

Into `BepInEx/plugins/sinai-dev-UnityExplorer/`:

```
UnityExplorer.BIE.Unity.IL2CPP.CoreCLR.dll   ← the MERGED DLL
UniverseLib.BIE.IL2CPP.Interop.dll
```

`mcs.dll` and `Tomlet.dll` are **not** needed — they are internalized into the
merged assembly. Copies in an existing install are leftovers.

Requires **BepInEx 6 be.647+** for IL2CPP CoreCLR.

### 7.2 The game locks the DLL

While the game is running, the plugin DLL is locked and cannot be overwritten
(`Device or resource busy`). Close the game, deploy, then relaunch. A running
game keeps executing the **old** build even after the file on disk changes.

Always confirm which build is live:

```bash
# compare the deployed DLL against your build
sha256sum "<game>/BepInEx/plugins/sinai-dev-UnityExplorer/UnityExplorer.BIE.Unity.IL2CPP.CoreCLR.dll"
```

Or check a symbol unique to your change is present in the deployed file.

### 7.3 BepInEx scans `plugins/` recursively

Do **not** put backups inside `BepInEx/plugins/`. BepInEx discovers DLLs
recursively, so a "disabled" plugin in `plugins/backup/` still loads, and a
duplicate copy of UnityExplorer triggers
`Skipping [UnityExplorer 4.13.5] because a newer version exists`. Keep backups in
`BepInEx/backup/` instead.

### 7.4 Standalone / Editor

`ExplorerStandalone.CreateInstance()` after loading UniverseLib, HarmonyX and
MonoMod. See README for the Unity Editor package.

---

## 8. Verifying a change

A clean build proves almost nothing about MCP correctness. Most of the bugs found
in this fork were schema/implementation mismatches that compiled fine.

**Required loop:**

1. Build and merge (§4.4).
2. Deploy with the game closed (§7.2).
3. Confirm the new build is actually live — compare a hash or a unique symbol.
4. Exercise the change over the bridge and **read back the result**.
5. Check for regressions in neighbouring tools.

### Talking to the bridge

```bash
curl -s http://127.0.0.1:17891/mcp \
  -H "Authorization: Bearer $TOKEN" \
  -H "Content-Type: application/json" \
  -H "Accept: application/json, text/event-stream" \
  -d '{"jsonrpc":"2.0","id":1,"method":"tools/list","params":{}}'
```

`Content-Length` is required; chunked request bodies get `501`.

### Regression checks worth running

```
get_status                          bridge alive, correct runtime
list_scenes                         scene enumeration + handle conversion
search_objects  kind=component      kind selects the search type
get_object      depth=1             members + memberInfo kinds
get_member      member_path="name"  lowercase members reachable
get_object      member_filter=...   filter applied BEFORE truncation
search_objects  includeExplorer=false    no com.sinai.* leakage
get_object      type=<T>            static view without an instance
```

Parse responses with a **strict** JSON parser — that is how the case-collision
bug was caught.

### Safety when probing a live game

- The game may be **online with EasyAntiCheat**. Assume ban risk.
- Never write unless the task needs it, and always revert + read back.
- `invoke_method` can execute arbitrary game code. Do not call methods whose name
  implies mutation (`Close*`, `Set*`, `Remove*`) as a "harmless" probe.
- Prefer `get_member` by name over deep `get_object` reads.
- After a crash, the log often ends with **no managed exception** — that points at
  a native/IL2CPP fault, not a C# error.

---

## 9. Documentation map

| Document | Contents |
|---|---|
| `README.md` | Upstream feature overview, install per loader, building |
| `docs/MCP.md` | Architecture, setup, client config, tool list, troubleshooting |
| `docs/MCP_CAPABILITIES.md` | **Per-tool parameter reference**, response shapes, error codes, verification status |
| `docs/MCP_SECURITY.md` | Threat model, baseline policy, release checklist |
| `status.md` | Session notes: current state, open issues, verified behaviours |

`status.md` is a working log, not a specification — it may lag the code. Prefer
the code and live behaviour, and update it when you finish a session.

---

## 10. Known limitations

- **Generic method invocation is unverified end-to-end.** The alias and listing
  are fixed, but a successful `GetComponent<Transform>()` has not been observed.
- **Deeper `depth` can return fewer members** than a shallower one. The shared
  serialization budget is consumed by traversal, so a deep walk truncates the
  top-level member list early. `depth` above 3 is clamped.
- **Unreadable Unity struct members.** `Matrix4x4.determinant` / `.isIdentity`
  throw `InvalidProgramException` — IL2CPP cannot run those managed bodies. They
  are reported per-member, and the snapshot still returns.
- **Delegate parameters are unsupported** by `invoke_method`. A delegate needs a
  callable target, not an object reference.
- **Enums serialize as name strings**, so the runtime enum type is not visible in
  the value; `memberInfo.declaredType` carries it.
- **IL2CPP-stripped code is unreachable**, and a managed wrapper can outlive its
  native object.

---

## 11. Quick reference

```bash
# Clone
git clone --recurse-submodules -b fix/unity-6.3-scene-handle \
  https://github.com/barankrky/UnityExplorer.git

# Build (UniverseLib FIRST — see §4.1)
dotnet build UniverseLib/src/UniverseLib.sln -c Release_IL2CPP_Interop_BIE
dotnet build src/UnityExplorer.sln -c Release_BIE_Unity_Cpp

# Merge into the deployable DLL (see §4.4 for traps)
R="Release/UnityExplorer.BepInEx.Unity.IL2CPP.CoreCLR"
MSYS_NO_PATHCONV=1 ./lib/ILRepack.exe /target:library \
  /lib:lib/net472/BepInEx/build647+ /lib:lib/net6/ /lib:lib/interop/ \
  /lib:"$R" /internalize \
  /out:"$R/UnityExplorer.BIE.Unity.IL2CPP.CoreCLR.dll" \
  "$R/UnityExplorer.BIE.Unity.IL2CPP.CoreCLR.dll" "$R/mcs.dll" "$R/Tomlet.dll"

# Restore lib/interop after a build moved the interop DLLs out
cp "$R"/Il2CppInterop.*.dll lib/interop/

# Full build of all 13 configurations (PowerShell)
./build.ps1

# Push (fork, NOT origin)
git push fork HEAD:fix/unity-6.3-scene-handle
```

Expected merged DLL size for `Release_BIE_Unity_Cpp`: **~1.95 MB**. A ~450 KB
output means the merge step did not run.
