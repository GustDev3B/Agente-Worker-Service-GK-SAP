using Agent.Core.Interfaces;
using Agent.Core.Models;
using Agent.Worker.Interfaces;
using Agent.Worker.Models;
using System.Text.Json;

namespace Agent.Worker.Tools;

public class ObtenerEstadoMigracionTool : IAgentTool
{
    private readonly IMigrationApiClient        _apiClient;
    private readonly IMigrationValidationService _validacion;
    private readonly MigrationConfig            _config;

    public ObtenerEstadoMigracionTool(
        IMigrationApiClient         apiClient,
        IMigrationValidationService validacion,
        MigrationConfig             config)
    {
        _apiClient  = apiClient;
        _validacion = validacion;
        _config     = config;
    }

    public string Nombre => "obtener_estado_migracion";

    public ToolDefinition Definicion => new()
    {
        Name        = Nombre,
        Description = "Consulta la API de migración GK→SAP y analiza el estado actual para una fecha dada. Retorna el análisis con cantidad de errores y discrepancias.",
        Parameters  = new ToolParameters
        {
            Properties = new Dictionary<string, ToolProperty>
            {
                ["fecha"] = new() { Type = "string", Description = "Fecha a consultar en formato yyyy-MM-dd" }
            },
            Required = ["fecha"]
        }
    };

    public async Task<string> EjecutarAsync(JsonElement parametros, AgentContext contexto, CancellationToken ct = default)
    {
        var fecha = parametros.TryGetProperty("fecha", out var f) ? f.GetString() ?? contexto.Fecha : contexto.Fecha;

        try
        {
            var comparaciones = await _apiClient.GetComparacionesAsync(fecha, _config.ToAddress, false);
            var analisis      = _validacion.Analizar(comparaciones, _config.Tolerancia);

            contexto.UltimoAnalisis = analisis;

            var resultado = new
            {
                fecha,
                totalTiendas    = analisis.TotalTiendas,
                cantidadErrores = analisis.CantidadErrores,
                diferenciaTotal = analisis.DiferenciaTotal,
                todoOk          = analisis.TodoOk,
                tiendasConError = analisis.TiendasConError.Take(5).Select(t => new
                {
                    t.TiendaId,
                    t.Diferencia
                }),
                tiendasConErrorMostradas = Math.Min(5, analisis.TiendasConError.Count)
            };

            return JsonSerializer.Serialize(resultado);
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new { error = ex.Message, fecha });
        }
    }
}
