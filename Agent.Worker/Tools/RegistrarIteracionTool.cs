using Agent.Core.Interfaces;
using Agent.Core.Models;
using Agent.Worker.Interfaces;
using Agent.Worker.Models;
using System.Text.Json;

namespace Agent.Worker.Tools;

public class RegistrarIteracionTool : IAgentTool
{
    private readonly IMigrationMemoryService _memoria;

    public RegistrarIteracionTool(IMigrationMemoryService memoria) => _memoria = memoria;

    public string Nombre => "registrar_iteracion";

    public ToolDefinition Definicion => new()
    {
        Name        = Nombre,
        Description = "Guarda en la memoria operacional (SQLite) el resultado de la iteración actual: análisis, decisión y mensaje del agente.",
        Parameters  = new ToolParameters
        {
            Properties = new Dictionary<string, ToolProperty>
            {
                ["totalTiendas"]    = new() { Type = "integer", Description = "Total de tiendas analizadas" },
                ["cantidadErrores"] = new() { Type = "integer", Description = "Cantidad de tiendas con discrepancias" },
                ["diferenciaTotal"] = new() { Type = "number",  Description = "Suma total de diferencias monetarias" },
                ["decision"]        = new() { Type = "string",  Description = "Decisión tomada: REINTENTAR o FINALIZAR" },
                ["mensaje"]         = new() { Type = "string",  Description = "Mensaje explicativo del agente" }
            },
            Required = ["totalTiendas", "cantidadErrores", "diferenciaTotal", "decision", "mensaje"]
        }
    };

    public async Task<string> EjecutarAsync(JsonElement parametros, AgentContext contexto, CancellationToken ct = default)
    {
        var log = new MigrationMonitorLog
        {
            SessionId       = contexto.SessionId,
            Timestamp       = DateTime.UtcNow,
            Iteracion       = contexto.IteracionActual,
            TotalTiendas    = parametros.TryGetProperty("totalTiendas",    out var tt)  ? tt.GetInt32()      : 0,
            CantidadErrores = parametros.TryGetProperty("cantidadErrores", out var ce)  ? ce.GetInt32()      : 0,
            DiferenciaTotal = parametros.TryGetProperty("diferenciaTotal", out var dt)  ? (decimal)dt.GetDouble() : 0,
            Decision        = parametros.TryGetProperty("decision",        out var dec) ? dec.GetString() ?? "" : "",
            Mensaje         = parametros.TryGetProperty("mensaje",         out var msg) ? msg.GetString() ?? "" : ""
        };

        await _memoria.LogAsync(log);

        return JsonSerializer.Serialize(new
        {
            ok        = true,
            sessionId = contexto.SessionId,
            iteracion = log.Iteracion,
            decision  = log.Decision
        });
    }
}
