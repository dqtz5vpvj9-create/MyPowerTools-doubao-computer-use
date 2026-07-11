param(
    [int]$ToolPort = 38102,
    [int]$McpPort = 38080,
    [int]$PlannerPort = 38189,
    [string]$EnvFile = "$env:USERPROFILE\.codex\secrets\doubao-computer-use.env",
    [switch]$SkipPlanner
)

$ErrorActionPreference = "Stop"

function Import-EnvFile {
    param([string]$Path)
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) { return }
    foreach ($line in Get-Content -LiteralPath $Path) {
        $trimmed = $line.Trim()
        if (-not $trimmed -or $trimmed.StartsWith("#")) { continue }
        $index = $trimmed.IndexOf("=")
        if ($index -lt 1) { continue }
        $name = $trimmed.Substring(0, $index).Trim()
        $value = $trimmed.Substring($index + 1).Trim()
        if (($value.StartsWith('"') -and $value.EndsWith('"')) -or ($value.StartsWith("'") -and $value.EndsWith("'"))) {
            $value = $value.Substring(1, $value.Length - 2)
        }
        [Environment]::SetEnvironmentVariable($name, $value, "Process")
    }
}

Import-EnvFile -Path $EnvFile

$Root = $PSScriptRoot
$LogsDir = Join-Path $Root "logs"
New-Item -ItemType Directory -Force -Path $LogsDir | Out-Null

$ToolDir = Join-Path $Root "tool_server"
$McpDir = Join-Path $Root "mcp_server"
$PlannerDir = Join-Path $Root "planner"
$PlannerAppDir = Join-Path $PlannerDir "src\planner"

if (-not $SkipPlanner -and -not $env:ARK_API_KEY) {
    throw "ARK_API_KEY is required when starting planner."
}

$env:FASTMCP_PORT = [string]$McpPort
$env:CONFIG_FILES = Join-Path $ToolDir "config.toml"

$tool = Start-Process `
    -FilePath (Join-Path $ToolDir ".venv\Scripts\python.exe") `
    -ArgumentList @("main.py") `
    -WorkingDirectory $ToolDir `
    -WindowStyle Hidden `
    -RedirectStandardOutput (Join-Path $LogsDir "tool_server.out.log") `
    -RedirectStandardError (Join-Path $LogsDir "tool_server.err.log") `
    -PassThru

Start-Sleep -Seconds 3

$mcp = Start-Process `
    -FilePath (Join-Path $McpDir ".venv\Scripts\mcp-server.exe") `
    -ArgumentList @("--transport", "sse") `
    -WorkingDirectory $McpDir `
    -WindowStyle Hidden `
    -RedirectStandardOutput (Join-Path $LogsDir "mcp_server.out.log") `
    -RedirectStandardError (Join-Path $LogsDir "mcp_server.err.log") `
    -PassThru

$planner = $null
if (-not $SkipPlanner) {
    Start-Sleep -Seconds 3
    $env:CONFIG_FILES = Join-Path $PlannerDir "config.toml"
    $planner = Start-Process `
        -FilePath (Join-Path $PlannerDir ".venv\Scripts\python.exe") `
        -ArgumentList @("-m", "uvicorn", "app:app", "--host", "0.0.0.0", "--port", [string]$PlannerPort) `
        -WorkingDirectory $PlannerAppDir `
        -WindowStyle Hidden `
        -RedirectStandardOutput (Join-Path $LogsDir "planner.out.log") `
        -RedirectStandardError (Join-Path $LogsDir "planner.err.log") `
        -PassThru
}

Start-Sleep -Seconds 2
$toolServerPid = (Get-NetTCPConnection -LocalPort $ToolPort -ErrorAction SilentlyContinue |
    Where-Object State -eq "Listen" |
    Select-Object -First 1 -ExpandProperty OwningProcess)
$mcpServerPid = (Get-NetTCPConnection -LocalPort $McpPort -ErrorAction SilentlyContinue |
    Where-Object State -eq "Listen" |
    Select-Object -First 1 -ExpandProperty OwningProcess)
$plannerServerPid = if ($SkipPlanner) {
    $null
} else {
    Get-NetTCPConnection -LocalPort $PlannerPort -ErrorAction SilentlyContinue |
        Where-Object State -eq "Listen" |
        Select-Object -First 1 -ExpandProperty OwningProcess
}

$state = [ordered]@{
    started_at = (Get-Date).ToString("s")
    tool_port = $ToolPort
    mcp_port = $McpPort
    planner_port = if ($SkipPlanner) { $null } else { $PlannerPort }
    tool_wrapper_pid = $tool.Id
    tool_server_pid = $toolServerPid
    mcp_wrapper_pid = $mcp.Id
    mcp_server_pid = $mcpServerPid
    planner_wrapper_pid = if ($planner) { $planner.Id } else { $null }
    planner_pid = $plannerServerPid
}

$state | ConvertTo-Json | Set-Content -Path (Join-Path $LogsDir "local-computer-use.pids.json") -Encoding UTF8
$state
