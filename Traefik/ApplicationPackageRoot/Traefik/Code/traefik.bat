
powershell .\traefik.ps1 -Template .\traefik.toml
IF %ERRORLEVEL% NEQ 0 (
  EXIT /B %ERRORLEVEL%
)
