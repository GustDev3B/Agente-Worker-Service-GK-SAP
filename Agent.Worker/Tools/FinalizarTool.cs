using Agent.Worker.Interfaces;
using Agent.Worker.Models;
using System.Text.Json;

namespace Agent.Worker.Tools;

public class FinalizarTool : IAgentTool
{
    public string Nombre => "finalizar";

    public ToolDefinition Definicion => new()
    {
        Name        = Nombre,
        Description = "Finaliza la sesión del agente. Llamar cuando el flujo completo haya terminado (notificación enviada y evento publicado).",
        Parameters  = new ToolParameters
        {
            Properties = new Dictionary<string, ToolProperty>
            {
                ["mensaje"] = new() { Type = "string", Description = "Mensaje resumen del resultado final de la sesión" }
            },
            Required = ["mensaje"]
        }
    };

    public Task<string> EjecutarAsync(JsonElement parametros, AgentContext contexto, CancellationToken ct = default)
    {
        var mensaje = parametros.TryGetProperty("mensaje", out var m) ? m.GetString() ?? "Sesión finalizada." : "Sesión finalizada.";

        contexto.FinalizarRequested = true;
        contexto.MensajeFinal       = mensaje;

        return Task.FromResult(JsonSerializer.Serialize(new
        {
            ok        = true,
            sessionId = contexto.SessionId,
            mensaje,
            iteracionesTotales = contexto.IteracionActual
        }));
    }
}
