using Agent.Worker.Models;
using System.Text.Json;

namespace Agent.Worker.Interfaces;

/// <summary>
/// Herramienta que el LLM puede invocar durante el agent loop.
/// Cada tool tiene un contrato claro: nombre, descripción, parámetros y resultado JSON.
/// </summary>
public interface IAgentTool
{
    string Nombre { get; }
    ToolDefinition Definicion { get; }
    Task<string> EjecutarAsync(JsonElement parametros, AgentContext contexto, CancellationToken ct = default);
}
