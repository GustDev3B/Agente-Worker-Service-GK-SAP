using Agent.Core.Models;

namespace Agent.Worker.Models;

public class AgentContext
{
    public string          SessionId          { get; set; } = Guid.NewGuid().ToString("N")[..8];
    public string          Fecha              { get; set; } = DateTime.Today.ToString("yyyy-MM-dd");
    public int             IteracionActual    { get; set; }
    public bool            WaitRequested      { get; set; }
    public TimeSpan        WaitDuration       { get; set; } = TimeSpan.FromMinutes(5);
    public bool            FinalizarRequested { get; set; }
    public string?         MensajeFinal       { get; set; }
    public MigrationAnalisis? UltimoAnalisis  { get; set; }
}
