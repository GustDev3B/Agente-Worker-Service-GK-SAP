using Agent.Worker.Data;
using Agent.Worker.Interfaces;
using Agent.Worker.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Agent.Worker.Services;

public class AgentEventStoreService : IAgentEventStore
{
    private readonly AgentEventDbContext _context;
    private readonly ILogger<AgentEventStoreService> _logger;

    public AgentEventStoreService(
        AgentEventDbContext context,
        ILogger<AgentEventStoreService> logger)
    {
        _context = context;
        _logger  = logger;
    }

    public async Task PublishAsync(AgentEvent evento, CancellationToken ct = default)
    {
        _context.AgentEvents.Add(evento);
        await _context.SaveChangesAsync(ct);
        _logger.LogDebug("Evento publicado: {EventType} | Session={SessionId}", evento.EventType, evento.SessionId);
    }

    public Task<List<AgentEvent>> GetPendingAsync(int maxCount = 50, CancellationToken ct = default) =>
        _context.AgentEvents
            .Where(e => !e.Consumed)
            .OrderBy(e => e.Timestamp)
            .Take(maxCount)
            .ToListAsync(ct);

    public async Task MarkConsumedAsync(int eventId, CancellationToken ct = default)
    {
        var evento = await _context.AgentEvents.FindAsync(new object[] { eventId }, ct);
        if (evento is null) return;

        evento.Consumed   = true;
        evento.ConsumedAt = DateTime.UtcNow;
        await _context.SaveChangesAsync(ct);
    }
}
