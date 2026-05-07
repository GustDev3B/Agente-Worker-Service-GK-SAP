using Agent.Core.Interfaces;
using Agent.Core.Models;
using Agent.Infrastructure.Services;
using Agent.Tools.Clients;
using Agent.Tools.Data;
using Agent.Tools.Services;
using Agent.Worker.Agents;
using Agent.Worker.Data;
using Agent.Worker.Interfaces;
using Agent.Worker.Policy;
using Agent.Worker.Prompts;
using Agent.Worker.Services;
using Agent.Worker.Tools;
using Agent.Worker.Workers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Serilog;
using Serilog.Events;

// ── Bootstrap logger ────────────────────────────────────────────────────────
Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Information()
    .WriteTo.Console(outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] {Message:lj}{NewLine}{Exception}")
    .CreateBootstrapLogger();

try
{
    Log.Information("Iniciando Agent.Worker — Agente Autónomo Monitor GK→SAP");

    var builder = Host.CreateApplicationBuilder(args);

    // ── Serilog ──────────────────────────────────────────────────────────────
    var seqUrl = builder.Configuration["Seq:Url"] ?? "http://localhost:5341";

    builder.Services.AddSerilog((_, lc) => lc
        .ReadFrom.Configuration(builder.Configuration)
        .MinimumLevel.Information()
        .MinimumLevel.Override("Microsoft.EntityFrameworkCore", LogEventLevel.Warning)
        .MinimumLevel.Override("System.Net.Http",               LogEventLevel.Warning)
        .MinimumLevel.Override("Microsoft.Hosting",             LogEventLevel.Warning)
        .Enrich.WithProperty("Application", "MonitorMigracionGkSap-Worker")
        .Enrich.WithEnvironmentName()
        .Enrich.WithMachineName()
        .WriteTo.Console(outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] {Message:lj}{NewLine}{Exception}")
        .WriteTo.Seq(seqUrl));

    // ── MigrationConfig ───────────────────────────────────────────────────────
    var migCfg = new MigrationConfig();
    builder.Configuration.GetSection("MigrationMonitor").Bind(migCfg);
    if (TimeOnly.TryParse(builder.Configuration["MigrationMonitor:HoraInicio"], out var hi)) migCfg.HoraInicio = hi;
    if (TimeOnly.TryParse(builder.Configuration["MigrationMonitor:HoraFin"],    out var hf)) migCfg.HoraFin    = hf;
    builder.Services.AddSingleton(migCfg);

    // ── SQLite — memoria operacional (proyecto console, reutilizado sin modificar)
    var sqliteConn = builder.Configuration.GetConnectionString("AgentDB") ?? "Data Source=agent_memory.db";
    builder.Services.AddDbContext<AgentDbContext>(o => o.UseSqlite(sqliteConn));

    // ── PostgreSQL — eventos consumibles ────────────────────────────────────
    var pgConn = builder.Configuration.GetConnectionString("AgentEvents");
    if (!string.IsNullOrWhiteSpace(pgConn))
    {
        builder.Services.AddDbContext<AgentEventDbContext>(o => o.UseNpgsql(pgConn));
        Log.Information("PostgreSQL configurado para eventos consumibles.");
    }
    else
    {
        builder.Services.AddDbContext<AgentEventDbContext>(o =>
            o.UseSqlite("Data Source=agent_events_fallback.db"));
        Log.Warning("PostgreSQL no configurado. Usando SQLite como fallback para eventos.");
    }
    builder.Services.AddScoped<IAgentEventStore, AgentEventStoreService>();

    // ── Servicios del proyecto console (reutilizados) ───────────────────────
    builder.Services.AddHttpClient<IMigrationApiClient, MigrationApiClient>()
        .ConfigureHttpClient(c => c.Timeout = TimeSpan.FromSeconds(30));

    builder.Services.AddScoped<IMigrationValidationService, MigrationValidationService>();
    builder.Services.AddScoped<IMigrationMemoryService,     MigrationMemoryService>();
    builder.Services.AddScoped<INotificationService,        NotificationService>();

    // ── IAgentLLMService — selección automática por API keys disponibles ─────
    //   Prioridad: Claude > Groq > Ollama (local)
    var claudeKey = builder.Configuration["LLM:ClaudeApiKey"];
    var groqKey   = builder.Configuration["LLM:GroqApiKey"];

    if (!string.IsNullOrWhiteSpace(claudeKey))
    {
        builder.Services.AddHttpClient<IAgentLLMService, ClaudeAgentLLMService>(client =>
        {
            client.BaseAddress = new Uri("https://api.anthropic.com");
            client.Timeout     = TimeSpan.FromMinutes(2);
        });
        Log.Information("LLM: Claude ({Model})", builder.Configuration["LLM:ClaudeModel"] ?? "claude-haiku-4-5-20251001");
    }
    else if (!string.IsNullOrWhiteSpace(groqKey))
    {
        builder.Services.AddHttpClient<IAgentLLMService, GroqAgentLLMService>(client =>
        {
            client.BaseAddress = new Uri("https://api.groq.com");
            client.Timeout     = TimeSpan.FromMinutes(2);
        });
        Log.Information("LLM: Groq ({Model})", builder.Configuration["LLM:GroqModel"] ?? "llama-3.1-8b-instant");
    }
    else
    {
        throw new InvalidOperationException(
            "No hay LLM configurado. Agrega LLM:ClaudeApiKey o LLM:GroqApiKey en appsettings.json.");
    }

    // ── Herramientas del agente ──────────────────────────────────────────────
    builder.Services.AddScoped<ObtenerEstadoMigracionTool>();
    builder.Services.AddScoped<ObtenerHistorialSesionTool>();
    builder.Services.AddScoped<RegistrarIteracionTool>();
    builder.Services.AddScoped<EnviarNotificacionTool>();
    builder.Services.AddScoped<PublicarEventoTool>();
    builder.Services.AddScoped<EsperarYReintentarTool>();
    builder.Services.AddScoped<FinalizarTool>();

    // Registrar todas las herramientas como IAgentTool (para inyección en AgentPromptBuilder)
    builder.Services.AddScoped<IAgentTool>(sp => sp.GetRequiredService<ObtenerEstadoMigracionTool>());
    builder.Services.AddScoped<IAgentTool>(sp => sp.GetRequiredService<ObtenerHistorialSesionTool>());
    builder.Services.AddScoped<IAgentTool>(sp => sp.GetRequiredService<RegistrarIteracionTool>());
    builder.Services.AddScoped<IAgentTool>(sp => sp.GetRequiredService<EnviarNotificacionTool>());
    builder.Services.AddScoped<IAgentTool>(sp => sp.GetRequiredService<PublicarEventoTool>());
    builder.Services.AddScoped<IAgentTool>(sp => sp.GetRequiredService<EsperarYReintentarTool>());
    builder.Services.AddScoped<IAgentTool>(sp => sp.GetRequiredService<FinalizarTool>());

    // ── Política y constructores de prompt ────────────────────────────────────
    builder.Services.AddScoped<MigrationPolicy>();
    builder.Services.AddScoped<AgentPromptBuilder>();

    // ── Agente autónomo ────────────────────────────────────────────────────────
    builder.Services.AddScoped<MigrationAgent>();

    // ── Worker ────────────────────────────────────────────────────────────────
    builder.Services.AddHostedService<MigrationMonitorWorker>();

    var host = builder.Build();
    await host.RunAsync();

    return 0;
}
catch (Exception ex) when (ex is not OperationCanceledException)
{
    Log.Fatal(ex, "Agent.Worker terminó de forma inesperada.");
    return 1;
}
finally
{
    await Log.CloseAndFlushAsync();
}
