using Agent.Worker.Interfaces;
using Agent.Worker.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Agent.Worker.Services;

/// <summary>
/// Implementación del LLM usando la API de Groq (OpenAI-compatible).
/// Groq corre los modelos en hardware especializado (LPU) — respuestas en segundos.
/// Modelo por defecto: llama-3.1-8b-instant
/// </summary>
public class GroqAgentLLMService : IAgentLLMService
{
    private readonly HttpClient                   _http;
    private readonly string                       _model;
    private readonly ILogger<GroqAgentLLMService> _logger;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy        = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition      = JsonIgnoreCondition.WhenWritingNull,
        PropertyNameCaseInsensitive = true
    };

    public GroqAgentLLMService(
        HttpClient                    http,
        IConfiguration                config,
        ILogger<GroqAgentLLMService>  logger)
    {
        _http   = http;
        _model  = config["LLM:GroqModel"] ?? "llama-3.1-8b-instant";
        _logger = logger;

        var apiKey = config["LLM:GroqApiKey"] ?? throw new InvalidOperationException("LLM:GroqApiKey no configurado.");
        _http.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", apiKey);
    }

    public async Task<AgentChatResponse> ChatAsync(AgentChatRequest request, CancellationToken ct = default)
    {
        var body = new GroqRequest
        {
            Model    = _model,
            Messages = request.Messages.Select(MapMessage).ToList(),
            Tools    = request.Tools?.Select(MapTool).ToList()
        };

        _logger.LogDebug("[Groq] Enviando {N} mensajes al modelo {Model}", body.Messages.Count, _model);

        HttpResponseMessage response;
        for (var intento = 1; ; intento++)
        {
            response = await _http.PostAsJsonAsync("/openai/v1/chat/completions", body, JsonOpts, ct);

            if (response.StatusCode != System.Net.HttpStatusCode.TooManyRequests) break;

            if (intento >= 3) break; // máximo 3 reintentos

            _logger.LogWarning("[Groq] Rate limit alcanzado. Esperando 45 segundos antes de reintentar ({Intento}/3)...", intento);
            await Task.Delay(TimeSpan.FromSeconds(45), ct);
        }

        if (!response.IsSuccessStatusCode)
        {
            var error = await response.Content.ReadAsStringAsync(ct);
            throw new InvalidOperationException($"Groq API error {(int)response.StatusCode}: {error}");
        }

        var result = await response.Content.ReadFromJsonAsync<GroqResponse>(JsonOpts, ct)
                     ?? throw new InvalidOperationException("Respuesta vacía de Groq.");

        return MapResponse(result);
    }

    // ── Mapping ──────────────────────────────────────────────────────────────

    private static GroqMessage MapMessage(ChatMessage msg)
    {
        var groqMsg = new GroqMessage
        {
            Role       = msg.Role,
            Content    = msg.Content,
            ToolCallId = msg.ToolCallId
        };

        if (msg.ToolCalls != null)
            groqMsg.ToolCalls = msg.ToolCalls.Select(tc => new GroqToolCall
            {
                Id       = tc.Id,
                Type     = "function",
                Function = new GroqToolCallFunction
                {
                    Name      = tc.Name,
                    Arguments = tc.Arguments.GetRawText()
                }
            }).ToList();

        return groqMsg;
    }

    private static GroqTool MapTool(ToolDefinition td) => new()
    {
        Type     = "function",
        Function = new GroqToolFunction
        {
            Name        = td.Name,
            Description = td.Description,
            Parameters  = new GroqToolParameters
            {
                Type       = "object",
                Properties = td.Parameters.Properties.ToDictionary(
                    kv => kv.Key,
                    kv => (object)new { type = kv.Value.Type, description = kv.Value.Description }),
                Required   = td.Parameters.Required
            }
        }
    };

    private static AgentChatResponse MapResponse(GroqResponse response)
    {
        var message = response.Choices.FirstOrDefault()?.Message
                      ?? throw new InvalidOperationException("Groq no devolvió ningún choice.");

        List<ToolCall>? toolCalls = null;

        if (message.ToolCalls?.Count > 0)
        {
            toolCalls = message.ToolCalls.Select(tc =>
            {
                // Groq devuelve arguments como string JSON — lo parseamos a JsonElement
                JsonElement args;
                try   { args = JsonDocument.Parse(tc.Function?.Arguments ?? "{}").RootElement; }
                catch { args = JsonDocument.Parse("{}").RootElement; }

                return new ToolCall
                {
                    Id        = tc.Id ?? Guid.NewGuid().ToString("N")[..8],
                    Name      = tc.Function?.Name ?? "",
                    Arguments = args
                };
            }).ToList();
        }

        return new AgentChatResponse
        {
            Message = new ChatMessage("assistant", message.Content, toolCalls)
        };
    }

    // ── DTOs internos (formato OpenAI / Groq) ────────────────────────────────

    private class GroqRequest
    {
        public string            Model    { get; set; } = "";
        public List<GroqMessage> Messages { get; set; } = [];
        public List<GroqTool>?   Tools    { get; set; }
    }

    private class GroqMessage
    {
        public string               Role       { get; set; } = "";
        public string?              Content    { get; set; }
        public string?              ToolCallId { get; set; }
        public List<GroqToolCall>?  ToolCalls  { get; set; }
    }

    private class GroqToolCall
    {
        public string?               Id       { get; set; }
        public string                Type     { get; set; } = "function";
        public GroqToolCallFunction? Function { get; set; }
    }

    private class GroqToolCallFunction
    {
        public string? Name      { get; set; }
        public string? Arguments { get; set; }
    }

    private class GroqTool
    {
        public string           Type     { get; set; } = "function";
        public GroqToolFunction Function { get; set; } = new();
    }

    private class GroqToolFunction
    {
        public string              Name        { get; set; } = "";
        public string              Description { get; set; } = "";
        public GroqToolParameters  Parameters  { get; set; } = new();
    }

    private class GroqToolParameters
    {
        public string                     Type       { get; set; } = "object";
        public Dictionary<string, object> Properties { get; set; } = new();
        public List<string>               Required   { get; set; } = [];
    }

    private class GroqResponse
    {
        public List<GroqChoice> Choices { get; set; } = [];
    }

    private class GroqChoice
    {
        public GroqMessage Message { get; set; } = new();
    }
}
