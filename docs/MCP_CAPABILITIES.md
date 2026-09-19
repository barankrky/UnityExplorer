# MCP Capability Reference

A field-by-field reference for every MCP tool: what each parameter does, what the
response contains, and what is verified against a real IL2CPP game. For setup,
security, and the threat model see [MCP.md](MCP.md) and
[MCP_SECURITY.md](MCP_SECURITY.md).

Everything here was verified live against Marvel Contest of Champions on Unity
`6000.3.10f1` (IL2CPP). Where a behaviour is not verified, it says so.

## 1. The agent's view of an object

The UnityExplorer Inspector shows an object as rows grouped by **Property /
Field / Method / Constructor**, with an **All / Instance / Static** scope
control, a declared type per row, and wrapped values rendered unwrapped
(`True (EB.SafeBool)`). The MCP surface mirrors that model:

| Inspector concept | MCP equivalent |
|---|---|
| Property / Field buttons | `memberInfo[name].kind` = `"property"` or `"field"` |
| All / Instance / Static | `include_static` on `get_object`; statics only by default for a type-only call |
| Declaring type prefix (`Profile.`) | `memberInfo[name].declaredBy`, and `declaredType` |
| Method / Constructor rows | `list_methods` with `include_constructors`; `kind` per entry |
| `True (EB.SafeBool)` | `{"objectId": "...", "type": "EB.SafeBool", "value": true}` |
| Object Explorer tree + Sibling Index | `children[]` with `siblingIndex`, `parentId`, `path` |
| Component list | `components[]` and `componentCount` |

### 1.1 `memberInfo`

`get_object` returns a sibling map alongside `members`, keyed by member name:

```json
"memberInfo": {
  "AlertNotifEnabled":   { "kind": "property", "declaredType": "System.Boolean", "declaredBy": "Profile" },
  "_alertNotifsEnabled": { "kind": "property", "declaredType": "EB.SafeBool",  "declaredBy": "Profile" },
  "Instance":            { "kind": "property", "declaredType": "Profile", "declaredBy": "Profile", "static": true }
}
```

Without this map a property and its backing field are indistinguishable — both
appear as bare names with no indication of which is authoritative. `static` is
only present when true.

`member_filter` narrows `members` and `memberInfo` together, so the map never
describes a member that was removed.

### 1.2 Wrapper unwrapping

Wrapper types such as `EB.SafeBool` and `EB.SafeInt` carry a primitive payload.
At the depth boundary they are inlined as `value`, so an agent can read the
actual boolean without a second round trip:

```json
"_alertNotifsEnabled": { "objectId": "m197", "type": "EB.SafeBool", "value": true }
```

Only primitive, string, and enum payloads are inlined. Anything else stays an
opaque handle, so this cannot recurse or expand unbounded.

## 2. Tools

Thirteen tools. `tools/list` is authoritative for the running build; this
section explains the parameters.

### 2.1 `get_status`

No parameters. Returns bridge state, `unityVersion`, `application`, `runtime`
(`IL2CPP` or `Mono`), `sceneCount`, `registryCount`, `prunedObjectIds`, and a
`capabilities` array.

### 2.2 `list_scenes`

| Parameter | Type | Default | Notes |
|---|---|---|---|
| `include_unloaded` | boolean | `false` | Adds scenes in build settings that are not loaded. Their `handle` is `null` because they have no runtime handle. |
| `include_special` | boolean | `false` | Adds the synthetic scenes the Object Explorer lists: `DontDestroyOnLoad` (handle `-12`) and `HideAndDontSave` (handle `-1`), marked `special: true`. |
| `limit` | integer 1–200 | scene count | **Set this explicitly when using the two flags above**, otherwise the default limit equals the scene count and the additions are silently cut. |

Each entry: `handle`, `name`, `path`, `buildIndex`, `loaded`, `valid`, `active`,
`rootCount`. Verified: `include_special` adds `DontDestroyOnLoad`;
`include_unloaded` adds `1_boot`, `2_gameboard`, `5_masteries`.

### 2.3 `search_objects`

| Parameter | Type | Default | Notes |
|---|---|---|---|
| `query` | string | — | Case-insensitive substring on the object name. |
| `scene` | string | — | Scene name or handle. |
| `type` | string | `UnityEngine.GameObject` | Must derive from `UnityEngine.Object`. |
| `kind` | string | — | `gameobject`, `component`, `scriptableobject`, or `asset`. **Selects the search type** when `type` is omitted. Unknown values are rejected with `invalid_kind`. |
| `path` | string | — | Case-insensitive substring on the hierarchy path. |
| `include_inactive` | boolean | `true` | `false` hides objects inactive in the hierarchy. |
| `include_explorer` | boolean | `false` | `true` also returns UnityExplorer's own UI objects. |
| `exact` | boolean | `false` | Exact name match instead of substring. |
| `limit` | integer 1–200 | 50 | |

