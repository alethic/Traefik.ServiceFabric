param(

    [Parameter(Mandatory)]
    [string]$Template

)

$ErrorActionPreference = "Stop"

mkdir "$env:Fabric_Folder_App_Work\conf" -Force | Out-Null

$cfg = gc $Template
$cfg = $cfg.Replace('%TraefikPort%', $env:Fabric_Endpoint_TraefikEndpoint)
$cfg = $cfg.Replace('%TraefikApiPort%', $env:Fabric_Endpoint_TraefikApiEndpoint)
$cfg = $cfg.Replace('%TraefikConfDir%', "$env:Fabric_Folder_App_Work\conf".Replace('\', '\\'))
$cfg = $cfg.Replace('%ClusterManagementPort%', $env:Traefik_ClusterManagementPort)
$cfg = $cfg.Replace('%ProvidersThrottleDuration%', $env:Traefik_ProvidersThrottleDuration)
$cfg = $cfg.Replace('%GraceTimeout%', $env:Traefik_GraceTimeout)
$cfg = $cfg.Replace('%RequestAcceptGraceTimeout%', $env:Traefik_RequestAcceptGraceTimeout)
$cfg = $cfg.Replace('%MaxIdleConnsPerHost%', $env:Traefik_MaxIdleConnsPerHost)
$cfg = $cfg.Replace('%ReadTimeout%', $env:Traefik_ReadTimeout)
$cfg = $cfg.Replace('%WriteTimeout%', $env:Traefik_WriteTimeout)
$cfg = $cfg.Replace('%IdleTimeout%', $env:Traefik_IdleTimeout)
$cfg = $cfg.Replace('%DialTimeout%', $env:Traefik_DialTimeout)
$cfg = $cfg.Replace('%ResponseHeaderTimeout%', $env:Traefik_ResponseHeaderTimeout)
$cfg = $cfg.Replace('%IdleConnTimeout%', $env:Traefik_IdleConnTimeout)
[IO.File]::WriteAllLines("$env:Fabric_Folder_App_Work\traefik.toml", $cfg)

& .\traefik.exe --configfile=$env:Fabric_Folder_App_Work\traefik.toml
if ($LASTEXITCODE -ne 0) {
    exit $LASTEXITCODE
}
