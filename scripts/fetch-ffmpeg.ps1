# Baixa as DLLs do FFmpeg 7.1 (build LGPL compartilhada do BtbN) para third_party/ffmpeg/bin.
# Usado pelo CI e por quem compila localmente. A versão precisa bater com o pacote FFmpeg.AutoGen 7.1.x.
#
# O release "latest" do BtbN só traz as versões mais novas do FFmpeg. As anteriores continuam nos
# auto-builds guardados (os últimos dias e o último build de cada mês), então a busca vai do release
# mais novo ao mais antigo até achar o pacote do padrão.
param(
    [string]$Destination = (Join-Path $PSScriptRoot "..\third_party\ffmpeg"),
    [string]$Pattern = 'ffmpeg-n7\.1.*-win64-lgpl-shared-7\.1\.zip$'
)
$ErrorActionPreference = "Stop"
$headers = @{ "User-Agent" = "ScreenFlow-build"; "Accept" = "application/vnd.github+json" }
if ($env:GITHUB_TOKEN) { $headers["Authorization"] = "Bearer $env:GITHUB_TOKEN" }

$releases = [System.Collections.Generic.List[object]]::new()
for ($page = 1; $page -le 5; $page++) {
    $uri = "https://api.github.com/repos/BtbN/FFmpeg-Builds/releases?per_page=100&page=$page"
    # Invoke-RestMethod devolve a lista JSON inteira como um objeto só; o pipe abre um release por item.
    $batch = @(Invoke-RestMethod -Headers $headers -Uri $uri -MaximumRetryCount 3 -RetryIntervalSec 5 | ForEach-Object { $_ })
    foreach ($item in $batch) { $releases.Add($item) }
    if ($batch.Count -lt 100) { break }
}
$sorted = @($releases | Sort-Object -Property published_at -Descending)

$release = $null
$asset = $null
foreach ($candidate in $sorted) {
    $found = $candidate.assets | Where-Object { $_.name -match $Pattern } | Select-Object -First 1
    if ($found) { $release = $candidate; $asset = $found; break }
}
if (-not $asset) {
    $newest = $sorted | Select-Object -First 1
    $available = @($newest.assets | Where-Object { $_.name -match 'win64-lgpl-shared' } | ForEach-Object { $_.name })
    throw "Nenhum pacote do FFmpeg encontrado com o padrão $Pattern em $($releases.Count) releases do BtbN. Pacotes LGPL no mais novo ($($newest.tag_name)): $($available -join ', ')"
}

$zip = Join-Path ([IO.Path]::GetTempPath()) $asset.name
Write-Host "Baixando $($asset.name) do release $($release.tag_name)..."
Invoke-WebRequest -Headers $headers -Uri $asset.browser_download_url -OutFile $zip -MaximumRetryCount 3 -RetryIntervalSec 5
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
