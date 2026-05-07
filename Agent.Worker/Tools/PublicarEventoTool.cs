using Agent.Worker.Interfaces;
using Agent.Worker.Models;
using System.Text.Json;

namespace Agent.Worker.Tools;

public class PublicarEventoTool : IAgentTool
{
    private readonly IAgentEventStore _eventStore;

    public PublicarEventoTool(IAgentEventStore eventStore) => _eventStore = eventStore;

    public string Nombre => "publicar_evento";

    public ToolDefinition Definicion => new()
    {
        Name        = Nombre,
        Description = "Publica un evento consumible en PostgreSQL (o SQLite fallback). Tipos válidos: MigracionOk, LimiteIntentosAlcanzado, DiscrepanciaDetectada, NotificacionEnviada.",
        Parameters  = new ToolParameters
        {
            Properties = new Dictionary<string, ToolProperty>
            {
                ["tipo"]  = new() { Type = "string", Description = "Tipo del evento. Ej: MigracionOk, LimiteIntentosAlcanzado" },
                ["datos"] = new() { Type = "string", Description = "Datos del evento en formato JSON o texto descriptivo" }
            },
            Required = ["tipo", "datos"]
        }
    };

    public async Task<string> EjecutarAsync(JsonElement parametros, AgentContext contexto, CancellationToken ct = default)
    {
        var tipo  = parametros.TryGetProperty("tipo",  out var t) ? t.GetString() ?? ""  : "";
        var datos = parametros.TryGetProperty("datos", out var d) ? d.GetString() ?? "{}" : "{}";

        try
        {
            var evento = new AgentEvent
            {
                SessionId = contexto.SessionId,
                EventType = tipo,
                Payload   = datos,
                Timestamp = DateTime.UtcNow
            };

            await _eventStore.PublishAsync(evento, ct);

            return JsonSerializer.Serialize(new { ok = true, tipo, sessionId = contexto.SessionId });
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new { ok = false, error = ex.Message, tipo });
        }
    }
}
