$ErrorActionPreference = 'Stop'
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
Add-Type -AssemblyName System.IO.Compression.FileSystem
$root = Split-Path -Parent $PSScriptRoot
$packages = Join-Path $root 'vendor\packages'
New-Item -ItemType Directory -Path $packages -Force | Out-Null
$dependencies = @(
    @{ Id = 'Microsoft.Web.WebView2'; Version = '1.0.2420.47' },
    @{ Id = 'NvAPIWrapper.Net'; Version = '0.8.1.101' }
)
foreach ($dependency in $dependencies) {
    $id = $dependency.Id.ToLowerInvariant()
    $version = $dependency.Version
    $destination = Join-Path $packages ($dependency.Id + '.' + $version)
    if (Test-Path $destination) { throw "Package directory already exists: $destination. Use a fresh checkout for a clean restore." }
    $archive = Join-Path $packages "$id.$version.nupkg"
    $url = "https://api.nuget.org/v3-flatcontainer/$id/$version/$id.$version.nupkg"
    Write-Host "Restoring $($dependency.Id) $version from NuGet..."
    Invoke-WebRequest -Uri $url -OutFile $archive -UseBasicParsing
    [IO.Compression.ZipFile]::ExtractToDirectory($archive, $destination)
}
Write-Host 'Pinned dependencies restored. Run .\build_portable.ps1 next.'
