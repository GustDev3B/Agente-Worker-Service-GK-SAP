using Agent.Worker.Models;

namespace Agent.Worker.Interfaces;

/// <summary>
/// Servicio LLM que soporta conversación multi-turno con tool calling.
/// El LLM decide qué herramientas llamar y cuándo finalizar.
/// </summary>
public interface IAgentLLMService
{
    Task<AgentChatResponse> ChatAsync(AgentChatRequest request, CancellationToken ct = default);
}
