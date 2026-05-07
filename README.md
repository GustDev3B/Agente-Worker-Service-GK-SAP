# Monitor de Migración GK → SAP — Worker Service

Versión Worker Service del agente de monitoreo GK→SAP.

---

## Cómo Ejecutar

```bash
cd "C:\Projects\agents\agent worker gk-sap"
dotnet run --project Agent.Worker
```

---

## Configuración (`Agent.Worker/appsettings.json`)

```json
{
  "ConnectionStrings": {
    "AgentDB":     "Data Source=agent_memory.db",
    "AgentEvents": "Host=localhost;Port=5432;Database=agent_events;Username=postgres;Password=postgres"
  },
  "LLM": {
    "Provider":    "Ollama",
    "OllamaUrl":   "http://localhost:11434",
    "OllamaModel": "llama3"
  },
  "MigrationMonitor": {
    "IntervaloMinutos":     5,
    "HoraInicio":           "22:55",
    "HoraFin":              "00:10",
    "MaximoIntentos":       15,
    "Tolerancia":           0.01,
    "ModoSimulacionCorreo": true,
    "ToAddress":            "destinatario@empresa.com",
    "FechaMonitoreo":       ""
  },
  "Seq": { "Url": "http://localhost:5341" }
}
```

| Parámetro              | Descripción                                              |
|------------------------|----------------------------------------------------------|
| `AgentDB`              | SQLite — memoria operacional (historial de iteraciones)  |
| `AgentEvents`          | PostgreSQL — eventos consumibles (omitir = SQLite local) |
| `LLM:Provider`         | `"Ollama"` o `"Mock"` (sin LLM real)                    |
| `IntervaloMinutos`     | Espera entre reintentos del agente                       |
| `HoraInicio`/`HoraFin` | Ventana de monitoreo diaria                              |
| `MaximoIntentos`       | Límite de iteraciones antes de forzar notificación       |
| `Tolerancia`           | Diferencia máxima aceptable por tienda (decimal)         |
| `ModoSimulacionCorreo` | `true` = no envía correo real, solo loguea               |
| `FechaMonitoreo`       | Fecha a verificar (`yyyy-MM-dd`). Vacío = hoy            |

---

## Cómo Correr Ollama

```bash
# Instalar desde https://ollama.com/
ollama pull llama3

# Verificar disponibilidad
curl http://localhost:11434/api/tags
```

Para ejecutar sin Ollama:
```json
"LLM": { "Provider": "Mock" }
```

---

## Cómo Abrir Seq

```bash
# Con Docker
docker run --name seq -e ACCEPT_EULA=Y -p 5341:5341 -p 80:80 datalust/seq

# Abrir: http://localhost
```

Seq recibe únicamente **logs técnicos** (errores, performance, debugging).

---

## Cómo Consultar Memoria Operacional (SQLite)

```bash
sqlite3 agent_memory.db
```

```sql
-- Sesiones recientes
SELECT SessionId, COUNT(*) iteraciones, MAX(Timestamp) ultima
FROM MigrationMonitorLogs GROUP BY SessionId ORDER BY ultima DESC;

-- Detalle de una sesión
SELECT Iteracion, Timestamp, CantidadErrores, DiferenciaTotal, Decision
FROM MigrationMonitorLogs WHERE SessionId = 'abc12345' ORDER BY Iteracion;
```

---

## Cómo Consultar Eventos (PostgreSQL)

```sql
-- Eventos pendientes
SELECT id, session_id, event_type, timestamp
FROM "AgentEvents" WHERE consumed = false ORDER BY timestamp;

-- Resumen por tipo
SELECT event_type, COUNT(*) total
FROM "AgentEvents" GROUP BY event_type ORDER BY total DESC;

-- Sesiones que finalizaron OK
SELECT payload->>'sessionId', payload->>'fecha'
FROM "AgentEvents"
WHERE event_type = 'MigracionOk' ORDER BY timestamp DESC;
```

Si PostgreSQL no está configurado, los eventos se guardan en `agent_events_fallback.db` (SQLite).

---

## Flujo del Agente

```
Worker inicia
  │
  ├─► Inicializa SQLite (memoria) + PostgreSQL (eventos)
  │
  └─► LOOP PERMANENTE
        │
        ├─► Publica: SesionIniciada
        │
        ├─► Delega a MigrationMonitorAgent.ExecuteAsync()
        │     (lógica del proyecto console, sin modificar)
        │     ├── percibir: GET endpoint GK/SAP
        │     ├── razonar: validar + historial + Ollama
        │     ├── decidir: REINTENTAR o FINALIZAR
        │     ├── actuar: enviar notificación
        │     └── recordar: guardar en SQLite
        │
        ├─► Lee resultado desde SQLite
        ├─► Publica eventos según resultado:
        │     ├── DiscrepanciaDetectada (si hubo errores)
        │     ├── MigracionOk (si todo OK)
        │     ├── LimiteIntentosAlcanzado (si se agotaron intentos)
        │     ├── NotificacionEnviada
        │     └── SesionFinalizada
        │
        └─► Espera hasta HoraInicio del día siguiente
```

---

## Troubleshooting

**Ollama no disponible:** Cambiar `LLM:Provider` a `"Mock"`.

**PostgreSQL no disponible:** El Worker usa `agent_events_fallback.db` (SQLite). Log: `[WRN] PostgreSQL no configurado...`

**Fuera de ventana horaria:** El agente espera automáticamente hasta `HoraInicio`. Normal en ejecuciones diurnas.

**ModoSimulacionCorreo:** Siempre `true` por defecto. Cambiar a `false` solo en producción verificada.

**Compilar la solución:**
```bash
dotnet build AgentWorkerSolution.sln
```

Ver [AGENTS.md](AGENTS.md) para documentación técnica completa.
