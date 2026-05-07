# AGENTS.md — Monitor de Migración GK → SAP (Worker Service)

## Objetivo

Agente autónomo de monitoreo que verifica la correcta migración de datos de ventas y devoluciones desde **GK** (punto de venta) hacia **SAP** (ERP). Esta versión evoluciona el proyecto console existente hacia una arquitectura de producción real basada en Worker Service.

El agente:
- Percibe el estado de la migración consultando un endpoint REST
- Razona sobre las discrepancias usando un LLM local (Ollama)
- Decide si reintentar o finalizar basándose en la respuesta del LLM
- Actúa notificando por correo al finalizar
- Recuerda cada iteración en SQLite
- Publica eventos de negocio en PostgreSQL
- Itera automáticamente con ciclo diario

---

## Arquitectura

```
C:\Projects\agents\
│
├── control de monitoreo gk-sap console\    ← proyecto original (NO MODIFICADO)
│   ├── Agent.Core\        dominio: interfaces, modelos, agent loop, prompts
│   ├── Agent.Tools\       servicios: EF Core, SQLite, HTTP client
│   ├── Agent.Infrastructure\ LLM: Ollama + Mock
│   └── Agent.Runner\      consola (punto de entrada original)
│
└── agent worker gk-sap\                    ← ESTE PROYECTO (nuevo)
    ├── Agent.Worker\
    │   ├── Interfaces\    IAgentEventStore
    │   ├── Models\        AgentEvent, AgentEventTypes
    │   ├── Data\          AgentEventDbContext (PostgreSQL)
    │   ├── Services\      AgentEventStoreService
    │   ├── Workers\       MigrationMonitorWorker (BackgroundService)
    │   ├── Program.cs     host con Serilog + DI completo
    │   └── appsettings.json
    ├── AgentWorkerSolution.sln
    ├── AGENTS.md           (este archivo)
    └── README.md
```

**Principio clave:** Este proyecto **referencia** los .csproj del proyecto console usando rutas relativas. No copia ni duplica lógica. No modifica ningún archivo del proyecto original.

---

## Stack Tecnológico

| Capa                   | Tecnología                                               |
|------------------------|----------------------------------------------------------|
| Runtime                | .NET 9 / Worker Service (`Microsoft.NET.Sdk.Worker`)    |
| Agent loop (reutilizado) | `MigrationMonitorAgent` del proyecto console           |
| LLM                    | Ollama (llama3 o cualquier modelo local)                 |
| Memoria operacional    | SQLite vía EF Core (`AgentDbContext` del proyecto console)|
| Eventos consumibles    | PostgreSQL vía EF Core + Npgsql (`AgentEventDbContext`)  |
| Logs técnicos          | Serilog → consola + Seq                                  |
| HTTP                   | `HttpClient` vía DI factory                              |
| Config                 | `appsettings.json` + `IConfiguration`                    |
| DI                     | `Microsoft.Extensions.Hosting` (IHost)                   |

---

## Responsabilidades

### Agent.Worker (este proyecto)

| Componente                    | Responsabilidad                                           |
|-------------------------------|-----------------------------------------------------------|
| `Program.cs`                  | Host, DI, Serilog, configuración de todos los servicios  |
| `MigrationMonitorWorker`      | BackgroundService: ciclo diario, espera horaria, eventos |
| `IAgentEventStore`            | Contrato para publicar/consumir eventos de negocio       |
| `AgentEvent` / `AgentEventTypes` | Modelo de evento y catálogo de tipos                  |
| `AgentEventDbContext`         | DbContext PostgreSQL para eventos consumibles            |
| `AgentEventStoreService`      | Implementación: publish, get-pending, mark-consumed      |

### Proyectos del console (reutilizados sin modificar)

| Proyecto                    | Qué aporta                                               |
|-----------------------------|----------------------------------------------------------|
| `Agent.Core`                | Interfaces, modelos, MigrationMonitorAgent, prompts      |
| `Agent.Tools`               | MigrationApiClient, validación, SQLite (AgentDbContext)  |
| `Agent.Infrastructure`      | OllamaLLMService, LLMServiceMock                         |

---

## Agent Loop

```
┌─────────────────────────────────────────────────────────────────┐
│                  WORKER LOOP (BackgroundService)                 │
│                                                                 │
│  INICIALIZAR                                                    │
│    └─► SQLite (AgentDbContext del console)                       │
│    └─► PostgreSQL (AgentEventDbContext del Worker)               │
│                                                                 │
│  CICLO DIARIO (se repite indefinidamente)                        │
│    │                                                            │
│    ├─► Publicar evento: SesionIniciada                          │
│    │                                                            │
│    ├─► [DELEGAR] agent.ExecuteAsync()                           │
│    │     El agente del console ejecuta internamente:            │
│    │     ├── percibir: GET endpoint GK/SAP                      │
│    │     ├── razonar: validar + recuperar historial + LLM       │
│    │     ├── decidir: REINTENTAR o FINALIZAR                    │
│    │     ├── actuar: enviar notificación                        │
│    │     └── recordar: guardar log en SQLite                    │
│    │                                                            │
│    ├─► [OBSERVAR] Leer resultado desde SQLite                   │
│    │     └─► Derivar y publicar eventos consumibles:            │
│    │           - DiscrepanciaDetectada (si hubo errores)        │
│    │           - MigracionOk (si todo OK)                       │
│    │           - LimiteIntentosAlcanzado (si se agotaron)       │
│    │           - NotificacionEnviada                            │
│    │           - SesionFinalizada                               │
│    │                                                            │
│    └─► Esperar hasta HoraInicio del día siguiente              │
│                                                                 │
└─────────────────────────────────────────────────────────────────┘
```

