<#
.SYNOPSIS
    Packs a built workshop into one folder somebody else can run.

.DESCRIPTION
    bootstrap.bat turns a bare machine into a workshop: it installs git, the
    .NET SDK and the MSVC toolset, fetches Doppio and Ristretto, and builds all
    three. That is the right thing for the machine doing the building and the
    wrong thing for every machine after it - nobody should need a C++ compiler
    to install something that is already compiled.

    So this takes a workshop that has been through bootstrap.bat once and
    writes out what the install actually needs:

        Barista-<version>\
            Barista.exe              self-contained, no .NET on the target
            READ-ME-FIRST.txt
            Doppio\                  the built DLLs, the .reg files, the scripts
            Ristretto\               the same, when it is included

    The wizard finds those two folders beside itself and never offers to build
    anything, because everything it checks for is already there.

    What it deliberately does NOT contain: build.bat, the C++ sources, the git
    history. A bundle that carries a build script invites someone to press it
    on a machine with no compiler, and the answer to "it says I need MSVC" is
    that they were never supposed to.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File tools\package-release.ps1

.NOTES
    The file lists below are the ones Preflight.cs checks for. They are
    repeated here because this is PowerShell and that is C#; a test in
    tests\Barista.Core.Tests asserts that this script covers every file those
    lists name, so the two cannot drift apart quietly.
