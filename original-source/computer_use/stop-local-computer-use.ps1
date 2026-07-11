$ErrorActionPreference = "Continue"

$Root = $PSScriptRoot
$LogsDir = Join-Path $Root "logs"
$PidFile = Join-Path $LogsDir "local-computer-use.pids.json"
$pids = @()

if (Test-Path $PidFile) {
    $state = Get-Content $PidFile -Raw | ConvertFrom-Json
    $pids += $state.tool_wrapper_pid
    $pids += $state.tool_server_pid
    $pids += $state.mcp_wrapper_pid
    $pids += $state.mcp_server_pid
    $pids += $state.planner_wrapper_pid
    $pids += $state.planner_pid
}

$rootFull = [System.IO.Path]::GetFullPath($Root)
$processes = Get-CimInstance Win32_Process |
    Where-Object {
        $commandLine = $_.CommandLine
        ($_.Name -match "^(python|mcp-server)\.exe$") -and
        $commandLine -and
        ($commandLine.IndexOf($rootFull, [System.StringComparison]::OrdinalIgnoreCase) -ge 0) -and
        (
            ($commandLine -like "*tool_server*main.py*") -or
            ($commandLine -like "*uvicorn*app:app*") -or
            (
                ($_.Name -eq "mcp-server.exe") -and
                ($commandLine -like "*--transport*sse*")
            )
        )
    }

$pids += $processes.ProcessId

foreach ($port in @(38102, 38080, 38189)) {
    $listeners = Get-NetTCPConnection -LocalPort $port -ErrorAction SilentlyContinue |
        Where-Object State -eq "Listen" |
        Select-Object -ExpandProperty OwningProcess
    $pids += $listeners
}

$pids = $pids | Where-Object { $_ } | Select-Object -Unique

foreach ($id in $pids) {
    Stop-Process -Id $id -Force -ErrorAction SilentlyContinue
}

[pscustomobject]@{
    stopped_pids = @($pids)
    stopped_at = (Get-Date).ToString("s")
}
