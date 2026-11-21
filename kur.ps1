param(
    [string]$ModsPath = "C:\Program Files (x86)\Steam\steamapps\common\Stardew Valley\Mods"
)

$SourceMods = Join-Path $PSScriptRoot "Mods"
if (-not (Test-Path $ModsPath)) {
    Write-Error "Stardew Valley Mods klasoru bulunamadi: $ModsPath"
    exit 1
}

Copy-Item -Path "$SourceMods\*" -Destination $ModsPath -Recurse -Force
Write-Host "Tum Turkce ceviriler basariyla yuklendi -> $ModsPath" -ForegroundColor Green
