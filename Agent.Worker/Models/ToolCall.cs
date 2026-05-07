using System.Text.Json;

namespace Agent.Worker.Models;

public class ToolCall
{
    public string      Id        { get; set; } = Guid.NewGuid().ToString("N")[..8];
    public string      Name      { get; set; } = string.Empty;
    public JsonElement Arguments { get; set; }
}
