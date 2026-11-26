param (
    [switch]$NoArchive,
    [string]$OutputDirectory = $PSScriptRoot
)

$ErrorActionPreference = "Stop"
Set-Location -Path $PSScriptRoot

# UTF-8 OHNE BOM (manche Mod-Manager lesen info.json mit BOM nicht)
$Utf8NoBom = New-Object System.Text.UTF8Encoding($false)

function Read-TextNoBom([string]$path) {
    $text = [System.IO.File]::ReadAllText($path, $Utf8NoBom)
    return $text.TrimStart([char]0xFEFF)
}

function Write-TextNoBom([string]$path, [string]$text) {
    [System.IO.File]::WriteAllText($path, $text, $Utf8NoBom)
}

Write-Host ""
Write-Host ""
Write-Host ""
Write-Host "=== Packe die Mod ===" -ForegroundColor Cyan
Write-Host ""

# -------------------------------------------------
# Checks
# -------------------------------------------------
if (!(Test-Path "info.json")) { throw "info.json fehlt!" }
if (!(Test-Path "build"))     { throw "build/-Ordner fehlt!" }

$InfoPath = Join-Path $PSScriptRoot "info.json"

# -------------------------------------------------
# Mod-Infos
# -------------------------------------------------
$infoText   = Read-TextNoBom $InfoPath
$modInfo    = $infoText | ConvertFrom-Json
$modId      = $modInfo.Id
$modVersion = $modInfo.Version

# -------------------------------------------------
# Zielverzeichnisse
# -------------------------------------------------
$DistDir   = Join-Path $OutputDirectory "dist"
$BackupDir = Join-Path $OutputDirectory "backup"

New-Item -Path $DistDir   -ItemType Directory -Force | Out-Null
New-Item -Path $BackupDir -ItemType Directory -Force | Out-Null

# -------------------------------------------------
# Version: existiert das ZIP schon -> Hotfix hochzählen (x.xx.01, x.xx.02, ...)
# -------------------------------------------------
function Get-NextHotfixVersion([string]$version) {
    $parts = $version.Split('.')

    while ($parts.Count -lt 3) { $parts += "0" }

    $hotfix = 0
    [void][int]::TryParse($parts[2], [ref]$hotfix)
    $parts[2] = ($hotfix + 1).ToString("00")

    return ($parts[0..2] -join '.')
}

$originalVersion = $modVersion

while (Test-Path (Join-Path $DistDir "${modId}_v${modVersion}.zip")) {
    $modVersion = Get-NextHotfixVersion $modVersion
}

if ($modVersion -ne $originalVersion) {
    Write-Host "Version $originalVersion existiert bereits in dist -> neue Version $modVersion" -ForegroundColor Yellow

    # nur die Versionszeile ersetzen, Formatierung bleibt erhalten
    $pattern = '("Version"\s*:\s*")' + [regex]::Escape($originalVersion) + '(")'
    $infoText = [regex]::Replace($infoText, $pattern, ('${1}' + $modVersion + '${2}'), 1)
    Write-TextNoBom $InfoPath $infoText
}

Write-Host "Mod-ID:  $modId"
Write-Host "Version: $modVersion"

# -------------------------------------------------
# repository.json (UMM-Update-Check) auf dieselbe Version bringen
# -------------------------------------------------
$RepoPath = Join-Path $PSScriptRoot "repository.json"

if (Test-Path $RepoPath) {
    $repoText = Read-TextNoBom $RepoPath
    $repoNew  = [regex]::Replace($repoText, '("Version"\s*:\s*")[^"]*(")', ('${1}' + $modVersion + '${2}'))
    $repoNew  = [regex]::Replace($repoNew, '_v[0-9][0-9\.]*\.zip', ('_v' + $modVersion + '.zip'))

    if ($repoNew -ne $repoText) {
        Write-TextNoBom $RepoPath $repoNew
        Write-Host "repository.json -> $modVersion"
    }
}

# -------------------------------------------------
# TEMP Ordner (ORIGINALVERHALTEN)
# -------------------------------------------------
$TmpDir    = Join-Path $DistDir "tmp"
$ZipOutDir = Join-Path $TmpDir $modId

if (Test-Path $ZipOutDir) {
    Remove-Item $ZipOutDir -Recurse -Force
}
New-Item -Path $ZipOutDir -ItemType Directory -Force | Out-Null

# -------------------------------------------------
# RELEASE Dateien sammeln (EXAKT WIE IM ORIGINAL)
# -------------------------------------------------
$FilesToInclude = @(
    "build\*",
    "Audio",
    "Licenses",
    "LICENSE"
)

foreach ($item in $FilesToInclude) {
    Copy-Item -Path $item -Destination $ZipOutDir -Recurse -Force
}

# info.json immer ohne BOM ins Paket schreiben
Write-TextNoBom (Join-Path $ZipOutDir "info.json") (Read-TextNoBom $InfoPath)

# -------------------------------------------------
# ZIP erstellen (dist)
# -------------------------------------------------
if (-not $NoArchive) {

    $fileName = "${modId}_v${modVersion}.zip"
    $zipPath  = Join-Path $DistDir $fileName

    Compress-Archive `
        -Path (Join-Path $ZipOutDir "*") `
        -DestinationPath $zipPath `
        -CompressionLevel Fastest `
        -Force

    Write-Host "ZIP erstellt: dist\$fileName"

    # =================================================
    # SOURCE BACKUP – BIN/OBJ ABSOLUT AUSGESCHLOSSEN
    # =================================================
    $timestamp = Get-Date -Format "yyyy-MM-dd_HH-mm-ss"
    $sourceBackupName = "${modId}_v${modVersion}_$timestamp.zip"
    $sourceBackupPath = Join-Path $BackupDir $sourceBackupName

    $sourceTmp = Join-Path $TmpDir "source"
    if (Test-Path $sourceTmp) {
        Remove-Item $sourceTmp -Recurse -Force
    }
    New-Item -Path $sourceTmp -ItemType Directory -Force | Out-Null

    $sourceFiles = Get-ChildItem -Path $PSScriptRoot -Recurse -File |
        Where-Object {
            $rel = $_.FullName.Substring($PSScriptRoot.Length).TrimStart('\','/')

            # HARTE Ausschlüsse (egal wo im Pfad)
            if ($rel -match '(^|[\\/])bin([\\/]|$)') { return $false }
            if ($rel -match '(^|[\\/])obj([\\/]|$)') { return $false }
            if ($rel -match '(^|[\\/])build([\\/]|$)') { return $false }
            if ($rel -match '(^|[\\/])dist([\\/]|$)')  { return $false }
            if ($rel -match '(^|[\\/])backup([\\/]|$)'){ return $false }

            # NUR Quellformate
            return $_.Extension -in ".cs", ".csproj", ".txt", ".json", ".ps1"
        }

    foreach ($f in $sourceFiles) {
        Copy-Item $f.FullName (Join-Path $sourceTmp $f.Name) -Force
    }

    Compress-Archive `
        -Path (Join-Path $sourceTmp "*") `
        -DestinationPath $sourceBackupPath `
        -CompressionLevel Optimal `
        -Force

    Remove-Item $sourceTmp -Recurse -Force

    Write-Host "Source-Backup erstellt: backup\$sourceBackupName"
}
else {
    Write-Host "Archivieren übersprungen (-NoArchive)"
}

Write-Host ""
Write-Host "=== FERTIG ===" -ForegroundColor Green
Write-Host ""
Write-Host ""
Write-Host ""
