[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$PackageRoot,

    [Parameter(Mandatory = $true)]
    [string]$DestinationRoot,

    [Parameter(Mandatory = $true)]
    [string]$GitHubPathFile
)

$ErrorActionPreference = 'Stop'

$codexPackage = Join-Path -Path ([System.IO.Path]::GetFullPath($PackageRoot)) -ChildPath '@openai\codex'
if (-not (Test-Path -LiteralPath $codexPackage -PathType Container)) {
    throw 'CODEX_NATIVE_PACKAGE_NOT_FOUND'
}

$nativePackage = Join-Path -Path ([System.IO.Path]::GetFullPath($PackageRoot)) -ChildPath '@openai\codex-win32-x64'
$searchRoots = @($codexPackage)
if (Test-Path -LiteralPath $nativePackage -PathType Container) {
    $searchRoots += $nativePackage
}

$candidates = @($searchRoots |
    ForEach-Object { Get-ChildItem -LiteralPath $_ -Filter 'codex.exe' -File -Recurse } |
    Sort-Object -Property FullName -Unique)
if ($candidates.Count -ne 1) {
    throw "CODEX_NATIVE_EXECUTABLE_AMBIGUOUS:count=$($candidates.Count)"
}

$destination = [System.IO.Path]::GetFullPath($DestinationRoot)
[System.IO.Directory]::CreateDirectory($destination) | Out-Null
$target = Join-Path -Path $destination -ChildPath 'codex.exe'
Copy-Item -LiteralPath $candidates[0].FullName -Destination $target -Force

$version = (& $target --version 2>&1 | Out-String).Trim()
if ($LASTEXITCODE -ne 0 -or $version -notmatch '^codex-cli [0-9]') {
    throw 'CODEX_NATIVE_EXECUTABLE_PROBE_FAILED'
}

$pathFile = [System.IO.Path]::GetFullPath($GitHubPathFile)
$existing = if (Test-Path -LiteralPath $pathFile -PathType Leaf) {
    [System.IO.File]::ReadAllText($pathFile, [System.Text.Encoding]::UTF8)
} else {
    ''
}
[System.IO.File]::WriteAllText(
    $pathFile,
    $existing + $destination + [System.Environment]::NewLine,
    [System.Text.UTF8Encoding]::new($false))

[pscustomobject]@{
    schema = 'hiker-codex-native-shim/v1'
    status = 'PASS'
    executable = $target
    version = $version
} | ConvertTo-Json -Compress
