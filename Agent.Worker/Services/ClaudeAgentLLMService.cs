using Agent.Worker.Interfaces;
using Agent.Worker.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Agent.Worker.Services;

/// <summary>
/// Implementación del LLM usando la API de Anthropic (Claude).
/// Modelo por defecto: claude-haiku-4-5-20251001 (rápido y económico).
/// </summary>
public class ClaudeAgentLLMService : IAgentLLMService
{
    private readonly HttpClient                    _http;
    private readonly string                        _model;
    private readonly ILogger<ClaudeAgentLLMService> _logger;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy        = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition      = JsonIgnoreCondition.WhenWritingNull,
        PropertyNameCaseInsensitive = true
    };

    public ClaudeAgentLLMService(
        HttpClient                     http,
        IConfiguration                 config,
        ILogger<ClaudeAgentLLMService> logger)
    {
        _http   = http;
        _model  = config["LLM:ClaudeModel"] ?? "claude-haiku-4-5-20251001";
        _logger = logger;

        var apiKey = config["LLM:ClaudeApiKey"]
            ?? throw new InvalidOperationException("LLM:ClaudeApiKey no configurado.");
        _http.DefaultRequestHeaders.Add("x-api-key", apiKey);
        _http.DefaultRequestHeaders.Add("anthropic-version", "2023-06-01");
    }

    public async Task<AgentChatResponse> ChatAsync(AgentChatRequest request, CancellationToken ct = default)
    {
        var systemPrompt = request.Messages
            .FirstOrDefault(m => m.Role == "system")?.Content ?? "";

        var body = new ClaudeRequest
        {
            Model     = _model,
            MaxTokens = 4096,
            System    = string.IsNullOrEmpty(systemPrompt) ? null : systemPrompt,
            Messages  = BuildClaudeMessages(request.Messages),
            Tools     = request.Tools?.Select(MapTool).ToList()
        };

        _logger.LogDebug("[Claude] Enviando {N} mensajes al modelo {Model}", body.Messages.Count, _model);

        var response = await _http.PostAsJsonAsync("/v1/messages", body, JsonOpts, ct);

        if (!response.IsSuccessStatusCode)
        {
            var error = await response.Content.ReadAsStringAsync(ct);
            throw new InvalidOperationException($"Claude API error {(int)response.StatusCode}: {error}");
        }

        var result = await response.Content.ReadFromJsonAsync<ClaudeResponse>(JsonOpts, ct)
                     ?? throw new InvalidOperationException("Respuesta vacía de Claude.");

        return MapResponse(result);
    }

    private static List<ClaudeMessage> BuildClaudeMessages(List<ChatMessage> messages)
    {
        var result = new List<ClaudeMessage>();
        var i      = 0;

        while (i < messages.Count)
        {
            var msg = messages[i];

            if (msg.Role == "system") { i++; continue; }

            if (msg.Role == "assistant")
            {
                var content = new List<object>();
                if (!string.IsNullOrEmpty(msg.Content))
                    content.Add(new { type = "text", text = msg.Content });
                if (msg.ToolCalls != null)
                    foreach (var tc in msg.ToolCalls)
                        content.Add(new { type = "tool_use", id = tc.Id, name = tc.Name, input = tc.Arguments });

                result.Add(new ClaudeMessage { Role = "assistant", Content = content });
                i++;
                continue;
            }

            if (msg.Role == "tool")
            {
                var toolResults = new List<object>();
                while (i < messages.Count && messages[i].Role == "tool")
                {
                    var tr = messages[i];
                    toolResults.Add(new { type = "tool_result", tool_use_id = tr.ToolCallId ?? "", content = tr.Content ?? "" });
                    i++;
                }
                result.Add(new ClaudeMessage { Role = "user", Content = toolResults });
                continue;
            }

            result.Add(new ClaudeMessage { Role = msg.Role, Content = (object)(msg.Content ?? "") });
            i++;
        }

        return result;
    }

    private static ClaudeTool MapTool(ToolDefinition td) => new()
    {
        Name        = td.Name,
        Description = td.Description,
        InputSchema = new ClaudeToolSchema
        {
            Type       = "object",
            Properties = td.Parameters.Properties.ToDictionary(
                kv => kv.Key,
                kv => (object)new { type = kv.Value.Type, description = kv.Value.Description }),
            Required   = td.Parameters.Required
        }
    };

    private static AgentChatResponse MapResponse(ClaudeResponse response)
    {
        string?         text      = null;
        List<ToolCall>? toolCalls = null;

        foreach (var block in response.Content)
        {
            if (block.Type == "text") text = block.Text;
            if (block.Type == "tool_use")
            {
                toolCalls ??= [];
                toolCalls.Add(new ToolCall
                {
                    Id        = block.Id ?? Guid.NewGuid().ToString("N")[..8],
                    Name      = block.Name ?? "",
                    Arguments = block.Input
                });
            }
        }

        return new AgentChatResponse { Message = new ChatMessage("assistant", text, toolCalls) };
    }

    private class ClaudeRequest
    {
        public string              Model     { get; set; } = "";
        public int                 MaxTokens { get; set; }
        public string?             System    { get; set; }
        public List<ClaudeMessage> Messages  { get; set; } = [];
        public List<ClaudeTool>?   Tools     { get; set; }
    }

    private class ClaudeMessage
    {
        public string Role    { get; set; } = "";
        public object Content { get; set; } = "";
    }

    private class ClaudeTool
    {
        public string           Name        { get; set; } = "";
        public string           Description { get; set; } = "";
        public ClaudeToolSchema InputSchema { get; set; } = new();
    }

    private class ClaudeToolSchema
    {
        public string                     Type       { get; set; } = "object";
        public Dictionary<string, object> Properties { get; set; } = new();
        public List<string>               Required   { get; set; } = [];
    }

    private class ClaudeResponse
    {
        public List<ClaudeContentBlock> Content    { get; set; } = [];
        public string?                  StopReason { get; set; }
    }

    private class ClaudeContentBlock
    {
        public string?     Type  { get; set; }
        public string?     Text  { get; set; }
        public string?     Id    { get; set; }
        public string?     Name  { get; set; }
        public JsonElement Input { get; set; }
    }
}
