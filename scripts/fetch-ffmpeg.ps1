# Baixa as DLLs do FFmpeg 7.1 (build LGPL compartilhada do BtbN) para third_party/ffmpeg/bin.
# Usado pelo CI e por quem compila localmente. A versão precisa bater com o pacote FFmpeg.AutoGen 7.1.x.
param(
    [string]$Destination = (Join-Path $PSScriptRoot "..\third_party\ffmpeg"),
    [string]$Pattern = 'ffmpeg-n7\.1.*-win64-lgpl-shared-7\.1\.zip$'
)
$ErrorActionPreference = "Stop"
$headers = @{ "User-Agent" = "ScreenFlow-build" }
if ($env:GITHUB_TOKEN) { $headers["Authorization"] = "Bearer $env:GITHUB_TOKEN" }

$release = Invoke-RestMethod -Headers $headers -Uri "https://api.github.com/repos/BtbN/FFmpeg-Builds/releases/latest"
$asset = $release.assets | Where-Object { $_.name -match $Pattern } | Select-Object -First 1
if (-not $asset) { throw "Nenhum pacote do FFmpeg encontrado com o padrão $Pattern no release $($release.tag_name)." }

$zip = Join-Path ([IO.Path]::GetTempPath()) $asset.name
Write-Host "Baixando $($asset.name)..."
Invoke-WebRequest -Headers $headers -Uri $asset.browser_download_url -OutFile $zip
$extract = Join-Path ([IO.Path]::GetTempPath()) "ffmpeg-extract"
if (Test-Path $extract) { Remove-Item $extract -Recurse -Force }
Expand-Archive $zip -DestinationPath $extract
$root = Get-ChildItem $extract -Directory | Select-Object -First 1

if (Test-Path $Destination) { Remove-Item $Destination -Recurse -Force }
New-Item -ItemType Directory -Force -Path (Join-Path $Destination "bin") | Out-Null
Copy-Item (Join-Path $root.FullName "bin\*.dll") (Join-Path $Destination "bin")
Copy-Item (Join-Path $root.FullName "LICENSE.txt") $Destination -ErrorAction SilentlyContinue
Set-Content (Join-Path $Destination "SOURCE.txt") "FFmpeg $($asset.name) de https://github.com/BtbN/FFmpeg-Builds ($($release.tag_name)), licença LGPL."
Get-ChildItem (Join-Path $Destination "bin") | ForEach-Object { Write-Host "  $($_.Name)" }
