# Monitor de Migración GK → SAP — Worker Service

Agente autónomo que monitorea la correcta migración de datos de ventas y devoluciones desde GK (punto de venta) hacia SAP (ERP). El LLM controla el flujo de ejecución mediante tool calling — no hay lógica REINTENTAR/FINALIZAR hardcodeada en C#.

---

## Requisitos

- .NET 9 SDK
- API key de Claude o Groq (ver Configuración)
- Seq (opcional, para visualizar logs)

---

## Configuración

Copiar `Agent.Worker/appsettings.example.json` a `Agent.Worker/appsettings.json` y completar:

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

| Parámetro              | Descripción                                                        |
|------------------------|--------------------------------------------------------------------|
| `AgentDB`              | SQLite — memoria operacional (historial de iteraciones)            |
| `AgentEvents`          | PostgreSQL — eventos consumibles. Vacío = SQLite fallback          |
| `ClaudeApiKey`         | API key de Anthropic. Si está presente, se usa Claude              |
| `GroqApiKey`           | API key de Groq. Se usa si no hay ClaudeApiKey                     |
| `IntervaloMinutos`     | Espera entre reintentos del agente                                 |
| `HoraInicio`/`HoraFin` | Ventana de monitoreo diaria (soporta cruce de medianoche)         |
| `MaximoIntentos`       | Límite de iteraciones antes de forzar finalización                 |
| `Tolerancia`           | Diferencia máxima aceptable por tienda (decimal)                   |
| `ModoSimulacionCorreo` | `true` = no envía correo real, solo loguea                        |
| `FechaMonitoreo`       | Fecha a verificar (`yyyy-MM-dd`). Vacío = hoy                     |

**Selección automática de LLM:** si `ClaudeApiKey` está configurada se usa Claude; si no, Groq. Si ninguna está configurada, el Worker no arranca.

---

## Ejecutar en desarrollo

```powershell
cd "C:\Projects\agents\agent worker gk-sap"
dotnet run --project Agent.Worker
```

El agente ejecuta el ciclo inmediatamente al arrancar (sin esperar `HoraInicio`), útil para pruebas.

---

## Instalar como servicio de Windows

### 1. Publicar

```powershell
dotnet publish "C:\Projects\agents\agent worker gk-sap\Agent.Worker" -c Release -o "C:\Services\MonitorGkSap"
```

### 2. Copiar configuración

`appsettings.json` no se publica (está en `.gitignore`), copiarlo manualmente:

```powershell
Copy-Item "C:\Projects\agents\agent worker gk-sap\Agent.Worker\appsettings.json" "C:\Services\MonitorGkSap\"
```

### 3. Instalar el servicio (PowerShell como Administrador)

```powershell
sc.exe create MonitorGkSap binPath="C:\Services\MonitorGkSap\Agent.Worker.exe" start=auto
sc.exe description MonitorGkSap "Agente autónomo de monitoreo de migración GK→SAP"
```

### 4. Iniciar

```powershell
sc.exe start MonitorGkSap
```

### Comandos útiles

```powershell
sc.exe stop    MonitorGkSap          # detener
sc.exe start   MonitorGkSap          # iniciar
sc.exe delete  MonitorGkSap          # desinstalar (detener primero)
sc.exe query   MonitorGkSap          # ver estado
```

También se puede gestionar desde `services.msc` buscando "MonitorGkSap".

### Actualizar el servicio

```powershell
sc.exe stop MonitorGkSap
dotnet publish "C:\Projects\agents\agent worker gk-sap\Agent.Worker" -c Release -o "C:\Services\MonitorGkSap"
sc.exe start MonitorGkSap
```

---

## Seguimiento en tiempo real

### Consola / logs del servicio

Cada herramienta que invoca el LLM aparece en los logs:

```
[16:05:01 INF] [Agente] Iniciando sesión a1b2c3d4 para fecha 2026-05-07
[16:05:02 INF] [Agente] Ejecutando herramienta: obtener_estado_migracion
[16:05:03 INF] [Agente] Ejecutando herramienta: registrar_iteracion
[16:05:03 INF] [Agente] Ejecutando herramienta: esperar_y_reintentar
[16:05:03 INF] [Agente] Esperando 5.0 min antes de reintentar...
[16:10:04 INF] [Agente] Ejecutando herramienta: obtener_estado_migracion
[16:10:05 INF] [Agente] Ejecutando herramienta: enviar_notificacion
[16:10:05 INF] [Agente] Ejecutando herramienta: publicar_evento
[16:10:05 INF] [Agente] Ejecutando herramienta: finalizar
[16:10:05 INF] [Agente] Sesión a1b2c3d4 finalizada por herramienta.
```

Para ver logs de un servicio Windows en tiempo real:

```powershell
# Logs del Event Viewer de Windows
Get-EventLog -LogName Application -Source MonitorGkSap -Newest 50
```

### Seq (recomendado)

```powershell
# Con Docker
docker run --name seq -e ACCEPT_EULA=Y -p 5341:5341 -p 80:80 datalust/seq
```

Abrir `http://localhost` — permite filtrar por `SessionId`, nivel, herramienta, etc.

---

## Consultar memoria operacional (SQLite)

```bash
sqlite3 agent_memory.db
```

```sql
-- Sesiones recientes
SELECT SessionId, COUNT(*) iteraciones, MAX(Timestamp) ultima
FROM MigrationMonitorLogs
GROUP BY SessionId ORDER BY ultima DESC;

-- Detalle de una sesión
SELECT Iteracion, Timestamp, CantidadErrores, DiferenciaTotal, Decision
FROM MigrationMonitorLogs
WHERE SessionId = 'a1b2c3d4' ORDER BY Iteracion;
```

---

## Consultar eventos consumibles

Si PostgreSQL está configurado:

```sql
SELECT event_type, payload, timestamp
FROM "AgentEvents"
WHERE consumed = false ORDER BY timestamp;
```

Si no, los eventos están en `agent_events_fallback.db` (SQLite).

---

## Troubleshooting

**No arranca — "No hay LLM configurado":** Agregar `ClaudeApiKey` o `GroqApiKey` en `appsettings.json`.

**Groq devuelve 429:** Límite de tokens por minuto del plan gratuito alcanzado. El agente reintenta automáticamente hasta 3 veces esperando 45 segundos entre intentos.

**PostgreSQL no disponible:** El Worker usa `agent_events_fallback.db`. Log: `[WRN] PostgreSQL no configurado. Usando SQLite como fallback`.

**ModoSimulacionCorreo:** Siempre `true` por defecto. Cambiar a `false` solo en producción verificada.

**Compilar la solución:**

```powershell
dotnet build "C:\Projects\agents\agent worker gk-sap\AgentWorkerSolution.sln"
```

Ver [AGENTS.md](AGENTS.md) para documentación técnica completa de la arquitectura.
