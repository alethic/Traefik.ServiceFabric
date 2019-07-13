
powershell .\traefik.ps1 -ConfigTemplate .\traefik.toml -TraefikPort %Fabric_Endpoint_TraefikEndpoint% -TraefikApiPort %Fabric_Endpoint_TraefikApiEndpoint% -ClusterManagementUrl %ClusterManagementUrl%
