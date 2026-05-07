using Agent.Core.Models;
using Agent.Worker.Agents;
using Agent.Worker.Data;
using Agent.Worker.Interfaces;
using Agent.Worker.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Text.Json;

namespace Agent.Worker.Workers;

/// <summary>
/// BackgroundService que gestiona el ciclo de vida del agente de monitoreo GK→SAP.
/// Delega toda la inteligencia a MigrationAgent, que a su vez usa el LLM para decidir.
/// </summary>
public class MigrationMonitorWorker : BackgroundService
{
    private readonly IServiceScopeFactory             _scopeFactory;
    private readonly MigrationConfig                  _config;
    private readonly ILogger<MigrationMonitorWorker>  _logger;

    public MigrationMonitorWorker(
        IServiceScopeFactory            scopeFactory,
        MigrationConfig                 config,
        ILogger<MigrationMonitorWorker> logger)
    {
        _scopeFactory = scopeFactory;
        _config       = config;
        _logger       = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation(
            "Worker iniciado. Horario={Inicio}-{Fin} | Intervalo={Min}min | Max={Max} intentos",
            _config.HoraInicio, _config.HoraFin,
            _config.IntervaloMinutos, _config.MaximoIntentos);

        await InicializarBaseDeDatosAsync(stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            var esperaInicio = CalcularEsperaHastaInicio();
            if (esperaInicio > TimeSpan.Zero)
            {
                _logger.LogInformation(
                    "Fuera de ventana de monitoreo. Próximo ciclo en {Horas:F1}h (a las {Hora}).",
                    esperaInicio.TotalHours, _config.HoraInicio);
                await Task.Delay(esperaInicio, stoppingToken);
                continue;
            }

            try
            {
                await EjecutarCicloAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error inesperado en ciclo del agente. Reintentando en 5 minutos.");
                await TryPublicarErrorAsync(ex.Message);
                await Task.Delay(TimeSpan.FromMinutes(5), stoppingToken);
            }
        }

        _logger.LogInformation("Worker detenido.");
    }

    // ──────────────────────────────────────────────────────────
    // CICLO: delega al agente, publica evento de sesión
    // ──────────────────────────────────────────────────────────
    private async Task EjecutarCicloAsync(CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var agent = scope.ServiceProvider.GetRequiredService<MigrationAgent>();

        // CancellationToken que expira al llegar HoraFin
        var tiempoHastaFin = CalcularTiempoHastaFin();
        using var ctsFin   = CancellationTokenSource.CreateLinkedTokenSource(ct);
        ctsFin.CancelAfter(tiempoHastaFin);

        _logger.LogInformation(
            "Iniciando ciclo. Ventana activa por {Min:F0} minutos (hasta {Fin}).",
            tiempoHastaFin.TotalMinutes, _config.HoraFin);

        var sessionId = Guid.NewGuid().ToString("N")[..8];
        await PublicarEventoAsync(sessionId, AgentEventTypes.SesionIniciada, new
        {
            sessionId,
            fecha            = _config.ResolveFecha(),
            maxIntentos      = _config.MaximoIntentos,
            intervaloMinutos = _config.IntervaloMinutos
        }, ct);

        AgentContext ctx;
        try
        {
            ctx = await agent.ExecuteAsync(sessionId, ctsFin.Token);
        }
        catch (OperationCanceledException) when (ctsFin.Token.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            _logger.LogWarning("Ciclo interrumpido al llegar HoraFin ({Fin}).", _config.HoraFin);
            await TryPublicarErrorAsync($"Ciclo interrumpido al llegar HoraFin ({_config.HoraFin}).");
            return;
        }

        await PublicarEventoAsync(ctx.SessionId, AgentEventTypes.SesionFinalizada, new
        {
            sessionId    = ctx.SessionId,
            fecha        = ctx.Fecha,
            iteraciones  = ctx.IteracionActual,
            finalizado   = ctx.FinalizarRequested,
            mensajeFinal = ctx.MensajeFinal
        }, ct);

        _logger.LogInformation("Ciclo completado. El worker esperará hasta la próxima ventana ({Inicio}).", _config.HoraInicio);
    }

