namespace Agent.Worker.Models;

public class ToolDefinition
{
    public string         Name        { get; set; } = string.Empty;
    public string         Description { get; set; } = string.Empty;
    public ToolParameters Parameters  { get; set; } = new();
}

public class ToolParameters
{
    public string                            Type       { get; set; } = "object";
    public Dictionary<string, ToolProperty>  Properties { get; set; } = new();
    public List<string>                      Required   { get; set; } = new();
}

public class ToolProperty
{
    public string Type        { get; set; } = "string";
    public string Description { get; set; } = string.Empty;
}
