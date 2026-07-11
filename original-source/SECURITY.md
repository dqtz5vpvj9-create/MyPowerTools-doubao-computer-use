# Security Notes

This repository must stay free of local secrets and machine-specific values.

Before publishing changes, run:

```powershell
rg -n --hidden --glob '!**/.git/**' "C:\\Users|ARK_API_KEY|api_key|secret|token|password" .
```

Expected findings may include placeholder field names and documentation. Real keys, local user paths, virtual environments, logs, and queue outputs should be removed before pushing.