#>
[CmdletBinding()]
param(
    # The Barista working copy. Defaults to the folder this script lives in.
    [string] $Root = (Split-Path -Parent $PSScriptRoot),

    [string] $DoppioDir,
    [string] $RistrettoDir,

    # Where bootstrap.bat published the wizard.
    [string] $WizardDir,

    [string] $OutDir,

    # Pack Doppio only. Ristretto is optional everywhere else too.
    [switch] $NoRistretto,

    [switch] $NoZip
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 1.0

if (-not $DoppioDir)    { $DoppioDir    = Join-Path $Root 'Doppio' }
if (-not $RistrettoDir) { $RistrettoDir = Join-Path $Root 'Ristretto' }
if (-not $WizardDir)    { $WizardDir    = Join-Path $Root 'wizard-out' }
if (-not $OutDir)       { $OutDir       = Join-Path $Root 'dist' }

# --- what goes in the bundle ------------------------------------------------
# Mirrors Preflight.cs. Each entry is a file the wizard will look for on the
# target machine, so a missing one is a bundle that fails its own preflight in
# front of somebody else.

$DoppioFiles = @(
    # Preflight.DoppioCoreFiles - needed for any install at all
    'TacProvider.dll'
    'enroll.exe'
    'register.reg'
    'install-doppio.ps1'

    # DoppioFilterFiles - makes the 2FA tile the only sign-in option
    'register-filter.reg'

    # DoppioRecoveryFiles - the only way back out of the credential provider
    'unregister.reg'

    # DoppioSubAuthFiles / DoppioSubAuthRecoveryFiles
    'TacSubAuth.dll'
    'install-subauth.ps1'
    'uninstall-subauth.ps1'

    # DoppioAuthPackageFiles / DoppioAuthPackageRecoveryFiles
    'TacAuthPackage.dll'
    'install-authpackage.ps1'
    'uninstall-authpackage.ps1'

    # DoppioDenyFiles - the supported way to close the non-interactive paths
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

function Copy-Set([string] $From, [string] $To, [string[]] $Files, [string] $Label) {
    if (-not (Test-Path -LiteralPath $From)) {
        Fail "No $Label folder at $From. Run bootstrap.bat first, or pass -${Label}Dir."
    }

    $missing = @($Files | Where-Object { -not (Test-Path -LiteralPath (Join-Path $From $_)) })
    if ($missing.Count -gt 0) {
        # Named rather than counted: "4 files missing" sends somebody looking
        # through a folder, and the names say immediately whether this is an
        # unbuilt checkout (DLLs missing) or an incomplete one (scripts missing).
        Fail ("$Label is not fully built. Missing from ${From}:`n      " +
              ($missing -join "`n      ") +
              "`n`n  Run bootstrap.bat in this folder first - packaging cannot compile anything.")
    }

    New-Item -ItemType Directory -Path $To -Force | Out-Null
    foreach ($file in $Files) {
        Copy-Item -LiteralPath (Join-Path $From $file) -Destination $To -Force
    }
    Write-Host "  $Label  $($Files.Count) files"
}

# --- the wizard -------------------------------------------------------------

$exe = Join-Path $WizardDir 'Barista.exe'
if (-not (Test-Path -LiteralPath $exe)) {
    $beside = Join-Path $Root 'Barista.exe'
    if (Test-Path -LiteralPath $beside) {
        $exe = $beside
    } else {
        Fail "No Barista.exe in $WizardDir or beside this repository. Run bootstrap.bat first."
    }
}

$version = (Get-Item -LiteralPath $exe).VersionInfo.FileVersion
if (-not $version) { $version = '0.0.0' }
$version = ($version -split '\+')[0]

$bundle = Join-Path $OutDir "Barista-$version"
if (Test-Path -LiteralPath $bundle) { Remove-Item -LiteralPath $bundle -Recurse -Force }
New-Item -ItemType Directory -Path $bundle -Force | Out-Null

Write-Host ""
Write-Host "Packing Barista $version"
Write-Host ""

Copy-Item -LiteralPath $exe -Destination $bundle -Force
Write-Host "  Barista.exe  $([Math]::Round((Get-Item -LiteralPath $exe).Length / 1MB, 1)) MB"

Copy-Set $DoppioDir (Join-Path $bundle 'Doppio') $DoppioFiles 'Doppio'

$withRistretto = $false
if (-not $NoRistretto) {
    if (Test-Path -LiteralPath (Join-Path $RistrettoDir 'install-ristretto.ps1')) {
        Copy-Set $RistrettoDir (Join-Path $bundle 'Ristretto') $RistrettoFiles 'Ristretto'
        $withRistretto = $true
    } else {
        # Not an error. Ristretto is optional in the wizard too, and a bundle
        # without it installs the credential provider perfectly well.
        Write-Host "  Ristretto  not here - packing Doppio only"
    }
}

# --- the note that travels with it -----------------------------------------
# Plain text, because it is read on a machine that may have nothing else on it
# yet, and in the state where the reader has just double-clicked something.

$readme = @"
Barista $version
================

What this is
------------
A setup wizard for Doppio (a 2FA tile on the Windows logon screen) and
Ristretto (a gate on network logons). Everything is already compiled: there
is nothing to build and no developer tools to install.

Before you start
----------------
READ THIS PART.

This installs code that runs inside lsass.exe, the Windows process that
handles signing in. A fault there is a blue screen on the logon path, and
that can leave a machine that does not boot.

Use a test VM you can throw away. Take a snapshot before you start, and
another before every restart the wizard asks for. A machine that cannot boot
is recovered by restoring a snapshot - there is no other quick way back.

Do not run this on a machine you need.

How to run it
-------------
1. Take the snapshot.
2. Right-click Barista.exe and choose "Run as administrator".
3. Follow the wizard. It finds the Doppio$(if ($withRistretto) { ' and Ristretto' }) folder next to
   itself, so leave them where they are.

It installs in stages, one change per restart, and asks after each one
whether the machine came back properly. If you answer no, it stops and offers
the matching uninstall. That is deliberate: at any failure there is exactly
one suspect.

What it needs from you
----------------------
- The snapshot. A VM cannot snapshot itself.
- An authenticator app on your phone, to scan the QR code during enrollment.
- One conscious decision about turning lsass protection (RunAsPPL) off. The
  wizard explains it and asks separately.

If something goes wrong
-----------------------
Restore the snapshot. If you cannot get in but the machine boots, the network
paths may still be open (SMB, WinRM, RDP) unless a later stage closed them.
Safe Mode also still works for the credential provider stages.

Source and the full story: https://github.com/Nobrac
"@

Set-Content -LiteralPath (Join-Path $bundle 'READ-ME-FIRST.txt') -Value $readme -Encoding UTF8

# --- zip --------------------------------------------------------------------

Write-Host ""
Write-Host "  -> $bundle"

if (-not $NoZip) {
    $zip = "$bundle.zip"
    if (Test-Path -LiteralPath $zip) { Remove-Item -LiteralPath $zip -Force }
    Compress-Archive -Path $bundle -DestinationPath $zip -CompressionLevel Optimal
    Write-Host "  -> $zip  $([Math]::Round((Get-Item -LiteralPath $zip).Length / 1MB, 1)) MB"
}

Write-Host ""
Write-Host "That folder is the whole thing. The machine it goes to needs no git,"
Write-Host "no .NET SDK and no C++ compiler - only administrator rights."
Write-Host ""
