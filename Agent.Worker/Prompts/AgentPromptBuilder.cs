using Agent.Worker.Interfaces;
using Agent.Worker.Models;
using Agent.Worker.Policy;

namespace Agent.Worker.Prompts;

/// <summary>
/// Construye el system prompt que guía al LLM como agente.
/// Incorpora la política, el contexto actual y las capacidades disponibles.
/// </summary>
public class AgentPromptBuilder
{
    private readonly MigrationPolicy      _policy;
    private readonly IEnumerable<IAgentTool> _tools;

    public AgentPromptBuilder(MigrationPolicy policy, IEnumerable<IAgentTool> tools)
    {
        _policy = policy;
        _tools  = tools;
    }

    public string Build(AgentContext ctx) => $"""
        Eres un agente de monitoreo GK→SAP. Sesión: {ctx.SessionId} | Fecha: {ctx.Fecha}

        {_policy.GetText()}

        REGLA CRÍTICA: SIEMPRE responde llamando una herramienta. NUNCA respondas con texto libre.
        Cuando termines el flujo completo llama `finalizar`.
        """;
}
