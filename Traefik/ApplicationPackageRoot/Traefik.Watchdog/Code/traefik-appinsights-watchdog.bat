
.\traefik-appinsights-watchdog.exe --appinsightskey=%AppInsightsKey% --traefikbackendname=%Fabric_ServiceName% --traefikhealthendpoint=http://localhost:%TraefikApiPort%/health --watchdogtestserverport=%Fabric_Endpoint_ServiceEndpoint% --watchdogtraefikurl=http://localhost:%TraefikPort%/Traefik/Watchdog --pollintervalsec=%PollIntervalSec% --debug=true
IF %ERRORLEVEL% NEQ 0 (
  EXIT /B %ERRORLEVEL%
)
