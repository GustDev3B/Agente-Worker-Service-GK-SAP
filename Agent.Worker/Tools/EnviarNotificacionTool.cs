using Agent.Core.Interfaces;
using Agent.Core.Models;
using Agent.Worker.Interfaces;
using Agent.Worker.Models;
using System.Text.Json;

namespace Agent.Worker.Tools;

public class EnviarNotificacionTool : IAgentTool
{
    private readonly INotificationService _notificacion;
    private readonly MigrationConfig      _config;

    public EnviarNotificacionTool(INotificationService notificacion, MigrationConfig config)
    {
        _notificacion = notificacion;
        _config       = config;
    }

    public string Nombre => "enviar_notificacion";

    public ToolDefinition Definicion => new()
    {
        Name        = Nombre,
        Description = "Envía la notificación por correo con el resultado final del monitoreo.",
        Parameters  = new ToolParameters
        {
            Properties = new Dictionary<string, ToolProperty>
            {
                ["mensaje"] = new() { Type = "string", Description = "Mensaje resumen del resultado del agente para incluir en el correo" }
            },
            Required = ["mensaje"]
        }
    };

    public async Task<string> EjecutarAsync(JsonElement parametros, AgentContext contexto, CancellationToken ct = default)
    {
        var mensaje  = parametros.TryGetProperty("mensaje", out var m) ? m.GetString() ?? "" : "";
        var analisis = contexto.UltimoAnalisis ?? new MigrationAnalisis();

        try
        {
            await _notificacion.NotificarAsync(contexto.Fecha, _config.ToAddress, analisis, mensaje);

            return JsonSerializer.Serialize(new
            {
                ok             = true,
                toAddress      = _config.ToAddress,
                modoSimulacion = _config.ModoSimulacionCorreo,
                fecha          = contexto.Fecha
            });
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new { ok = false, error = ex.Message });
        }
    }
}
