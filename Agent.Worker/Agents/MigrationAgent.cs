using Agent.Core.Models;
using Agent.Worker.Interfaces;
using Agent.Worker.Models;
using Agent.Worker.Prompts;
using Microsoft.Extensions.Logging;

namespace Agent.Worker.Agents;

/// <summary>
/// Agente verdadero: el LLM controla el flujo de ejecución mediante tool calling.
/// Implementa el ciclo Percibir → Razonar → Decidir → Actuar → Recordar → Iterar
/// según el artículo https://www.webreactiva.com/blog/agentes-ia-programadores
///
/// Primitivas del agente (artículo):
///   1. LLM              — IAgentLLMService
///   2. Contexto/Instrucciones — AgentPromptBuilder + MigrationPolicy
///   3. Herramientas     — IEnumerable&lt;IAgentTool&gt;
///   4. Estado/Memoria   — AgentContext + SQLite (vía herramientas)
///   5. Bucle del agente — el while de este método
/// </summary>
public class MigrationAgent
{
    private readonly IAgentLLMService              _llm;
    private readonly IEnumerable<IAgentTool>       _tools;
    private readonly AgentPromptBuilder            _promptBuilder;
    private readonly MigrationConfig               _config;
    private readonly ILogger<MigrationAgent>       _logger;

    private const int MaxTurnosSeguridad = 50;

    public MigrationAgent(
        IAgentLLMService        llm,
        IEnumerable<IAgentTool> tools,
        AgentPromptBuilder      promptBuilder,
        MigrationConfig         config,
        ILogger<MigrationAgent> logger)
    {
        _llm           = llm;
        _tools         = tools;
        _promptBuilder = promptBuilder;
        _config        = config;
        _logger        = logger;
    }

    // ──────────────────────────────────────────────────────────────────────────
    // BUCLE DEL AGENTE
    // ──────────────────────────────────────────────────────────────────────────
    public async Task<AgentContext> ExecuteAsync(string? sessionId = null, CancellationToken ct = default)
    {
        var ctx = new AgentContext
        {
            SessionId = sessionId ?? Guid.NewGuid().ToString("N")[..8],
            Fecha     = _config.ResolveFecha()
        };

        _logger.LogInformation(
            "[Agente] Iniciando sesión {SessionId} para fecha {Fecha}",
            ctx.SessionId, ctx.Fecha);

        // ── 1. PERCIBIR: construir instrucciones + contexto inicial ─────────
        var mensajes   = new List<ChatMessage>();
        var toolDefs   = _tools.Select(t => t.Definicion).ToList();
        var toolIndex  = _tools.ToDictionary(t => t.Nombre, t => t);

        mensajes.Add(ChatMessage.System(_promptBuilder.Build(ctx)));
        mensajes.Add(ChatMessage.User(
            $"Inicia el monitoreo de migración GK→SAP para la fecha {ctx.Fecha}. " +
            $"Sesión: {ctx.SessionId}. Sigue la política y usa las herramientas disponibles."));

        var turno = 0;

        // ── BUCLE AGENTE ────────────────────────────────────────────────────
        while (!ct.IsCancellationRequested && turno < MaxTurnosSeguridad)
        {
            turno++;
            ctx.IteracionActual++;

            _logger.LogDebug("[Agente] Turno {Turno} — enviando {N} mensajes al LLM", turno, mensajes.Count);

            // ── 2. RAZONAR: el LLM decide qué hacer ───────────────────────
            AgentChatResponse respuesta;
            try
            {
                respuesta = await _llm.ChatAsync(new AgentChatRequest
                {
                    Messages = mensajes,
                    Tools    = toolDefs
                }, ct);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[Agente] Error al llamar al LLM en turno {Turno}", turno);
                break;
            }

            // Añadir respuesta del asistente al historial de mensajes
            mensajes.Add(respuesta.Message);

            // ── 3. DECIDIR: si el LLM no llama tools, terminó ──────────────
            if (!respuesta.HasToolCalls)
            {
                _logger.LogInformation(
                    "[Agente] LLM no llamó herramientas. Respuesta: {Msg}",
                    respuesta.Content ?? "(vacío)");
                break;
            }

            // ── 4. ACTUAR: ejecutar cada herramienta que el LLM invocó ─────
            foreach (var toolCall in respuesta.Message.ToolCalls!)
            {
                _logger.LogInformation(
                    "[Agente] Ejecutando herramienta: {Tool} | Args: {Args}",
                    toolCall.Name, toolCall.Arguments.ToString());

                string resultado;
                if (toolIndex.TryGetValue(toolCall.Name, out var tool))
                {
                    try
                    {
                        resultado = await tool.EjecutarAsync(toolCall.Arguments, ctx, ct);
                    }
                    catch (Exception ex)
                    {
                        resultado = $"{{\"error\": \"{ex.Message}\"}}";
                        _logger.LogWarning(ex, "[Agente] Error ejecutando herramienta {Tool}", toolCall.Name);
                    }
                }
                else
                {
                    resultado = $"{{\"error\": \"Herramienta '{toolCall.Name}' no encontrada.\"}}";
                    _logger.LogWarning("[Agente] Herramienta desconocida: {Tool}", toolCall.Name);
                }

                _logger.LogDebug("[Agente] Resultado de {Tool}: {Resultado}", toolCall.Name, resultado);

                // ── 5. RECORDAR: añadir resultado al contexto de mensajes ──
                mensajes.Add(ChatMessage.Tool(resultado, toolCall.Id));
            }

            // ── Verificar señales de control del agente ───────────────────
            if (ctx.FinalizarRequested)
            {
                _logger.LogInformation(
                    "[Agente] Sesión {SessionId} finalizada por herramienta. Mensaje: {Msg}",
                    ctx.SessionId, ctx.MensajeFinal);
                break;
            }

            // ── 6. ITERAR: esperar si se solicitó antes del próximo turno ──
            if (ctx.WaitRequested)
            {
                _logger.LogInformation(
                    "[Agente] Esperando {Min:F1} min antes de reintentar...",
                    ctx.WaitDuration.TotalMinutes);

                ctx.WaitRequested = false;

                try { await Task.Delay(ctx.WaitDuration, ct); }
                catch (OperationCanceledException) { break; }

                // Informar al LLM el estado actual de iteraciones para que aplique la política
                var intentosRestantes = _config.MaximoIntentos - ctx.IteracionActual;
                mensajes.Add(ChatMessage.User(
                    $"Iteración {ctx.IteracionActual} de {_config.MaximoIntentos} completada. " +
                    $"Intentos restantes: {intentosRestantes}. " +
                    (intentosRestantes <= 0
                        ? "Has alcanzado el límite máximo de intentos. Debes FINALIZAR ahora: envía notificación, publica evento y llama a finalizar."
                        : "Continúa el monitoreo según la política.")));
            }
        }

        if (turno >= MaxTurnosSeguridad)
            _logger.LogWarning("[Agente] Se alcanzó el límite de seguridad de {Max} turnos.", MaxTurnosSeguridad);

        _logger.LogInformation(
            "[Agente] Sesión {SessionId} completada. Turnos={Turno} | Finalizado={Ok}",
            ctx.SessionId, turno, ctx.FinalizarRequested);

        return ctx;
    }
}
