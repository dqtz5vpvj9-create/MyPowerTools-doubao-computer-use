using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using DoubaoComputerUse.Desktop.Models;

namespace DoubaoComputerUse.Desktop.Services;

public sealed class LocalRuntimeService : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false
    };

    private readonly HttpClient _httpClient = new()
    {
        Timeout = Timeout.InfiniteTimeSpan
    };

    public LocalRuntimeService()
    {
        ComputerUseRoot = FindComputerUseRoot();
        Endpoints = new EndpointSettings();
        SecretFile = ResolveSecretFile();
    }

    public DirectoryInfo ComputerUseRoot { get; }
    public EndpointSettings Endpoints { get; }
    public FileInfo SecretFile { get; }
    public string StartScriptPath => Path.Combine(ComputerUseRoot.FullName, "start-local-computer-use.ps1");
    public string StopScriptPath => Path.Combine(ComputerUseRoot.FullName, "stop-local-computer-use.ps1");
    public string LogDirectory => Path.Combine(ComputerUseRoot.FullName, "logs");

    public async Task<ServiceSnapshot> GetStatusAsync(CancellationToken cancellationToken)
    {
        var toolTask = ProbeJsonAsync($"{Endpoints.ToolServerUrl}/config", cancellationToken);
        var plannerTask = ProbeJsonAsync($"{Endpoints.PlannerUrl}/health", cancellationToken);
        var modelsTask = ProbeJsonAsync($"{Endpoints.PlannerUrl}/models", cancellationToken);
        var overlayTask = CallToolActionAsync("OverlayStatus", null, cancellationToken);
        var mcpTask = ProbeTcpAsync("127.0.0.1", 38080, cancellationToken);

        await Task.WhenAll(toolTask, plannerTask, modelsTask, overlayTask, mcpTask);
        var toolServer = await toolTask;
        var planner = await plannerTask;
        var models = await modelsTask;
        var mcpServer = await mcpTask;
        var overlay = await overlayTask;

        return new ServiceSnapshot
        {
            CheckedAt = DateTimeOffset.Now,
            Endpoints = Endpoints,
            ToolServer = toolServer,
            Planner = planner,
            Models = models,
            McpServer = mcpServer,
            Overlay = overlay,
            OverlayJson = overlay.Data,
            ModelOptions = ParseModels(models.Data)
        };
    }

    public async Task<RuntimeStartResult> EnsureStartedAsync(IProgress<string>? progress, CancellationToken cancellationToken)
    {
        progress?.Report("检查本地服务");
        var status = await GetStatusAsync(cancellationToken);
        if (status.IsReady)
        {
            return new RuntimeStartResult
            {
                Success = true,
                Message = "本地服务已就绪"
            };
        }

        if (!File.Exists(StartScriptPath))
        {
            return new RuntimeStartResult
            {
                Success = false,
                Message = "缺少启动脚本，请重新安装豆包本地运行时",
                Diagnostic = $"Start script missing: {StartScriptPath}"
            };
        }

        progress?.Report("启动本地服务");
        var startResult = await RunPowerShellAsync(
            StartScriptPath,
            ["-EnvFile", SecretFile.FullName],
            TimeSpan.FromSeconds(70),
            cancellationToken);

        if (!startResult.Success)
        {
            return startResult;
        }

        var deadline = DateTimeOffset.Now.AddSeconds(45);
        while (DateTimeOffset.Now < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Delay(1500, cancellationToken);
            status = await GetStatusAsync(cancellationToken);
            if (status.IsReady)
            {
                return new RuntimeStartResult
                {
                    Success = true,
                    Message = "本地服务已启动",
                    Diagnostic = startResult.Diagnostic
                };
            }
        }

        return new RuntimeStartResult
        {
            Success = false,
            Message = "服务启动超时，请打开诊断查看日志",
            Diagnostic = startResult.Diagnostic
        };
    }

    public async Task<ProbeResult> CallOverlayAsync(string command, OverlayRequest request, CancellationToken cancellationToken)
    {
        var action = command switch
        {
            "show" => "ShowOverlay",
            "hide" => "HideOverlay",
            "self-test" => "OverlaySelfTest",
            _ => "OverlayStatus"
        };

        var parameters = action switch
        {
            "ShowOverlay" => new Dictionary<string, string>
            {
                ["PositionX"] = request.X.ToString(),
                ["PositionY"] = request.Y.ToString(),
                ["Label"] = "Doubao",
                ["DurationMs"] = request.DurationMs.ToString(),
                ["Radius"] = request.Radius.ToString()
            },
            _ => null
        };

        return await CallToolActionAsync(action, parameters, cancellationToken);
    }

    public async Task RunTaskStreamAsync(
        string prompt,
        string modelName,
        string systemPrompt,
        Action<TraceEvent> onEvent,
        CancellationToken cancellationToken)
    {
        var payload = JsonSerializer.Serialize(
            new
            {
                user_prompt = prompt,
                model_name = modelName,
                system_prompt = systemPrompt
            },
            JsonOptions);
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{Endpoints.PlannerUrl}/run/task")
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json")
        };

        using var response = await _httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var error = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(error)
                ? $"Planner HTTP {(int)response.StatusCode}"
                : error);
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        var lines = new List<string>();
        var index = 0;

        while (!reader.EndOfStream)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var line = await reader.ReadLineAsync(cancellationToken);
            if (line is null)
            {
                break;
            }

            if (line.Length == 0)
            {
                FlushFrame(lines, onEvent, ref index);
                continue;
            }

            lines.Add(line);
        }

        FlushFrame(lines, onEvent, ref index);
    }

    public void Dispose()
    {
        _httpClient.Dispose();
    }

    private async Task<ProbeResult> ProbeJsonAsync(string url, CancellationToken cancellationToken)
    {
        var watch = Stopwatch.StartNew();
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.TryAddWithoutValidation("Accept", "application/json");
            using var response = await _httpClient.SendAsync(request, timeout.Token);
            var data = await response.Content.ReadAsStringAsync(timeout.Token);
            return new ProbeResult
            {
                Ok = response.IsSuccessStatusCode,
                Status = (int)response.StatusCode,
                Ms = watch.ElapsedMilliseconds,
                Data = data
            };
        }
        catch (Exception ex)
        {
            return new ProbeResult
            {
                Ok = false,
                Ms = watch.ElapsedMilliseconds,
                Error = ex.Message
            };
        }
    }

    private async Task<ProbeResult> ProbeTcpAsync(string host, int port, CancellationToken cancellationToken)
    {
        var watch = Stopwatch.StartNew();
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(3));
            using var client = new TcpClient();
            await client.ConnectAsync(host, port, timeout.Token);
            return new ProbeResult
            {
                Ok = true,
                Ms = watch.ElapsedMilliseconds,
                Data = $"tcp://{host}:{port}"
            };
        }
        catch (Exception ex)
        {
            return new ProbeResult
            {
                Ok = false,
                Ms = watch.ElapsedMilliseconds,
                Error = ex.Message
            };
        }
    }

    private async Task<ProbeResult> CallToolActionAsync(
        string action,
        IReadOnlyDictionary<string, string>? parameters,
        CancellationToken cancellationToken)
    {
        var query = new Dictionary<string, string>
        {
            ["Action"] = action,
            ["Version"] = "2020-04-01"
        };
        if (parameters is not null)
        {
            foreach (var item in parameters)
            {
                query[item.Key] = item.Value;
            }
        }

        var url = $"{Endpoints.ToolServerUrl}/?{BuildQueryString(query)}";
        return await ProbeJsonAsync(url, cancellationToken);
    }

    private static void FlushFrame(List<string> lines, Action<TraceEvent> onEvent, ref int index)
    {
        if (lines.Count == 0)
        {
            return;
        }

        var payload = string.Join(
            Environment.NewLine,
            lines
                .Where(line => line.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
                .Select(line => line[5..].TrimStart()));
        lines.Clear();

        if (string.IsNullOrWhiteSpace(payload) || payload.Trim() == "[DONE]")
        {
            return;
        }

        index++;
        onEvent(TraceEvent.FromPayload(payload, index));
    }

    private static IReadOnlyList<ModelOption> ParseModels(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            if (!document.RootElement.TryGetProperty("models", out var models) ||
                models.ValueKind != JsonValueKind.Array)
            {
                return [];
            }

            return models
                .EnumerateArray()
                .Select(model => new ModelOption
                {
                    Name = model.TryGetProperty("name", out var name) ? name.GetString() ?? "" : "",
                    DisplayName = model.TryGetProperty("display_name", out var displayName)
                        ? displayName.GetString() ?? ""
                        : ""
                })
                .Where(model => !string.IsNullOrWhiteSpace(model.Name))
                .ToList();
        }
        catch
        {
            return [];
        }
    }

    private static string BuildQueryString(IReadOnlyDictionary<string, string> query)
    {
        return string.Join("&", query.Select(item =>
            $"{Uri.EscapeDataString(item.Key)}={Uri.EscapeDataString(item.Value)}"));
    }

    private static DirectoryInfo FindComputerUseRoot()
    {
        var explicitRoot = Environment.GetEnvironmentVariable("DOUBAO_COMPUTER_USE_ROOT");
        if (IsComputerUseRoot(explicitRoot))
        {
            return new DirectoryInfo(Path.GetFullPath(explicitRoot!));
        }

        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (IsComputerUseRoot(current.FullName))
            {
                return current;
            }

            var nestedComputerUse = Path.Combine(current.FullName, "computer_use");
            if (IsComputerUseRoot(nestedComputerUse))
            {
                return new DirectoryInfo(Path.GetFullPath(nestedComputerUse));
            }

            current = current.Parent;
        }

        var codexInstalledRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".codex",
            "computer-use",
            "doubao-computer-use-local");
        if (IsComputerUseRoot(codexInstalledRoot))
        {
            return new DirectoryInfo(codexInstalledRoot);
        }

        return new DirectoryInfo(Path.GetFullPath(AppContext.BaseDirectory));
    }

    private static bool IsComputerUseRoot(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        return File.Exists(Path.Combine(path, "start-local-computer-use.ps1"));
    }

    private static FileInfo ResolveSecretFile()
    {
        var codexHome = Environment.GetEnvironmentVariable("CODEX_HOME");
        if (string.IsNullOrWhiteSpace(codexHome))
        {
            codexHome = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".codex");
        }

        return new FileInfo(Path.Combine(codexHome, "secrets", "doubao-computer-use.env"));
    }

    private static async Task<RuntimeStartResult> RunPowerShellAsync(
        string scriptPath,
        IReadOnlyList<string> arguments,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var powerShell = FindPowerShellExecutable();
        var psi = new ProcessStartInfo
        {
            FileName = powerShell,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = Path.GetDirectoryName(scriptPath) ?? Environment.CurrentDirectory
        };

        psi.ArgumentList.Add("-NoProfile");
        if (string.Equals(Path.GetFileName(powerShell), "powershell.exe", StringComparison.OrdinalIgnoreCase))
        {
            psi.ArgumentList.Add("-ExecutionPolicy");
            psi.ArgumentList.Add("Bypass");
        }

        psi.ArgumentList.Add("-File");
        psi.ArgumentList.Add(scriptPath);
        foreach (var argument in arguments)
        {
            psi.ArgumentList.Add(argument);
        }

        using var process = Process.Start(psi);
        if (process is null)
        {
            return new RuntimeStartResult
            {
                Success = false,
                Message = "无法启动本地服务脚本",
                Diagnostic = $"Failed to start PowerShell executable: {powerShell}"
            };
        }

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeout);
        try
        {
            await process.WaitForExitAsync(timeoutCts.Token);
        }
        catch (OperationCanceledException)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch
            {
                // Best effort cleanup for a failed launch.
            }

            return new RuntimeStartResult
            {
                Success = false,
                Message = "启动脚本执行超时",
                Diagnostic = $"Timed out after {timeout.TotalSeconds:0}s: {scriptPath}"
            };
        }

        var stdout = await process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderr = await process.StandardError.ReadToEndAsync(cancellationToken);
        var diagnostic = string.Join(
            Environment.NewLine,
            new[]
            {
                $"Executable: {powerShell}",
                $"Script: {scriptPath}",
                string.IsNullOrWhiteSpace(stdout) ? "" : $"stdout:{Environment.NewLine}{stdout.Trim()}",
                string.IsNullOrWhiteSpace(stderr) ? "" : $"stderr:{Environment.NewLine}{stderr.Trim()}"
            }.Where(value => !string.IsNullOrWhiteSpace(value)));

        if (process.ExitCode == 0)
        {
            return new RuntimeStartResult
            {
                Success = true,
                Message = "启动脚本已完成",
                Diagnostic = diagnostic
            };
        }

        return new RuntimeStartResult
        {
            Success = false,
            Message = "启动脚本执行失败，请打开诊断查看日志",
            Diagnostic = diagnostic
        };
    }

    private static string FindPowerShellExecutable()
    {
        var pathValue = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var directory in pathValue.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = Path.Combine(directory, "pwsh.exe");
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return "powershell.exe";
    }
}
