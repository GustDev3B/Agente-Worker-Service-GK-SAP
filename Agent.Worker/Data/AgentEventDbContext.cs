using Agent.Worker.Models;
using Microsoft.EntityFrameworkCore;

namespace Agent.Worker.Data;

public class AgentEventDbContext : DbContext
{
    public DbSet<AgentEvent> AgentEvents { get; set; } = null!;

    public AgentEventDbContext(DbContextOptions<AgentEventDbContext> options) : base(options) { }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<AgentEvent>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.SessionId).HasMaxLength(32).IsRequired();
            e.Property(x => x.EventType).HasMaxLength(64).IsRequired();
            e.Property(x => x.Payload).HasColumnType("text");
            e.HasIndex(x => x.SessionId);
            e.HasIndex(x => new { x.Consumed, x.Timestamp });
        });
    }
}
