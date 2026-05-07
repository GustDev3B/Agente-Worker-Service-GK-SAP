using Agent.Worker.Models;

namespace Agent.Worker.Interfaces;

public interface IAgentEventStore
{
    Task PublishAsync(AgentEvent evento, CancellationToken ct = default);
    Task<List<AgentEvent>> GetPendingAsync(int maxCount = 50, CancellationToken ct = default);
    Task MarkConsumedAsync(int eventId, CancellationToken ct = default);
}
