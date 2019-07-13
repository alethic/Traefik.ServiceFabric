param(

    [Parameter(Mandatory)]
    [string]$ConfigTemplate,

    [Parameter(Mandatory)]
    [string]$ClusterManagementUrl

)

$cfg = gc $ConfigTemplate
$cfg = $cfg.Replace('%ClusterManagementUrl%', $ClusterManagementUrl)
[IO.File]::WriteAllLines("$env:Fabric_Folder_App_Work\traefik.toml", $cfg)

& .\traefik.exe --configfile=$env:Fabric_Folder_App_Work\traefik.toml
