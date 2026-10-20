# SolidPilot — local machine notes

Upstream: <https://github.com/eyfel/mcp-server-solidworks> (AGPL-3.0).
Installed at `C:\Users\PC\.config\opencode\solidpilot` and registered globally in
`C:\Users\PC\.config\opencode\opencode.json` as the MCP server **`solidworks`**,
so every OpenCode project on this machine gets it (e.g. `E:\All Company`).

| Piece | Path |
|---|---|
| Repo / adapter | `adapters\claude\server.py` |
| Python venv | `.venv` (mcp 2.3.0, httpx, python-dotenv, ezdxf, pywin32) |
| Execution layer | `execution\solidworks\SolidworksExecution\bin\Debug\SolidworksExecution.exe` |
| Logs | `adapters\claude\adapter.log`, `execution\...\bin\Debug\execution.log` |

## Local changes (not upstream)

1. **Port 5050 instead of 5000.** The ERP web app already listens on `0.0.0.0:5000`,
   so the execution server could not bind. The base address was moved to 5050 in
   three places, which must stay in sync:

   - the shipped binary (string patched in place, same length; backup kept as
     `SolidworksExecution.exe.orig-port5000`)
   - `execution\solidworks\SolidworksExecution\Program.cs`
   - `adapters\claude\.env` → `EXECUTION_BASE_URL`
   - plus the same value in the global OpenCode config

   `setup\localize.py` re-applies all of this and is safe to re-run (idempotent).
   If you rebuild the C# layer from source, nothing needs re-patching — just keep 5050.

2. **The install is protected.** The folder is hidden/system and carries a deny-delete
   ACL, so it cannot be removed by accident (or by anyone other than an administrator
   taking ownership). To unlock it when you actually need to remove or replace it:

   ```powershell
   icacls "C:\Users\PC\.config\opencode\solidpilot" /remove:d *S-1-1-0 /T
   attrib -H -S /S /D "C:\Users\PC\.config\opencode\solidpilot"
   ```

## Verified on this machine

- Microsoft Windows, SolidWorks **2024 SP4** (`32.4.0`), Python 3.12.
- The prebuilt execution binary targets SolidWorks 2026 but works here: opening a part,
  sketching a 100×100 mm rectangle and extruding 50 mm produced volume **0.0005 m³**
  exactly.

## Gotchas

- **Tool arguments are flat**: `{operation_id, state_version, ...params}`. Do not nest
  `params` — the adapter validates with pydantic and will reject it.
  `state_version` must match the value returned by the previous call.
- **Keep exactly one `SLDWORKS` process.** A windowless `SLDWORKS.exe -Embedding`
  instance (check `Get-Process SLDWORKS | Select Id,MainWindowHandle` — handle `0`)
  never registers in the ROT and makes every call fail `MK_E_UNAVAILABLE`. Kill it and
  let the server launch a real instance.
- Health check: `GET http://localhost:5050/health` → expect `comAttached: true`.
- The adapter auto-starts the execution server on the first tool call; do not start it
  by hand.
