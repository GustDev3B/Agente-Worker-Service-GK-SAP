namespace Agent.Worker.Models;

public class AgentEvent
{
    public int       Id          { get; set; }
    public string    SessionId   { get; set; } = string.Empty;
    public string    EventType   { get; set; } = string.Empty;
    public string    Payload     { get; set; } = string.Empty;
    public DateTime  Timestamp   { get; set; } = DateTime.UtcNow;
    public bool      Consumed    { get; set; } = false;
    public DateTime? ConsumedAt  { get; set; }
}

/// <summary>Tipos de eventos de negocio publicados por el agente GK→SAP.</summary>
public static class AgentEventTypes
{
    public const string SesionIniciada          = "SesionIniciada";
    public const string SesionFinalizada        = "SesionFinalizada";
    public const string DiscrepanciaDetectada   = "DiscrepanciaDetectada";
    public const string MigracionOk             = "MigracionOk";
    public const string LimiteIntentosAlcanzado = "LimiteIntentosAlcanzado";
    public const string NotificacionEnviada     = "NotificacionEnviada";
    public const string ErrorCiclo              = "ErrorCiclo";
}
