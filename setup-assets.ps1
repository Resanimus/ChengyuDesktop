$ErrorActionPreference = 'Stop'
$cache = Join-Path $PSScriptRoot '.dependencies'
New-Item -ItemType Directory -Path $cache -Force | Out-Null

function Install-Asset($relativePath, $url, $archive, $entry, $sha256) {
    $destination = Join-Path $PSScriptRoot $relativePath
    if ((Test-Path -LiteralPath $destination) -and (Get-FileHash -LiteralPath $destination).Hash -eq $sha256) { return }
    $download = Join-Path $cache $archive
    Invoke-WebRequest -UseBasicParsing -Uri $url -OutFile $download
    tar.exe -xf $download -C $cache $entry
    if ($LASTEXITCODE -ne 0) { throw "Could not extract $entry from $archive" }
    $file = Join-Path $cache $entry
    if ((Get-FileHash -LiteralPath $file).Hash -ne $sha256) { throw "SHA-256 mismatch for $entry" }
    Copy-Item -LiteralPath $file -Destination $destination -Force
}

Install-Asset 'src/Assets/ScenePlayer/webwallgl.global.min.js' `
    'https://registry.npmjs.org/webwallgl/-/webwallgl-3.0.2.tgz' 'webwallgl-3.0.2.tgz' `
    'package/webwallgl.global.min.js' '50A386175600D21FCA0FEB588F03B4FD83D97942F1DC2E0B9E04A8904DDB2E53'
Install-Asset 'src/Assets/TaskbarGuard/runtime/AutoHotkey64.exe' `
    'https://github.com/AutoHotkey/AutoHotkey/releases/download/v2.0.29/AutoHotkey_2.0.29.zip' 'AutoHotkey_2.0.29.zip' `
    'AutoHotkey64.exe' '0876BC360F927F2A62CEA75B12CB51268F564EDC4DA65B569894AA1A328BFD68'
