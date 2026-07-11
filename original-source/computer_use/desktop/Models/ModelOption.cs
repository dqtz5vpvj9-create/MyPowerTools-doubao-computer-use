namespace DoubaoComputerUse.Desktop.Models;

public sealed class ModelOption
{
    public string Name { get; init; } = "";
    public string DisplayName { get; init; } = "";
    public string DisplayLabel => string.IsNullOrWhiteSpace(DisplayName) ? Name : DisplayName;
}