    // ──────────────────────────────────────────────────────────
    // INICIALIZACIÓN
    // ──────────────────────────────────────────────────────────
    private async Task InicializarBaseDeDatosAsync(CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();

        try
        {
            var sqliteCtx = scope.ServiceProvider.GetRequiredService<Agent.Tools.Data.AgentDbContext>();
            await sqliteCtx.Database.EnsureCreatedAsync(ct);
            _logger.LogInformation("SQLite (memoria operacional) inicializado.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error al inicializar SQLite.");
        }

        try
        {
            var pgCtx = scope.ServiceProvider.GetRequiredService<AgentEventDbContext>();
            await pgCtx.Database.EnsureCreatedAsync(ct);
            _logger.LogInformation("PostgreSQL/SQLite (eventos consumibles) inicializado.");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Base de datos de eventos no disponible.");
        }
    }

    // ──────────────────────────────────────────────────────────
    // HELPERS
    // ──────────────────────────────────────────────────────────
    private async Task PublicarEventoAsync(string sessionId, string tipo, object payload, CancellationToken ct = default)
    {
        try
        {
            using var scope  = _scopeFactory.CreateScope();
            var eventStore   = scope.ServiceProvider.GetService<IAgentEventStore>();
            if (eventStore is null) return;

            await eventStore.PublishAsync(new AgentEvent
            {
                SessionId = sessionId,
                EventType = tipo,
                Payload   = JsonSerializer.Serialize(payload),
                Timestamp = DateTime.UtcNow
            }, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "No se pudo publicar evento {Tipo}.", tipo);
        }
    }

    private async Task TryPublicarErrorAsync(string mensaje)
    {
        try
        {
            await PublicarEventoAsync("unknown", AgentEventTypes.ErrorCiclo, new
            {
                error     = mensaje,
                timestamp = DateTime.UtcNow
            });
        }
        catch { /* silenciar */ }
    }

    /// <summary>
    /// Devuelve true si la hora actual está dentro de la ventana [HoraInicio, HoraFin].
    /// Maneja el cruce de medianoche (ej: 22:55 → 00:10).
    /// </summary>
    private bool EstaEnVentana()
    {
        var ahora  = TimeOnly.FromDateTime(DateTime.Now);
        var inicio = _config.HoraInicio;
        var fin    = _config.HoraFin;

        return inicio <= fin
            ? ahora >= inicio && ahora <= fin          // sin cruce de medianoche
            : ahora >= inicio || ahora <= fin;         // con cruce de medianoche
    }

    /// <summary>
    /// Tiempo que falta para que comience la próxima ventana.
    /// Devuelve Zero si ya estamos dentro de la ventana.
    /// </summary>
    private TimeSpan CalcularEsperaHastaInicio()
    {
        if (EstaEnVentana()) return TimeSpan.Zero;

        var ahora  = TimeOnly.FromDateTime(DateTime.Now);
        var inicio = _config.HoraInicio;

        var espera = ahora < inicio
            ? inicio.ToTimeSpan() - ahora.ToTimeSpan()
            : TimeSpan.FromHours(24) - ahora.ToTimeSpan() + inicio.ToTimeSpan();

        return espera < TimeSpan.FromMinutes(1) ? TimeSpan.FromMinutes(1) : espera;
    }

    /// <summary>
    /// Tiempo que queda hasta que la ventana cierre (HoraFin).
    /// Maneja el cruce de medianoche.
    /// </summary>
    private TimeSpan CalcularTiempoHastaFin()
    {
        var ahora = TimeOnly.FromDateTime(DateTime.Now);
        var fin   = _config.HoraFin;

        var restante = ahora <= fin
            ? fin.ToTimeSpan() - ahora.ToTimeSpan()
            : TimeSpan.FromHours(24) - ahora.ToTimeSpan() + fin.ToTimeSpan();

        return restante < TimeSpan.FromMinutes(1) ? TimeSpan.FromMinutes(1) : restante;
    }
}
