namespace Agent.Worker.Models;

public class AgentChatResponse
{
    public ChatMessage Message     { get; set; } = new();
    public bool        HasToolCalls => Message.ToolCalls?.Count > 0;
    public string?     Content      => Message.Content;
}
