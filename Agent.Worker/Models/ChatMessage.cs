namespace Agent.Worker.Models;

public class ChatMessage
{
    public string       Role      { get; set; } = string.Empty;
    public string?      Content   { get; set; }
    public List<ToolCall>? ToolCalls { get; set; }

    public ChatMessage() { }

    public ChatMessage(string role, string? content = null, List<ToolCall>? toolCalls = null)
    {
        Role      = role;
        Content   = content;
        ToolCalls = toolCalls;
    }

    public static ChatMessage System(string content)    => new("system",    content);
    public static ChatMessage User(string content)      => new("user",      content);
    public static ChatMessage Assistant(string? content, List<ToolCall>? toolCalls = null)
                                                        => new("assistant", content, toolCalls);
    public static ChatMessage Tool(string content, string? toolCallId = null)
                                                        => new("tool", content) { ToolCallId = toolCallId };

    public string? ToolCallId { get; set; }
}
