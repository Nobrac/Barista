<#
.SYNOPSIS
    Builds the payload a release of Barista carries inside itself.

.DESCRIPTION
    A release of Barista is one exe that brings everything along: the target
    machine needs no git, no .NET, no C++ compiler and no network. That only
    works if the Doppio and Ristretto binaries are compiled somewhere else and
    put inside the exe, and this is the script that does it - in CI, from the
    commits pinned in payload.lock.json.

    It writes two files into payload\out:

        payload.zip              Doppio\ and Ristretto\, exactly the files the
                                 wizard checks for, nothing else
        payload-manifest.json    the commit each was built from, and the
                                 SHA-256 and size of every file in the zip

    Barista.csproj embeds both when they exist. The wizard unpacks the zip into
    a folder only administrators can change, reads every file back, and shows
    the comparison against the manifest before it lets anything run.

    The checkouts must be at the pinned commits. The script refuses otherwise:
    a payload built from whatever happened to be checked out is exactly the
    unpinned, unchecked source this exists to replace.

.EXAMPLE
    # In CI, after checking out both repositories at their pins:
    pwsh tools/build-payload.ps1 -DoppioDir Doppio -RistrettoDir Ristretto -Version 0.2.0

.EXAMPLE
    # Locally, from an x64 Native Tools prompt or with Build Tools installed:
    powershell -ExecutionPolicy Bypass -File tools\build-payload.ps1 -Version 0.2.0-dev

.NOTES
    The file lists below mirror Preflight.cs. PackagingTests asserts that
    they cover every file Preflight names, so they cannot drift apart quietly.
