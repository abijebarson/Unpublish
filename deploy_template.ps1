$ErrorActionPreference = "Stop"

Write-Host "Building Unpublish plugin..."
dotnet publish -c Release -o ./publish

$dllPath = "./publish/Unpublish.dll"
if (-not (Test-Path $dllPath)) {
    Write-Error "Build failed or DLL not found at $dllPath"
    exit 1
}

$remoteUser = "user"
$remoteHost = "host"
$remotePluginDir = "/var/lib/jellyfin/plugins/Unpublish"

Write-Host "Creating plugin directory on remote server..."
ssh -t "${remoteUser}@${remoteHost}" "sudo mkdir -p $remotePluginDir && sudo chown -R ${remoteUser}:${remoteUser} $remotePluginDir"

Write-Host "Copying DLL to remote server..."
scp $dllPath "${remoteUser}@${remoteHost}:${remotePluginDir}/Unpublish.dll"

Write-Host "Setting permissions and restarting Jellyfin service..."
ssh -t "${remoteUser}@${remoteHost}" "sudo chown -R jellyfin:jellyfin $remotePluginDir && sudo systemctl restart jellyfin"

Write-Host "Deployment complete!"
