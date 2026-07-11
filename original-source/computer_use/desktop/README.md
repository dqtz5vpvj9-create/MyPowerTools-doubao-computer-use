# Doubao Computer Use Desktop

WPF desktop frontend for the local Doubao computer-use runtime.

## Run From Source

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File ..\start-desktop.ps1
```

The app checks `tool_server`, `mcp_server`, and `planner` on launch. When auto-start is enabled, it runs `..\start-local-computer-use.ps1` and then waits for the services to become ready.

For a UI-only smoke test that avoids starting local services, run the built executable with `--no-auto-start`.

## Publish

From the repository root:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\publish-desktop.ps1
```

The default publish target is `outputs\desktop-win-x64`.
