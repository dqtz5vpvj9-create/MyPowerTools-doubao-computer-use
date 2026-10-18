# Archived integration snapshots

The 9 files listed below (5,185 physical source lines) were retired from the current tree because they are superseded integration snapshots, not current build inputs.

The active UI is current-integration/src/DoubaoAgent.Surface. DoubaoAgent.Controller.Service explicitly links the secure runtime controller from that Surface directory, not from the retired Shell snapshot.

The superproject solution, project includes, tool build scripts, package manifests, and runtime source staging do not consume these paths. Existing Surface XAML, reflection-loaded factory types, shared runtime ownership, original-source trees, and linked tests remain unchanged. No runtime behavior change or build-time speedup is claimed from deleting files that were not compiled.

## Restore from Git history

Recovery commit in this tool repository: `e60e168ff661417ab82205306e1e8324f2d43eb8`. Keep the repository history containing this commit when publishing or mirroring it. A shallow clone or a source ZIP alone may not contain these historical blobs.

Run from this tool repository after saving any current changes:

```sh
git cat-file -e e60e168ff661417ab82205306e1e8324f2d43eb8^{commit}
git restore --source=e60e168ff661417ab82205306e1e8324f2d43eb8 -- current-integration/src/MyPowerTools.Shell.Avalonia
```

If the commit is absent, obtain it from an existing full clone or the repository remote containing this history before restoring. Restoring adds historical source files to the working tree; it does not wire them back into the current build.

The original source-map capture fields remain historical provenance. Its archivedIntegrationSnapshots entries describe the later retirement and point to this document, which stays inside current-integration so source-bundle materialization preserves it.

## Exact file recovery inventory

| Path | Git blob SHA-1 | Physical lines |
| --- | --- | ---: |
| `current-integration/src/MyPowerTools.Shell.Avalonia/Services/DoubaoAgentToolService.Settings.cs` | `9495a2f9adc5ff091d5ac720f07fd4a13635bd21` | 518 |
| `current-integration/src/MyPowerTools.Shell.Avalonia/Services/DoubaoAgentToolService.cs` | `0b31fa6642a55dcb405be9118499a1d66291ea9f` | 1522 |
| `current-integration/src/MyPowerTools.Shell.Avalonia/Services/DoubaoSecureRuntimeController.cs` | `e7ae730d5539c7f21a80997b09830924da810ee4` | 1289 |
| `current-integration/src/MyPowerTools.Shell.Avalonia/ViewModels/DoubaoAgentViewModel.Operations.cs` | `2c3209b0b5c6154e3054d85c0b4d46c701d66bb0` | 378 |
| `current-integration/src/MyPowerTools.Shell.Avalonia/ViewModels/DoubaoAgentViewModel.Properties.cs` | `fbef1fa0bc01dc1da9c0e908fa4c89e9ec6213f6` | 292 |
| `current-integration/src/MyPowerTools.Shell.Avalonia/ViewModels/DoubaoAgentViewModel.State.cs` | `4e55856357a9fb8cb95943e47ec55205aa29f7e3` | 235 |
| `current-integration/src/MyPowerTools.Shell.Avalonia/ViewModels/DoubaoAgentViewModels.cs` | `b3486114a7cae1e0a5504ee22c65a77b6edc7558` | 232 |
| `current-integration/src/MyPowerTools.Shell.Avalonia/Views/DoubaoAgentView.axaml` | `9d0dc01aec8f52f58d5c40a72eb1ad8264d3dc16` | 637 |
| `current-integration/src/MyPowerTools.Shell.Avalonia/Views/DoubaoAgentView.axaml.cs` | `afa3f2ee35006faf2746d51d1ab745000c087227` | 82 |