#>
[CmdletBinding()]
param(
    # The Barista working copy. Defaults to the folder this script lives in.
    [string] $Root = (Split-Path -Parent $PSScriptRoot),

    [string] $DoppioDir,
    [string] $RistrettoDir,

    [string] $LockFile,
    [string] $OutDir,

    [Parameter(Mandatory = $true)]
    [string] $Version,

    # The binaries are already built in the two folders. Still checks the pins.
    [switch] $SkipBuild
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 2.0

if (-not $DoppioDir)    { $DoppioDir    = Join-Path $Root 'Doppio' }
if (-not $RistrettoDir) { $RistrettoDir = Join-Path $Root 'Ristretto' }
if (-not $LockFile)     { $LockFile     = Join-Path $Root 'payload.lock.json' }
if (-not $OutDir)       { $OutDir       = Join-Path $Root 'payload\out' }

$DoppioDir    = (Resolve-Path -LiteralPath $DoppioDir).Path
$RistrettoDir = (Resolve-Path -LiteralPath $RistrettoDir).Path

# --- what goes in -------------------------------------------------------------
# Mirrors Preflight.cs. A file missing here is a
# release whose own integrity page shows a red row on someone else's machine.

$DoppioFiles = @(
    # Preflight.DoppioCoreFiles
    'TacProvider.dll'
    'enroll.exe'
    'register.reg'
    'install-doppio.ps1'

    # DoppioFilterFiles
    'register-filter.reg'

    # DoppioRecoveryFiles
    'unregister.reg'

    # DoppioSubAuthFiles / DoppioSubAuthRecoveryFiles
    'TacSubAuth.dll'
    'install-subauth.ps1'
    'uninstall-subauth.ps1'

    # DoppioAuthPackageFiles / DoppioAuthPackageRecoveryFiles
    'TacAuthPackage.dll'
    'install-authpackage.ps1'
    'uninstall-authpackage.ps1'

    # DoppioDenyFiles
    'deny-noninteractive.ps1'
)

$RistrettoFiles = @(
    # Preflight.RistrettoFiles / RistrettoRecoveryFiles
    'RistrettoFilter.dll'
    'ristretto.exe'
    'install-ristretto.ps1'
    'uninstall-ristretto.ps1'
)

function Fail([string] $Message) {
    Write-Host ""
    Write-Host "  $Message" -ForegroundColor Red
    Write-Host ""
    exit 1
}

# --- 1. the pins ----------------------------------------------------------------

$lock = Get-Content -LiteralPath $LockFile -Raw | ConvertFrom-Json

function Assert-Pinned([string] $Dir, [string] $Want, [string] $Label) {
    if ($Want -notmatch '^[0-9a-f]{40}$') {
        Fail "$Label pin in $LockFile is not a full 40-character commit id: '$Want'."
    }
    $head = (& git -C $Dir rev-parse HEAD 2>$null)
    if ($LASTEXITCODE -ne 0 -or -not $head) {
        Fail "$Dir is not a git checkout. The payload is only built from a pinned commit."
    }
    $head = $head.Trim()
    if ($head -ne $Want) {
        Fail "$Label is at $head, but payload.lock.json pins $Want. Check out the pin first."
    }
    $dirty = (& git -C $Dir status --porcelain --untracked-files=no)
    if ($dirty) {
        Fail "$Label has local changes to tracked files. The payload must be the pinned commit as published."
    }
    Write-Host "  $Label  $head  (pinned)"
}

Write-Host ""
Write-Host "Building the Barista $Version payload"
Write-Host ""

Assert-Pinned $DoppioDir    $lock.doppio.commit    'Doppio'
Assert-Pinned $RistrettoDir $lock.ristretto.commit 'Ristretto'

# Ristretto compiles Doppio's store code from its submodule. The submodule
# commit is part of Ristretto's commit, so pinning Ristretto pins it too - as
# long as it is actually checked out at that commit and not somewhere else.
$sub = (& git -C $RistrettoDir submodule status --recursive)
if ($LASTEXITCODE -ne 0) { Fail "Could not read Ristretto's submodules." }
foreach ($line in @($sub)) {
    if ($line -match '^[-+U]') {
        Fail "Ristretto's submodule is not at the commit Ristretto records: '$line'. Run: git submodule update --init --recursive"
    }
}

# And that commit is the one payload.lock.json names, so the lock file alone
# says everything a release was built from - bootstrap.bat reads the same
# entry when it has to fetch the submodule as a zip.
$subPin = $lock.ristretto.doppioSubmodule.commit
$subHead = (& git -C (Join-Path (Join-Path $RistrettoDir 'extern') 'doppio') rev-parse HEAD 2>$null)
if (-not $subHead -or $subHead.Trim() -ne $subPin) {
    Fail "Ristretto's extern\doppio is at '$subHead', but payload.lock.json pins $subPin. Update the lock file together with the Ristretto pin."
}
Write-Host "  Ristretto extern\doppio  $subPin  (pinned)"

# --- 2. build ---------------------------------------------------------------------

function Invoke-MsvcBuild([string] $Dir, [string] $Script) {
    $vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
    if (-not (Test-Path -LiteralPath $vswhere)) { Fail "vswhere.exe not found - no Visual Studio or Build Tools here." }

    $vs = & $vswhere -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
    if (-not $vs) { Fail "No Visual Studio instance with the C++ x64 toolset." }
    $vcvars = Join-Path (@($vs)[0].Trim()) 'VC\Auxiliary\Build\vcvars64.bat'
    if (-not (Test-Path -LiteralPath $vcvars)) { Fail "vcvars64.bat not found under $vs." }

    # A generated .bat rather than one cmd /c line, for the reason BuildRunner
    # gives: vcvars64.bat sits under a path with spaces, and nested quoting
    # through PowerShell into cmd.exe differs between PowerShell versions.
    $bat = Join-Path $Root ("payload\build-" + [guid]::NewGuid().ToString('N') + '.bat')
    New-Item -ItemType Directory -Path (Split-Path -Parent $bat) -Force | Out-Null
    $lines = @(
        '@echo off'
        "call `"$vcvars`" >nul"
        'if errorlevel 1 exit /b 1'
        "cd /d `"$Dir`""
        'if errorlevel 1 exit /b 1'
        "call `"$Script`""
        'exit /b %errorlevel%'
    )
    [System.IO.File]::WriteAllLines($bat, $lines, (New-Object System.Text.ASCIIEncoding))

    $cmd = Join-Path $env:SystemRoot 'System32\cmd.exe'
    Write-Host ""
    Write-Host "=== $Script in $Dir"
    try {
        & $cmd /c $bat
        $code = $LASTEXITCODE
    } finally {
        Remove-Item -LiteralPath $bat -Force -ErrorAction SilentlyContinue
    }
    if ($code -ne 0) { Fail "$Script failed with exit code $code." }
}

if (-not $SkipBuild) {
    Invoke-MsvcBuild $DoppioDir    'build.bat'
    Invoke-MsvcBuild $RistrettoDir 'build-ristretto.bat'
}

# --- 3. stage ---------------------------------------------------------------------

$stage = Join-Path (Split-Path -Parent $OutDir) 'stage'
if (Test-Path -LiteralPath $stage)  { Remove-Item -LiteralPath $stage -Recurse -Force }
if (Test-Path -LiteralPath $OutDir) { Remove-Item -LiteralPath $OutDir -Recurse -Force }
New-Item -ItemType Directory -Path $stage, $OutDir -Force | Out-Null

$entries = New-Object System.Collections.Generic.List[object]

function Add-Set([string] $From, [string] $Component, [string[]] $Files) {
    $missing = @($Files | Where-Object { -not (Test-Path -LiteralPath (Join-Path $From $_)) })
    if ($missing.Count -gt 0) {
        Fail ("$Component is missing " + ($missing -join ', ') + " in $From. Did the build run?")
    }

    $to = Join-Path $stage $Component
    New-Item -ItemType Directory -Path $to -Force | Out-Null
    foreach ($f in $Files) {
        $dst = Join-Path $to $f
        Copy-Item -LiteralPath (Join-Path $From $f) -Destination $dst -Force
        $entries.Add([pscustomobject]@{
            path   = "$Component/$f"
            sha256 = (Get-FileHash -LiteralPath $dst -Algorithm SHA256).Hash.ToLowerInvariant()
            size   = (Get-Item -LiteralPath $dst).Length
            local  = $dst
        })
    }
}

Add-Set $DoppioDir    'Doppio'    $DoppioFiles
Add-Set $RistrettoDir 'Ristretto' $RistrettoFiles

# --- 4. manifest ------------------------------------------------------------------

$manifest = [ordered]@{
    schemaVersion = 1
    version       = $Version
    builtUtc      = (Get-Date).ToUniversalTime().ToString('yyyy-MM-ddTHH:mm:ssZ')
    sources       = @(
        [ordered]@{ name = 'Doppio';    repository = $lock.doppio.repository;    commit = $lock.doppio.commit }
        [ordered]@{ name = 'Ristretto'; repository = $lock.ristretto.repository; commit = $lock.ristretto.commit }
    )
    files         = @($entries | ForEach-Object {
        [ordered]@{ path = $_.path; sha256 = $_.sha256; size = $_.size }
    })
}

$manifestPath = Join-Path $OutDir 'payload-manifest.json'
$json = $manifest | ConvertTo-Json -Depth 5
# UTF-8 without a BOM. The wizard hashes these exact bytes into the folder
# name, so the file is written once, here, and never re-encoded.
[System.IO.File]::WriteAllText($manifestPath, $json, (New-Object System.Text.UTF8Encoding($false)))

# --- 5. zip -----------------------------------------------------------------------
# Built entry by entry rather than with Compress-Archive: Windows PowerShell's
# Compress-Archive writes backslashes into entry names, and the wizard only
# accepts the "Doppio/name" form its manifest uses.

Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem

$zipPath = Join-Path $OutDir 'payload.zip'
$zip = [System.IO.Compression.ZipFile]::Open($zipPath, [System.IO.Compression.ZipArchiveMode]::Create)
try {
    foreach ($e in $entries) {
        [System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile(
            $zip, $e.local, $e.path, [System.IO.Compression.CompressionLevel]::Optimal) | Out-Null
    }
} finally {
    $zip.Dispose()
}

Remove-Item -LiteralPath $stage -Recurse -Force

# --- 6. report --------------------------------------------------------------------

Write-Host ""
foreach ($e in $entries) { Write-Host ("  {0}  {1}" -f $e.sha256, $e.path) }
Write-Host ""
Write-Host "  -> $zipPath  $([Math]::Round((Get-Item -LiteralPath $zipPath).Length / 1KB)) KB"
Write-Host "  -> $manifestPath"
Write-Host ""
Write-Host "Now build Barista with -p:RequirePayload=true; the payload is embedded from there."
Write-Host ""
