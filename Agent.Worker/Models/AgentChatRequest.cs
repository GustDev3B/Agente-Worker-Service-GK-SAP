namespace Agent.Worker.Models;

public class AgentChatRequest
{
    public List<ChatMessage>    Messages { get; set; } = new();
    public List<ToolDefinition>? Tools   { get; set; }
}
