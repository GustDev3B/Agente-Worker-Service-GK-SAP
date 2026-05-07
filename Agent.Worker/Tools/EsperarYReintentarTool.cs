using Agent.Core.Models;
using Agent.Worker.Interfaces;
using Agent.Worker.Models;
using System.Text.Json;

namespace Agent.Worker.Tools;

public class EsperarYReintentarTool : IAgentTool
{
    private readonly MigrationConfig _config;

    public EsperarYReintentarTool(MigrationConfig config) => _config = config;

    public string Nombre => "esperar_y_reintentar";

    public ToolDefinition Definicion => new()
    {
        Name        = Nombre,
        Description = "Solicita al agente que espere el intervalo configurado y reintente el monitoreo. Usar cuando hay errores y quedan intentos disponibles.",
        Parameters  = new ToolParameters
        {
            Properties = new Dictionary<string, ToolProperty>
            {
                ["minutos"] = new() { Type = "integer", Description = "Minutos a esperar (opcional, por defecto usa la configuración del sistema)" }
            },
            Required = []
        }
    };

    public Task<string> EjecutarAsync(JsonElement parametros, AgentContext contexto, CancellationToken ct = default)
    {
        var minutos = parametros.ValueKind == JsonValueKind.Object
                      && parametros.TryGetProperty("minutos", out var m)
                      && m.ValueKind == JsonValueKind.Number
            ? m.GetInt32()
            : _config.IntervaloMinutos;

        contexto.WaitRequested = true;
        contexto.WaitDuration  = TimeSpan.FromMinutes(minutos);

        return Task.FromResult(JsonSerializer.Serialize(new
        {
            ok               = true,
            esperandoMinutos = minutos,
            mensaje          = $"El agente esperará {minutos} minutos antes de reintentar. Iteración actual: {contexto.IteracionActual}"
        }));
    }
}
