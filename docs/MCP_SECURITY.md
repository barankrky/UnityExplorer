# MCP Security and Threat Model

This document defines the security boundary, default policy, and release checklist for UnityExplorer's MCP support. MCP holds roughly the privileges of an in-game debugger; once arbitrary reflection writes or method invocation are enabled, it must be treated as an interface that can execute high-privilege operations inside the current game process, not as an ordinary telemetry API.

## 1. Assets and trust boundary

Assets to protect:

- the integrity, stability, and recoverable state of the game process;
- saves, configuration, screenshots, and local files the game can reach;
- user accounts, multiplayer sessions, and anti-cheat state;
- the MCP token, object data, logs, and runtime strings that may contain private data;
- host CPU, memory, disk, and the Unity main-thread frame budget.

Trust boundary:

1. Untrusted natural language, web pages, game text, and model output enter the agent.
2. The MCP client connects over loopback to the SSE or Streamable HTTP server built into the UnityExplorer DLL.
3. The in-DLL network layer performs session management, authentication, protocol parsing, size limits, and request queuing.
4. Network threads hand validated requests to the Unity main thread.
5. Reflection and Unity APIs cross managed, IL2CPP, and native object boundaries.
6. Screenshots, exports, saves, and logs cross from the game process to the filesystem.

This design has no dependency on Node.js, a stdio adapter, or any external forwarding component. Authentication and permission checks are performed entirely by the in-DLL server and the in-game runtime executor.

**Untrusted input includes arguments the agent generates itself.** Prompt injection can arrive through object names, component strings, in-game chat, web pages, or user-supplied scripts. The server must not skip schema, permission, or scope validation merely because a call came from a "trusted agent".

## 2. Security objectives

- Not remotely reachable by default; unauthenticated requests denied by default.
- Read-only by default, dangerous operations off by default, and dangerous authorization never survives a restart.
- All Unity operations run on the main thread, bounded by timeouts, queue limits, and a per-frame budget.
- Tools follow least privilege with an explicit allowlist; unknown tools, types, members, and arguments are denied by default.
- Writes are auditable and verifiable; high-impact operations require per-operation user approval.
- Tokens, sensitive object data, and file contents do not enter ordinary logs.
- Client disconnects and timeouts do not produce infinite retries or duplicated side effects.

## 3. Baseline policy (MUST)

### Network and authentication

- The MCP server **MUST** listen on `127.0.0.1` only. It must not bind `0.0.0.0` or a non-loopback interface. Non-loopback peers are rejected at the socket level even if a request somehow arrives.
- The RPC endpoint **MUST** require a high-entropy token.
- HTTP method, `Content-Type`, `Accept`, request body size, header size, JSON depth, string length, and response size **MUST** all be bounded.
- SSE sessions **MUST** have connection, idle, write-queue, and lifetime limits; disconnecting must release the session and its network resources.
- Streamable HTTP **MUST NOT** fabricate or claim an `Mcp-Session-Id` while operating statelessly.
- Switching the transport in the UI **MUST** restart the server so the old listener and old sessions become invalid. Two modes must never share unvalidated session state.
- Tokens **MUST NOT** be accepted from URL query parameters and **MUST NOT** be written to logs.
- A browser-reachable HTTP implementation **MUST NOT** enable permissive CORS; unexpected `Origin` headers are denied so a malicious web page cannot drive the local port.
- The health endpoint **MUST NOT** leak tools, objects, tokens, or detailed exceptions. It can optionally require the token.

### Permissions

- Startup defaults are read-only on and dangerous operations off.
- Read-only mode **MUST** be enforced by the server, not merely by hiding UI controls or trusting client descriptions.
- Every tool **MUST** carry a server-side permission classification; unknown classifications are treated as dangerous and denied.
- The dangerous-operations switch **MUST** be clearly visible in the UI and **MUST** be reset to off on every process start.
- Reflection members, instantiable types, writable paths, and export directories **MUST** respect UnityExplorer's existing reflection blacklist. MCP must not bypass it.

### Execution and resource control

- Requests are validated and queued by network threads, then executed on the Unity main thread.
- Per-frame execution count, queue capacity, and per-request timeout are bounded and configurable.
- A request that times out may already have executed. Non-idempotent operations must not be retried automatically.
- Batches are ordered and non-atomic; permission checks apply to every sub-operation.

## 4. Threat model

### 4.1 Malicious or compromised MCP client

A local process or a compromised client could attempt to reach the port. Mitigations: loopback-only binding, mandatory bearer token, `Origin` rejection, and request size limits.

### 4.2 Prompt injection through game data

Object names, component text, and in-game chat can contain instructions aimed at the agent. Because MCP cannot distinguish "the user asked" from "the game text said", permission decisions must never depend on agent-supplied justification. Read-only mode and the dangerous switch are the real control.

### 4.3 Credential leakage

Tokens can leak through screenshots, shared config files, clipboard history, or logs. Mitigations: generate tokens locally, mask them in logs, never accept them in URLs, and make rotation easy.

### 4.4 Destructive or irreversible changes

Invocation, creation, destruction, and static writes can corrupt a save or crash the process. Mitigations: two-level permissions, dangerous authorization scoped to a single session, and documentation that tells agents to verify and not retry.

### 4.5 Denial of service against the game

Large enumerations, deep serialization, or high request volume can stall the main thread. Mitigations: depth, item, member, and batch caps; a per-frame execution budget; bounded queues; and request timeouts.

## 5. Default configuration

| Setting | Default | Rationale |
|---|---|---|
| Enabled | `false` | No listening socket unless the user opts in. |
| Transport | SSE | Broadest client compatibility. |
| Bind address | `127.0.0.1` | Loopback only; other addresses are rejected. |
| Port | `17891` | Unprivileged, unlikely to collide. |
| RPC path | `/mcp` | Conventional MCP endpoint. |
| Health path | `/health` | Separate from RPC; token optional. |
| Token | generated at startup if empty | Never ship a default credential. |
| Read-only | `true` | Queries work; mutations blocked. |
| Allow dangerous operations | `false` (reset each start) | Authorization does not persist. |
| Request logging | `false` | Avoid persisting potentially sensitive data. |
| Request timeout | 30000 ms | Bounds waiting on the main thread. |
| Max request body | 1 MiB | Prevents memory abuse. |
| Max pending requests | 128 | Bounds queue growth. |
| Max requests per frame | 16 | Protects frame time. |

## 6. Operational checklist

Before enabling write access:

- [ ] Confirm the game and save are recoverable and backed up.
- [ ] Confirm the bind address is `127.0.0.1`.
- [ ] Confirm a high-entropy token is set and not shared publicly.
- [ ] Start with read-only mode on and dangerous operations off.
- [ ] Verify `tools/list` and read-only tools work before enabling writes.
- [ ] Enable writes only for a specific, reviewed task.
- [ ] Re-read state after each mutation to verify the result.
- [ ] Turn dangerous operations back off when finished.
- [ ] Rotate the token if it may have been exposed.

## 7. Known limitations

- The HTTP parser requires `Content-Length` and rejects chunked request bodies with `501`. This is a deliberate scope limit, not a vulnerability, but it means some clients cannot connect.
- Read-only mode blocks mutation tools, but property getters and `ToString()` still execute code during reads. Treat read-only as "no writes", not as "no side effects".
- IL2CPP code that was stripped is unreachable, and a managed wrapper can outlive its native object. Validate liveness.
- The server cannot defend against a user who deliberately enables dangerous operations and asks an agent to destroy things.
