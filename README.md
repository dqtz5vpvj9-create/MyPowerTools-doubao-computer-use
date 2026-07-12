<!-- mypowertools-materialized-source -->
# doubao-computer-use

This repository contains the `doubao-computer-use` tool source and its current
MyPowerTools adapter. Its local submodule origin is:

```text
file:///C:/Users/lixinrui/repo/MyPowerTools.ToolRepos/doubao-computer-use
```

## Repository layout

- `original-source/` contains the captured Doubao Computer Use product source.
- `current-integration/` contains the MyPowerTools adapter, package template,
  product UI integration source, services, and related test snapshots.
- `source-map.json` records the captured source commit, dirty state, and file
  mapping.
- `tool-release.json` declares the adapter project, suite project references,
  package template, and staged package output.
- `build.ps1` builds the adapter in Release configuration and stages the
  package at `artifacts/package`.

The tool ID is `doubao-computer-use`; its current package and module ID is
`doubao-agent`.

## Build

Pass the MyPowerTools superproject explicitly when the repositories are in
arbitrary locations:

```powershell
pwsh ./build.ps1 -MyPowerToolsRepoRoot 'C:\path\to\MyPowerTools'
```

The parameter can be omitted for a checkout under
`MyPowerTools/tools/doubao-computer-use` or a standalone checkout beside a
directory named `MyPowerTools`:

```powershell
pwsh ./build.ps1
```

Automatic discovery also considers the `MYPOWERTOOLS_REPO_ROOT` environment
variable. A direct adapter build must pass the equivalent MSBuild property:

```powershell
dotnet build ./current-integration/src/DoubaoAgent.MyPowerTools/DoubaoAgent.MyPowerTools.csproj `
  --configuration Release `
  -p:MyPowerToolsRepoRoot='C:\path\to\MyPowerTools'
```

The package template remains under `current-integration/modules/doubao-agent`.
The build copies the completed template, including
`DoubaoAgent.MyPowerTools.dll`, to `artifacts/package`. Package integrity
metadata requires refresh before a signed release.

## Publishing the repository

After publishing this repository, update its URL from the MyPowerTools
superproject and commit `.gitmodules`:

```powershell
git config -f .gitmodules submodule.tools/doubao-computer-use.url <remote-url>
git submodule sync -- tools/doubao-computer-use
git add .gitmodules
```