---

## Memoria Operacional (SQLite)

**Origen:** `AgentDbContext` del proyecto console. Este Worker lo configura con `UseSqlite()` en lugar del SQL Server LocalDB que usa el console.

**Connection String:** `Data Source=agent_memory.db`

**Tabla:** `MigrationMonitorLogs`

| Columna         | Tipo          | Descripción                          |
|-----------------|---------------|--------------------------------------|
| Id              | INTEGER PK    | Auto-incremental                     |
| SessionId       | TEXT(32)      | GUID de 8 chars de la sesión         |
| Timestamp       | DATETIME      | UTC                                  |
| Iteracion       | INTEGER       | Número de intento (1..N)             |
| TotalTiendas    | INTEGER       | Tiendas comparadas                   |
| CantidadErrores | INTEGER       | Tiendas con diferencia > tolerancia  |
| Decision        | TEXT(50)      | "Reintentar" o "Finalizar"           |
| Mensaje         | TEXT(1000)    | Razonamiento del LLM (truncado)      |
| DiferenciaTotal | DECIMAL(18,2) | Suma total de diferencias            |

---

## Eventos Consumibles (PostgreSQL)

**Origen:** `AgentEventDbContext` propio de este Worker. Permite a sistemas externos consumir eventos de negocio del agente.

**Connection String:** `Host=localhost;Port=5432;Database=agent_events;Username=postgres;Password=postgres`

**Tabla:** `AgentEvents`

| Columna    | Tipo      | Descripción                               |
|------------|-----------|-------------------------------------------|
| Id         | SERIAL PK | Auto-incremental                          |
| SessionId  | TEXT      | GUID de la sesión                         |
| EventType  | TEXT      | Tipo de evento (ver catálogo)             |
| Payload    | TEXT      | JSON con datos del evento                 |
| Timestamp  | TIMESTAMP | UTC                                       |
| Consumed   | BOOLEAN   | Si fue procesado por un consumidor        |
| ConsumedAt | TIMESTAMP | Cuándo fue consumido (nullable)           |

**Catálogo de eventos:**

| EventType                  | Cuándo se publica                                     |
|----------------------------|-------------------------------------------------------|
| `SesionIniciada`           | Al inicio de cada ciclo diario                        |
| `DiscrepanciaDetectada`    | Si hubo iteraciones con errores en la sesión          |
| `MigracionOk`              | Si la sesión finalizó sin discrepancias               |
| `LimiteIntentosAlcanzado`  | Si se agotaron los intentos sin resolver              |
| `NotificacionEnviada`      | Al finalizar cada sesión (siempre)                    |
| `SesionFinalizada`         | Resumen final de la sesión                            |
| `ErrorCiclo`               | Si ocurre un error inesperado en el ciclo del Worker  |

---

## Observabilidad — Seq

**Propósito:** Únicamente logs técnicos. No se usa como memoria ni como event bus.

**Qué se registra:**
- Arranque y detención del Worker
- Inicio/fin de cada ciclo diario
- Inicialización de bases de datos
- Errores y excepciones con stack trace
- Advertencias de disponibilidad (PostgreSQL, Ollama)
- Tiempos y métricas de ejecución

**Propiedades enriquecidas:**
- `Application`: `MonitorMigracionGkSap-Worker`
- `EnvironmentName`: nombre del entorno
- `MachineName`: nombre del host

---

## Uso de Ollama

El agente delega las decisiones al LLM local. Sin Ollama, usar `LLM:Provider = "Mock"`.

**Flujo del prompt (en proyecto console, sin modificar):**
1. `MigrationPromptBuilder` construye el prompt con análisis + historial
2. `OllamaLLMService` llama a `POST /api/generate`
3. `MigrationDecisionParser` extrae `ACCION: REINTENTAR|FINALIZAR` + mensaje

---

## Configuración Completa

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

---

## Reglas del Proyecto

1. **NO modificar el proyecto console** — solo referenciar sus .csproj
2. **Seq es solo para logs técnicos** — no para memoria ni eventos de negocio
3. **SQLite es la memoria operacional** — historial de iteraciones por sesión
4. **PostgreSQL es el event bus** — eventos consumibles por sistemas externos
5. **ModoSimulacionCorreo = true por defecto** — protección contra envíos accidentales
6. **El LLM decide** — la lógica REINTENTAR/FINALIZAR vive en el agente original

---

## Estructura de Carpetas

```
agent worker gk-sap\
├── AgentWorkerSolution.sln
├── AGENTS.md
├── README.md
└── Agent.Worker\
    ├── Agent.Worker.csproj       ← referencias a console via rutas relativas
    ├── Program.cs                ← host + DI + Serilog
    ├── appsettings.json
    ├── Interfaces\
    │   └── IAgentEventStore.cs
    ├── Models\
    │   └── AgentEvent.cs         ← modelo + catálogo de tipos
    ├── Data\
    │   └── AgentEventDbContext.cs ← PostgreSQL
    ├── Services\
    │   └── AgentEventStoreService.cs
    └── Workers\
        └── MigrationMonitorWorker.cs ← BackgroundService
```
