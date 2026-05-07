# AGENTS.md — Monitor de Migración GK → SAP (Worker Service)

## Objetivo

Agente autónomo de monitoreo que verifica la correcta migración de datos de ventas y devoluciones desde **GK** (punto de venta) hacia **SAP** (ERP).

El agente es **verdaderamente autónomo**: el LLM controla el flujo de ejecución mediante tool calling. No es un chatbot ni un wrapper — el LLM decide qué herramienta invocar en cada paso, el runtime la ejecuta, y el LLM decide el siguiente paso basándose en el resultado.

Implementa las 5 primitivas del artículo [Agentes IA Programadores](https://www.webreactiva.com/blog/agentes-ia-programadores):

| Primitiva | Implementación |
|-----------|---------------|
| LLM | `IAgentLLMService` → Groq o Claude |
| Contexto / Instrucciones | `AgentPromptBuilder` + `MigrationPolicy` |
| Herramientas | 7 implementaciones de `IAgentTool` |
| Estado / Memoria | `AgentContext` + SQLite (vía herramientas) |
| Bucle del agente | `MigrationAgent.ExecuteAsync()` |

---

## Arquitectura

```
C:\Projects\agents\
│
├── control de monitoreo gk-sap console\    ← proyecto original (NO MODIFICADO)
│   ├── Agent.Core\        interfaces, modelos, config
│   ├── Agent.Tools\       API client, validación, SQLite, notificaciones
│   └── Agent.Infrastructure\ (no usado en este proyecto)
│
└── agent worker gk-sap\                    ← ESTE PROYECTO
    ├── Agent.Worker\
    │   ├── Agents\        MigrationAgent — bucle agente verdadero
    │   ├── Interfaces\    IAgentLLMService, IAgentTool, IAgentEventStore
    │   ├── Models\        ChatMessage, ToolCall, AgentContext, AgentEvent...
    │   ├── Policy\        MigrationPolicy — reglas de negocio como concepto
    │   ├── Prompts\       AgentPromptBuilder — construye el system prompt
    │   ├── Tools\         7 herramientas invocables por el LLM
    │   ├── Services\      GroqAgentLLMService, ClaudeAgentLLMService, AgentEventStoreService
    │   ├── Data\          AgentEventDbContext (PostgreSQL / SQLite fallback)
    │   ├── Workers\       MigrationMonitorWorker (BackgroundService)
    │   ├── Program.cs
    │   ├── appsettings.json          ← no se sube a git (tiene API keys)
    │   └── appsettings.example.json  ← plantilla sin keys (se sube a git)
    ├── AgentWorkerSolution.sln
    ├── AGENTS.md
    └── README.md
```

---

## Stack Tecnológico

| Capa | Tecnología |
|------|-----------|
| Runtime | .NET 9 / Worker Service (`Microsoft.NET.Sdk.Worker`) |
| LLM primario | Claude API (Anthropic) — si `ClaudeApiKey` está configurada |
| LLM secundario | Groq API (llama-3.1-8b-instant) — si solo `GroqApiKey` está configurada |
| Selección LLM | Automática por keys presentes: Claude > Groq |
| Memoria operacional | SQLite vía EF Core (`AgentDbContext` del proyecto console) |
| Eventos consumibles | PostgreSQL vía EF Core + Npgsql (SQLite como fallback) |
| Logs técnicos | Serilog → consola + Seq |

---

## Bucle del Agente (cómo funciona)

```
MigrationMonitorWorker
    └─► MigrationAgent.ExecuteAsync()
           │
           ├─ [1] System prompt: política + herramientas disponibles
           ├─ [2] User message: "Inicia monitoreo para fecha X"
           │
           └─ BUCLE (el LLM controla el flujo):
                │
                ├─ LLM recibe mensajes + definiciones de herramientas
                ├─ LLM responde con tool_calls (nunca texto libre)
                ├─ Runtime ejecuta cada herramienta
                ├─ Runtime agrega resultado al historial de mensajes
                ├─ Si esperar_y_reintentar → Task.Delay + mensaje de contexto
                ├─ Si finalizar → sale del bucle
                └─ Repite hasta finalizar o límite de seguridad (50 turnos)
```

**Percibir → Razonar → Decidir → Actuar → Recordar → Iterar**

---

## Herramientas (Tools)

El LLM puede invocar estas 7 herramientas. Cada una implementa `IAgentTool`.

| Herramienta | Acción real | Resultado |
|-------------|-------------|-----------|
| `obtener_estado_migracion` | HTTP GET a la API GK/SAP + análisis | JSON con errores y diferencias |
| `registrar_iteracion` | INSERT en SQLite | Confirmación |
| `obtener_historial_sesion` | SELECT en SQLite | Lista de iteraciones anteriores |
| `enviar_notificacion` | Llama `INotificationService` | Correo real o simulado |
| `publicar_evento` | INSERT en PostgreSQL/SQLite | Confirmación |
| `esperar_y_reintentar` | Señal al runtime para `Task.Delay` | Mensaje de espera |
| `finalizar` | Señal al runtime para salir del bucle | Mensaje final |

---

## Política (`MigrationPolicy`)

Las reglas de negocio son un concepto de primera clase, no lógica hardcodeada.
Se embeben en el system prompt para que el LLM las aplique:

- **REINTENTAR**: `CantidadErrores > 0` AND `iteración < MaximoIntentos`
- **FINALIZAR**: `CantidadErrores == 0` OR `iteración >= MaximoIntentos`
- Flujo obligatorio: obtener estado → registrar → (esperar si errores) → notificar → publicar evento → finalizar

---

## Selección Automática de LLM

```csharp
// En Program.cs — prioridad por keys configuradas:
ClaudeApiKey presente  →  ClaudeAgentLLMService  (claude-haiku-4-5-20251001)
GroqApiKey presente    →  GroqAgentLLMService    (llama-3.1-8b-instant)
Ninguna key            →  InvalidOperationException (configuración requerida)
```

---

## Memoria Operacional (SQLite)

**Archivo:** `agent_memory.db` (generado en el directorio de ejecución)
**Tabla:** `MigrationMonitorLogs`

| Columna | Tipo | Descripción |
|---------|------|-------------|
| SessionId | TEXT | ID de la sesión (8 chars) |
| Iteracion | INTEGER | Número de intento |
| CantidadErrores | INTEGER | Tiendas con discrepancia |
| Decision | TEXT | `REINTENTAR` o `FINALIZAR` |
| Mensaje | TEXT | Razonamiento del LLM |
| DiferenciaTotal | DECIMAL | Suma de diferencias monetarias |
| Timestamp | DATETIME | UTC |

---

## Eventos Consumibles (PostgreSQL / SQLite fallback)

**Archivo fallback:** `agent_events_fallback.db`
**Tabla:** `AgentEvents`

| EventType | Cuándo |
|-----------|--------|
| `SesionIniciada` | Al inicio del ciclo |
| `MigracionOk` | Finaliza sin errores |
| `LimiteIntentosAlcanzado` | Se agotaron los intentos |
| `DiscrepanciaDetectada` | Se detectaron errores en alguna iteración |
| `NotificacionEnviada` | Al enviar notificación |
| `SesionFinalizada` | Resumen final |
| `ErrorCiclo` | Error inesperado en el Worker |

---

## Configuración (`appsettings.json`)

```json
{
  "ConnectionStrings": {
    "AgentDB":     "Data Source=agent_memory.db",
    "AgentEvents": ""
  },
  "LLM": {
    "ClaudeApiKey":  "",
    "ClaudeModel":   "claude-haiku-4-5-20251001",
    "GroqApiKey":    "",
    "GroqModel":     "llama-3.1-8b-instant"
  },
  "MigrationMonitor": {
    "IntervaloMinutos":     5,
    "HoraInicio":           "22:55",
    "HoraFin":              "00:10",
    "MaximoIntentos":       15,
    "Tolerancia":           0.01,
    "ModoSimulacionCorreo": true,
    "ToAddress":            "correo@empresa.com",
    "EndpointUrl":          "http://servidor/api/endpoint",
    "FechaMonitoreo":       ""
  },
  "Seq": { "Url": "http://localhost:5341" }
}
```

---

## Reglas del Proyecto

1. **NO modificar el proyecto console** — solo referenciar sus `.csproj`
2. **El LLM controla el flujo** — no hay lógica REINTENTAR/FINALIZAR hardcodeada en C#
3. **`appsettings.json` no se sube a git** — usar `appsettings.example.json` como plantilla
4. **Seq es solo para logs técnicos** — no para memoria ni eventos de negocio
5. **SQLite es la memoria operacional** — historial de iteraciones por sesión
6. **PostgreSQL (o SQLite fallback) es el event bus** — eventos consumibles por otros sistemas
7. **`ModoSimulacionCorreo = true` por defecto** — protección contra envíos accidentales
