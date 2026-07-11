using System.IO;
using System.Text.Json;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace DoubaoComputerUse.Desktop.Models;

public sealed class TraceEvent
{
    private static readonly JsonSerializerOptions PrettyJson = new() { WriteIndented = true };

    public int Index { get; init; }
    public DateTime ReceivedAt { get; init; } = DateTime.Now;
    public string TimeText => ReceivedAt.ToString("HH:mm:ss");
    public string Title { get; init; } = "";
    public string Detail { get; init; } = "";
    public string RawJson { get; init; } = "";
    public bool IsError { get; init; }
    public ImageSource? Screenshot { get; init; }

    public static TraceEvent FromPayload(string payload, int index)
    {
        try
        {
            using var document = JsonDocument.Parse(payload);
            var root = document.RootElement;
            var title = PickString(root, "action")
                ?? PickString(root, "event")
                ?? PickString(root, "status")
                ?? "message";
            var detail = string.Join(
                Environment.NewLine,
                new[]
                {
                    PickString(root, "summary"),
                    PickString(root, "message"),
                    PickString(root, "error"),
                    PickString(root, "model_response"),
                    PickString(root, "parsed_action")
                }.Where(value => !string.IsNullOrWhiteSpace(value)));
            var rawJson = JsonSerializer.Serialize(root, PrettyJson);
            var screenshot = TryCreateImage(PickString(root, "screenshot")
                ?? PickString(root, "image")
                ?? PickString(root, "image_base64"));

            return new TraceEvent
            {
                Index = index,
                Title = title,
                Detail = detail,
                RawJson = rawJson,
                Screenshot = screenshot,
                IsError = root.TryGetProperty("error", out _)
                    || string.Equals(PickString(root, "status"), "error", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(PickString(root, "status"), "failed", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(PickString(root, "status"), "timeout", StringComparison.OrdinalIgnoreCase)
            };
        }
        catch
        {
            return new TraceEvent
            {
                Index = index,
                Title = "raw",
                Detail = payload,
                RawJson = payload,
                IsError = false
            };
        }
    }

    public static TraceEvent FromMessage(string title, string detail, int index, bool isError = false)
    {
        var raw = JsonSerializer.Serialize(new { event_name = title, message = detail }, PrettyJson);
        return new TraceEvent
        {
            Index = index,
            Title = title,
            Detail = detail,
            RawJson = raw,
            IsError = isError
        };
    }

    private static string? PickString(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var value))
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Null => null,
            JsonValueKind.Undefined => null,
            _ => value.GetRawText()
        };
    }

    private static ImageSource? TryCreateImage(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var base64 = value;
        var commaIndex = base64.IndexOf(',');
        if (base64.StartsWith("data:image/", StringComparison.OrdinalIgnoreCase) && commaIndex >= 0)
        {
            base64 = base64[(commaIndex + 1)..];
        }

        try
        {
            var bytes = Convert.FromBase64String(base64);
            using var stream = new MemoryStream(bytes);
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.StreamSource = stream;
            bitmap.EndInit();
            bitmap.Freeze();
            return bitmap;
        }
        catch
        {
            return null;
        }
    }
}