Returns `objects[]`, `count`, `scanned`, `limited`. Each object carries
`objectId`, `instanceId`, `name`, `type`, and — when the GameObject resolves —
`gameObjectId`, `path`, `scene`, `activeSelf`, `activeInHierarchy`.

`kind` and `include_inactive` were previously declared but ignored, and `kind`
was additionally broken: it filtered the default *GameObject* search, so
`kind=component` always returned zero. Verified fixed.

### 2.4 `get_object`

Accepts **either** `object_id` (an instance) **or** `type` (a static view of a
class, the Inspector's `[S]` tab).

| Parameter | Type | Default | Notes |
|---|---|---|---|
| `object_id` | string | — | Handle from `search_objects`. |
| `type` | string | — | Type name for a static view. |
| `depth` | integer 0–3 | 2 | Recursion depth. Above 3 is clamped. |
| `include_members` | boolean | `true` | `false` omits `members` and `memberInfo`. |
| `include_methods` | boolean | `false` | `true` adds a `methods` array (same shape as `list_methods`). |
| `include_static` | boolean | `false` | Include static fields and properties. Implied when `type` is used without an instance. |
| `member_filter` | string | — | Case-insensitive substring; keeps only matching members. Applied **before** truncation. |
| `max_collection_items` | integer 1–1024 | 1024 | Bounds the shared serialization budget. |

For a **GameObject** the response additionally includes `path`, `parentId`,
`childCount`, `siblingIndex`, `children[]`, `componentCount`, and `components[]`:

```json
"children": [ { "objectId": "u-...", "name": "PoolHolder", "siblingIndex": 0, "childCount": 6, "activeSelf": true } ],
"components": [ { "objectId": "u-...", "type": "UnityEngine.Transform" } ]
```

These are plain reads and work under the default read-only policy. Children and
components are capped at 128 per object with a `$truncated` marker.

**Truncation.** When a type has more members than the per-object cap, `members`
contains `$truncated: true` and `$truncatedMembers: <count dropped>`. Members
are sorted ordinally, where `A-Z` < `_` < `a-z`, so a cap that is too low drops
lowercase-initial members first — which is where `name`, `tag`, `transform`,
and `gameObject` sort. The default cap (512) is sized so a typical game
component (`Profile` has ~150 members) is returned in full.

`member_filter` is evaluated while walking members, before the cap, so it can
select members that alphabetical truncation would otherwise discard. Verified:
`member_filter="name"` on a 150-member type returns `name` rather than an empty
set.

### 2.5 `get_member`

| Parameter | Type | Default | Notes |
|---|---|---|---|
| `object_id` / `type` | string | — | Instance or static view. |
| `member_path` | string | **required** | Dotted path with `[n]` indexing, e.g. `windowStacks._buckets[0]`, `WindowsFlowStack._items[0].name`. |
| `depth` | integer 0–3 | 2 | |
| `max_items` | integer 1–1024 | 1024 | |

Errors: `invalid_member_path` (empty, malformed, negative index),
`member_not_found`, `null_member` (path dereferences null),
`index_out_of_range`, `not_indexable`, `read_only_collection`.

Indexing works across managed arrays, `Il2CppReferenceArray<T>`,
`Il2CppStringArray`, `Il2CppStructArray`, and `Il2CppSystem.Collections.Generic.List<T>`,
which implement a shadow `IList` unrelated to the BCL interface. Verified:
`windowStacks._buckets` (37 elements, valid 0–36), `_entries[0].hashCode`,
`WindowsFlowStack._items[0].name` → `"BusyBlocker"`.

### 2.6 `list_methods`

| Parameter | Type | Default | Notes |
|---|---|---|---|
| `object_id` / `type` | string | — | Instance or static view. |
| `name_filter` | string | — | Substring on the method name. |
| `include_non_public` | boolean | `true` | |
| `include_inherited` | boolean | `true` | `false` restricts to methods declared on the type itself. |
| `include_static` | boolean | `true` | |
| `include_constructors` | boolean | `false` | Adds `.ctor` / `.cctor` rows. |
| `limit` | integer 1–1024 | 512 | |

Each entry: `name`, `kind` (`method` / `constructor`), `signature`,
`returnType` (null for constructors), `declaredBy`, `inherited`, `static`,
`public`, `generic` (present when the method is an open generic definition),
and `parameters[]` with `name`, `type`, `optional`, `out`.

`include_inherited` was previously a silent no-op: `DeclaredOnly` was OR-ed onto
flags that already contained `FlattenHierarchy`, which the binding ignores.
Verified fixed (300 → 184 signatures for one component).

### 2.7 `set_member`

| Parameter | Type | Notes |
|---|---|---|
| `object_id` / `type` | string | Instance or static view. |
| `member_path` | string | **required** |
| `value` | any | **required** |
| `value_type` | string | Optional expected type, validated against the member. Mismatch reports `value_type_mismatch`. Numeric widening is accepted. |

Verified: writes to `windowStacks._buckets[0]` round-trip, and `value_type` of
`System.Int32` against a `bool` member is rejected.

### 2.8 `set_transform`

`object_id` **required**. Optional: `position`, `local_position`, `rotation`,
`local_rotation`, `euler_angles`, `local_euler_angles`, `local_scale` (all
`{x,y,z}` / `{x,y,z,w}` objects), `parent_id`, `world_position_stays`.

### 2.9 `set_enabled`

`object_id` and `enabled` **required**. Works on a GameObject (`SetActive`), a
`Behaviour` (`.enabled`), or any object exposing a writable `bool enabled`.

### 2.10 `create_object`

Optional: `name`, `primitive` (a `PrimitiveType` name), `parent_id`,
`world_position_stays`, `components` (array of component type names).

### 2.11 `destroy_object`

`object_id` **required**; optional `immediate` (uses `DestroyImmediate`).

### 2.12 `invoke_method`

| Parameter | Type | Notes |
|---|---|---|
| `object_id` / `type` | string | Instance or static view. |
| `method` | string | **required** |
| `arguments` | array | Converted to CLR values. |
| `overload` | string | Selects among same-named methods by matching against the full signature or the parameter type list. |
| `generic_type_arguments` | array of string | Type arguments used to close a generic method. |

Returns `method` (the resolved signature), `returnValue`, and `outArguments`
when the method has `ref`/`out` parameters.

Verified: parameterless calls return real values; `overload` genuinely narrows
the attempted set (with `IsInStack`, selecting the wrong overload left **no**
overload attempted, versus the other overload being tried without a selector).
Generic *invocation* is implemented but **not verified end-to-end** — it
requires the dangerous-operations policy, which was off for the final test.

### 2.13 `execute_batch`

`operations` **required**: an array of `{method, params}`. Optional
`stop_on_error` (default `true`). `atomic: true` is rejected with
`atomic_not_supported`.

Batches are **ordered and non-atomic**: completed operations are not rolled
back. Each sub-operation is permission-checked individually, so a batch cannot
bypass read-only mode.

## 3. Permission model

| Operation | Read-only on | Read-only off, dangerous off | Both off |
|---|---:|---:|---:|
| `get_status`, `list_scenes`, `search_objects`, `get_object`, `get_member`, `list_methods` | Allowed | Allowed | Allowed |
| `set_member`, `set_transform`, `set_enabled` | Denied | Allowed | Allowed |
| `invoke_method`, `create_object`, `destroy_object` | Denied | Denied | Allowed |
| `execute_batch` | Per sub-operation | Per sub-operation | Per sub-operation |

Enforcement is in the executor, not the schema. `MCP Allow Dangerous
Operations` is reset to off on every process start.

## 4. Limits

| Limit | Value |
|---|---|
| `search_objects` / `list_scenes` `limit` | 200 |
| `get_object` / `get_member` `depth` | 3 |
| Shared serialization budget | 1024 items |
| Members per object | 512 |
| Children / components per object | 128 |
| Batch commands | 64 |

The tool schemas derive these bounds from `McpGameExecutorOptions`, so the
advertised range always matches what the executor enforces. Out-of-range
integers are **clamped**, not reset to a default — a `limit` above the cap
returns the cap, never fewer results than a smaller request.

## 5. Error codes

| Code | Meaning |
|---|---|
| `object_not_found` | Handle is unknown, or the object was destroyed. Re-search. |
| `member_not_found` | No readable/writable member by that name. |
| `null_member` | The path dereferenced null before reaching the member. |
| `invalid_member_path` | Empty, malformed, or negative index. |
| `index_out_of_range` | Index outside `0..Count-1`. |
| `not_indexable` | Value is not an array or an indexable collection. |
| `read_only_collection` | Collection exposes no writable indexer. |
| `value_type_mismatch` | `value_type` disagrees with the member's type. |
| `conversion_failed` | JSON cannot be converted to the target CLR type. |
| `invalid_target` | Neither `object_id` nor `type` was supplied. |
| `invalid_kind` | Unknown `kind` value. |
| `read_only` | Mutation blocked by read-only mode. |
| `dangerous_operations_disabled` | Blocked by the dangerous-operations switch. |
| `method_not_found` | No compatible overload succeeded; the message lists per-overload reasons. |
| `atomic_not_supported` | `execute_batch` with `atomic: true`. |

## 6. Verification status

Verified live on `6000.3.10f1` IL2CPP: protocol negotiation and token auth
(401 on missing and wrong token), `get_status`, `list_scenes` (including
`include_special` / `include_unloaded`), `search_objects` (`kind`,
`include_inactive`, `include_explorer`, `path`), `get_object` (member kinds,
statics, `member_filter` before truncation, hierarchy, components, wrapper
unwrapping, `include_methods`), `get_member` (IL2CPP indexing, static reads by
type), `list_methods` (constructors, `include_inherited`, generic listing),
`set_member` (index writes, `value_type` validation), `create_object` /
`destroy_object` round-trip, `execute_batch`, and the read-only policy.

Not verified end-to-end: generic method *invocation* with
`generic_type_arguments`, and `set_transform` / `set_enabled` / `invoke_method`
beyond the checks noted above.
