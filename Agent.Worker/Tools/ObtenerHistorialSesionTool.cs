using Agent.Core.Interfaces;
using Agent.Worker.Interfaces;
using Agent.Worker.Models;
using System.Text.Json;

namespace Agent.Worker.Tools;

public class ObtenerHistorialSesionTool : IAgentTool
{
    private readonly IMigrationMemoryService _memoria;

    public ObtenerHistorialSesionTool(IMigrationMemoryService memoria) => _memoria = memoria;

    public string Nombre => "obtener_historial_sesion";

    public ToolDefinition Definicion => new()
    {
        Name        = Nombre,
        Description = "Recupera el historial de iteraciones de la sesión actual desde la memoria operacional (SQLite).",
        Parameters  = new ToolParameters
        {
            Properties = new Dictionary<string, ToolProperty>
            {
                ["sessionId"] = new() { Type = "string", Description = "ID de la sesión actual" }
            },
            Required = ["sessionId"]
        }
    };

    public async Task<string> EjecutarAsync(JsonElement parametros, AgentContext contexto, CancellationToken ct = default)
    {
        var sessionId = parametros.TryGetProperty("sessionId", out var s) ? s.GetString() ?? contexto.SessionId : contexto.SessionId;

        var historial = await _memoria.GetHistorialSesionAsync(sessionId);

        if (historial.Count == 0)
            return JsonSerializer.Serialize(new { sessionId, mensaje = "Sin historial previo.", iteraciones = Array.Empty<object>() });

        return JsonSerializer.Serialize(new
        {
            sessionId,
            totalIteraciones = historial.Count,
            iteraciones = historial.OrderBy(h => h.Iteracion).Select(h => new
            {
                h.Iteracion,
                h.CantidadErrores,
                h.DiferenciaTotal,
                h.Decision,
                h.Mensaje,
                timestamp = h.Timestamp.ToString("HH:mm:ss")
            })
        });
    }
}
