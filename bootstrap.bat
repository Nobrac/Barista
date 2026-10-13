@echo off
rem ===========================================================================
rem  Barista - one-click setup for Doppio and Ristretto
rem
rem  This sits at the root of the Barista repository. Clone it and run this;
rem  everything else is fetched and built beside it. It will:
rem
rem    1. elevate itself
rem    2. install git, the .NET 10 SDK and the MSVC build tools if missing
rem    3. fetch Doppio and Ristretto from github.com/Nobrac at the commits
rem       pinned in payload.lock.json - the same commits the standalone
rem       release is built from - as a clone where git can, a zip of that
rem       commit where it cannot
rem    4. pull the Doppio submodule Ristretto needs
rem    5. build all three projects
rem    6. launch the setup wizard
rem
rem  What it CANNOT do for you:
rem    - take the VM snapshot (a VM cannot snapshot itself; that is the host)
rem    - type the code off your phone
rem    - decide whether to turn RunAsPPL off
rem
rem  Flags:  /nodeps              do not install anything with winget
rem          /noclone             do not fetch; use the folders already here
rem          /update              bring existing clones up to date (git pull);
rem                               only with /unpinned - pinned, the pin decides
rem          /unpinned            ignore payload.lock.json and take the latest
rem                               release or default branch, as before
rem          /doppio-ref=<ref>    build Doppio from this tag or branch instead
rem                               of the pin (implies unpinned for Doppio)
rem          /ristretto-ref=<ref> the same for Ristretto
rem
rem  Note on structure: every check lives in a subroutine that returns an exit
rem  code, and the main flow is flat `call :x || goto :fail`. Jumping out of a
rem  parenthesised if-block with `goto` is unreliable once delayed expansion is
rem  on - it was silently running the wrong failure message.
rem ===========================================================================

setlocal EnableDelayedExpansion
cd /d "%~dp0"

rem Captured here because inside a `call :label` subroutine %0 is the label,
rem not the script, so %~dp0 cannot be used down there.
set "ROOT=%~dp0"

rem --- elevate ---------------------------------------------------------------
rem /elevated is a sentinel this script passes to itself. Without it, a machine
rem where the admin test misreads would relaunch forever.
set RELAUNCHED=
for %%a in (%*) do if /i "%%a"=="/elevated" set RELAUNCHED=1

net session >nul 2>&1
if not errorlevel 1 goto :isadmin
if defined RELAUNCHED goto :notadmin

echo Asking for administrator rights...

rem Two calls, not one with %*: when there are no arguments, -ArgumentList ''
rem is an empty string, and Start-Process refuses to bind that. The result is
rem a PowerShell error and no elevated window at all - which looks exactly
rem like the script dying on the first line.
if "%~1"=="" (
    powershell -NoProfile -Command "Start-Process -FilePath '%~f0' -ArgumentList '/elevated' -Verb RunAs" 2>nul
) else (
    powershell -NoProfile -Command "Start-Process -FilePath '%~f0' -ArgumentList '%* /elevated' -Verb RunAs" 2>nul
)
if errorlevel 1 goto :notadmin
echo A new window should have opened with administrator rights. Carry on there.
timeout /t 4 >nul
exit /b 0

:notadmin
echo.
echo   This needs to run as administrator and could not elevate itself.
echo   Right-click bootstrap.bat and choose "Run as administrator".
echo.
pause
exit /b 1

:isadmin

set NODEPS=
set NOCLONE=
set UPDATE=
set DOPPIOREF=
set RISTRETTOREF=

rem Parsed with shift and not `for %%a in (%*)`, because a plain FOR treats
rem `=` as a separator in its set: /doppio-ref=v1.0 would arrive as two
rem tokens and the value would be lost. %1 is safe to consume here - the
rem elevation above is done with it, and nothing below reads it again.
:parseargs
if "%~1"=="" goto :argsdone
if /i "%~1"=="/nodeps"   set NODEPS=1
if /i "%~1"=="/noclone"  set NOCLONE=1
if /i "%~1"=="/update"   set UPDATE=1
if /i "%~1"=="/unpinned" set UNPINNED=1
rem /elevated is the sentinel the relaunch above passes to itself. It is
rem matched by nothing here on purpose; falling through is what should happen.
set "ARG=%~1"
if /i "!ARG:~0,12!"=="/doppio-ref="    set "DOPPIOREF=!ARG:~12!"
if /i "!ARG:~0,15!"=="/ristretto-ref=" set "RISTRETTOREF=!ARG:~15!"
shift
goto :parseargs
:argsdone
set "ARG="

rem The downloader. Kept out of this file because it does real work - a
rem ladder of clone, release and branch-zip, each with a progress bar - and
rem batch is the wrong language for an HTTP stream read in a loop.
set "FETCH=%ROOT%tools\fetch-from-github.ps1"

rem The pins. Same file the release workflow builds from, so a self-built copy
rem and a downloaded one come from the same commits. /unpinned, or an
rem explicit /doppio-ref= or /ristretto-ref=, opts out per repository.
set "LOCK=%ROOT%payload.lock.json"
set PINNED=
if not defined UNPINNED if exist "%LOCK%" set PINNED=1
set PINFAIL=

rem -Ref for a single file is the branch, so a tag pinned for the repository
rem is not reusable here; only an explicit branch is.
set "DOPPIOFILEREF="
if defined DOPPIOREF set "DOPPIOFILEREF=-Ref %DOPPIOREF%"

set FAILMSG=
set REBOOTPENDING=

rem Never let git stop and ask for a username. A repository that is private or
rem does not exist both answer 404 to an anonymous clone, and git's reaction to
rem that is a credential prompt - which in a script looks like a hang. Failing
rem immediately is what we want: a missing optional repo is not an error.
set GIT_TERMINAL_PROMPT=0
set GCM_INTERACTIVE=never

echo.
echo ===========================================================
echo   Doppio / Ristretto bootstrap
echo   Working in: %CD%
echo ===========================================================
echo.

where winget >nul 2>&1 && (set HAVEWINGET=1) || (set HAVEWINGET=)

call :ensure_git     || goto :fail
call :ensure_dotnet  || goto :fail
call :ensure_msvc    || goto :fail
echo.

call :get_sources    || goto :fail
echo.

call :build_all      || goto :fail
echo.

echo ===========================================================
echo   Everything is built. Starting the wizard.
echo.
echo   It already knows where the folders are.
echo.
echo   SNAPSHOT THE VM NOW if you have not. The build tools are
echo   installed, nothing on this machine has been changed yet,
echo   so this is the snapshot worth keeping.
echo ===========================================================
echo.
pause

start "" "!LAUNCH!"
exit /b 0


rem ===========================================================================
rem  Dependencies
rem ===========================================================================

:ensure_git
where git >nul 2>&1
if not errorlevel 1 (
    echo [deps] git ok
    exit /b 0
)
if defined NODEPS (set FAILMSG=git& exit /b 1)
if not defined HAVEWINGET (set FAILMSG=git& exit /b 1)
echo [deps] Installing Git...
rem --source winget matters: without it winget also searches msstore, and on a
rem fresh VM that source often fails its certificate check. The package is then
rem "found in multiple sources" and winget refuses to pick one.
winget install --id Git.Git -e --source winget --silent --disable-interactivity ^
    --accept-package-agreements --accept-source-agreements

rem winget updates the PATH of new processes only, so look where it put it.
set "PATH=%PATH%;%ProgramFiles%\Git\cmd"
where git >nul 2>&1
if errorlevel 1 (set FAILMSG=git& exit /b 1)
echo [deps] git ok
exit /b 0


:ensure_dotnet
call :has_dotnet10
if not errorlevel 1 (
    echo [deps] .NET 10 SDK ok
    exit /b 0
)
if defined NODEPS (set FAILMSG=dotnet& exit /b 1)
if not defined HAVEWINGET (set FAILMSG=dotnet& exit /b 1)
echo [deps] Installing the .NET 10 SDK...
winget install --id Microsoft.DotNet.SDK.10 -e --source winget --silent --disable-interactivity ^
    --accept-package-agreements --accept-source-agreements

set "PATH=%PATH%;%ProgramFiles%\dotnet"
call :has_dotnet10
if errorlevel 1 (set FAILMSG=dotnet& exit /b 1)
echo [deps] .NET 10 SDK ok
exit /b 0

:has_dotnet10
where dotnet >nul 2>&1
if errorlevel 1 exit /b 1
set FOUND10=
for /f "tokens=1 delims= " %%v in ('dotnet --list-sdks 2^>nul') do (
    set VER=%%v
    if "!VER:~0,3!"=="10." set FOUND10=1
)
if defined FOUND10 exit /b 0
exit /b 1


:ensure_msvc
call :findvcvars
if defined VCVARS (
    echo [deps] MSVC ok
    exit /b 0
)
if defined NODEPS (set FAILMSG=msvc& exit /b 1)
if not defined HAVEWINGET (set FAILMSG=msvc& exit /b 1)
echo [deps] Installing the Visual Studio C++ build tools.
echo        Several gigabytes. This takes a while and looks like it has hung.
winget install --id Microsoft.VisualStudio.2022.BuildTools -e --source winget --silent --disable-interactivity ^
    --accept-package-agreements --accept-source-agreements ^
    --override "--quiet --wait --norestart --add Microsoft.VisualStudio.Workload.VCTools --add Microsoft.VisualStudio.Component.VC.Tools.x86.x64 --includeRecommended"

rem 3010 is "installed, but Windows wants a restart". Command-line builds
rem usually work before that restart - the pending part is shell and IDE
rem integration - so this carries on and only mentions it if a build then fails.
if "!errorlevel!"=="3010" set REBOOTPENDING=1

call :findvcvars
if not defined VCVARS (set FAILMSG=msvc& exit /b 1)
if defined REBOOTPENDING (
    echo [deps] MSVC ok - the installer asked for a restart. Carrying on;
    echo        if the C++ build fails below, reboot and run this again.
) else (
    echo [deps] MSVC ok
)
exit /b 0

:findvcvars
set VCVARS=
set "VSWHERE=%ProgramFiles(x86)%\Microsoft Visual Studio\Installer\vswhere.exe"
if not exist "!VSWHERE!" exit /b 0
for /f "usebackq tokens=*" %%i in (`"!VSWHERE!" -latest -prerelease -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath 2^>nul`) do (
    if exist "%%i\VC\Auxiliary\Build\vcvars64.bat" set "VCVARS=%%i\VC\Auxiliary\Build\vcvars64.bat"
)
exit /b 0


rem ===========================================================================
rem  Sources
rem ===========================================================================

:get_sources
rem Everything the three builds need is fetched here. Doppio and Ristretto
rem come from GitHub; nothing below expects a human to have downloaded
rem anything first. tools\fetch-from-github.ps1 walks clone -> latest release
rem -> default-branch zip and says which rung worked, so this stays a flat
rem "did it arrive or not".
if not exist "src\Barista.csproj" (set FAILMSG=nosetup& exit /b 1)
if not exist "%FETCH%" (set FAILMSG=nofetcher& exit /b 1)

if defined PINNED (
    echo [src] Pinned to the commits in payload.lock.json.
) else (
    echo [src] Unpinned: taking whatever GitHub currently has.
)

if not defined NOCLONE (
    call :fetch_repo Doppio build.bat
    rem Ristretto is optional. A repository that is private, unpublished or
    rem renamed answers 404 to an anonymous fetch exactly as a missing one
    rem does, so a failure here is "not available" and not an error.
    call :fetch_repo Ristretto build-ristretto.bat
) else (
    echo [src] /noclone - using the folders that are already here, unchecked.
)

if defined PINFAIL (set FAILMSG=pin& exit /b 1)
if not exist "Doppio\build.bat" (set FAILMSG=nodoppio& exit /b 1)

call :ensure_install_script || exit /b 1

set HAVERISTRETTO=
if exist "Ristretto\build-ristretto.bat" set HAVERISTRETTO=1
if defined HAVERISTRETTO (
    call :fetch_submodule
) else (
    echo [src] Ristretto is not here - building Doppio only.
    echo       Pass /ristretto-ref=^<tag^> if you have access to a specific
    echo       release, or unpack it into a folder called Ristretto beside
    echo       this file and run again.
)

echo [src] sources ok
exit /b 0


rem --- one repository --------------------------------------------------------
rem %1 = repository name under github.com/Nobrac, and the folder it lands in
rem %2 = a file that must exist inside it afterwards
rem
rem Deliberately has no failure path of its own. Whether a missing repository
rem is fatal is the caller's question - Doppio is, Ristretto is not - and it
rem is answered by the `if not exist` checks above, against the same marker
rem file the build then uses. One place decides what "present" means.
:fetch_repo
set "REF="
if /i "%~1"=="Ristretto" if defined RISTRETTOREF set "REF=-Ref %RISTRETTOREF%"
if /i "%~1"=="Doppio"    if defined DOPPIOREF    set "REF=-Ref %DOPPIOREF%"

rem Pinned unless this repository was given its own ref.
set "PIN="
if defined PINNED if not defined REF set "PIN=-LockFile "%LOCK%" -LockKey %~1"

set "UPD="
if defined UPDATE if not defined PIN set "UPD=-Update"

echo [src] %~1...
powershell -NoProfile -ExecutionPolicy Bypass -File "%FETCH%" ^
    -Owner Nobrac -Repo "%~1" -Dest "%ROOT%%~1" -Marker "%~2" !REF! !PIN! !UPD!
rem 2 = something is here that is not the pin. Stop rather than build it.
if errorlevel 2 set "PINFAIL=%~1"
exit /b 0


rem --- install-doppio.ps1 ----------------------------------------------------
rem The stage-1 script the wizard runs. It lives in Doppio, but a checkout that
rem predates it will not have it, and the wizard's preflight blocks without it
rem (Preflight.DoppioCoreFiles). Three places are tried, in order of how
rem trustworthy the copy is:
rem
rem   1. the one already in Doppio\      - under version control, always wins
rem   2. a copy beside this bootstrap    - what a zip release of Barista ships
rem   3. Doppio's default branch on raw  - for an older clone
rem
rem Fetching it on its own rather than re-cloning Doppio matters: by this point
rem Doppio may already be built, and throwing that away to pick up one 4 KB
rem script would mean another MSVC run.
:ensure_install_script
if exist "Doppio\install-doppio.ps1" exit /b 0

if exist "install-doppio.ps1" (
    copy /y "install-doppio.ps1" "Doppio\install-doppio.ps1" >nul
    echo [src] placed install-doppio.ps1 into Doppio\
    exit /b 0
)

echo [src] install-doppio.ps1 is not in the Doppio checkout - fetching it...
powershell -NoProfile -ExecutionPolicy Bypass -File "%FETCH%" ^
    -Owner Nobrac -Repo Doppio -Dest "%ROOT%Doppio" -File install-doppio.ps1 !DOPPIOFILEREF!

if exist "Doppio\install-doppio.ps1" exit /b 0
set FAILMSG=noinstallscript
exit /b 1


rem --- the Doppio sources Ristretto links in --------------------------------
:fetch_submodule
if exist "Ristretto\extern\doppio\verify.cpp" exit /b 0

rem Two ways Ristretto can be here. A clone is a git repository and the
rem submodule command works. A downloaded zip is NOT a repository - GitHub
rem source archives do not contain submodule contents - and `git submodule
rem update` there fails with "not a git repository", so in that case the
rem sources are copied straight out of the Doppio folder next door. That also
rem guarantees the two halves are built from the same Doppio.
if exist "Ristretto\.git" (
    echo [src] Fetching the Doppio submodule for Ristretto...
    pushd Ristretto
    git submodule update --init --recursive --quiet
    popd
)

rem Pinned, the fetcher has already put the submodule at the commit Ristretto
rem records. Copying Doppio next door across would build a different Doppio
rem than the release does, so that fallback is for unpinned runs only.
if defined PINNED if not defined RISTRETTOREF (
    if exist "Ristretto\extern\doppio\verify.cpp" exit /b 0
    echo [src] WARNING: Ristretto's pinned Doppio sources are missing. Skipping Ristretto.
    set HAVERISTRETTO=
    exit /b 0
)

if not exist "Ristretto\extern\doppio\verify.cpp" (
    echo [src] Copying the Doppio sources Ristretto links into
    echo       Ristretto\extern\doppio\ ...
    if not exist "Ristretto\extern\doppio" mkdir "Ristretto\extern\doppio"
    copy /y "Doppio\*.cpp" "Ristretto\extern\doppio\" >nul 2>&1
    copy /y "Doppio\*.h"   "Ristretto\extern\doppio\" >nul 2>&1
)

if exist "Ristretto\extern\doppio\verify.cpp" exit /b 0
echo [src] WARNING: could not get the Doppio sources Ristretto needs. Skipping it.
set HAVERISTRETTO=
exit /b 0


rem ===========================================================================
rem  Build
rem ===========================================================================

:build_all
echo [build] Loading the x64 MSVC environment...
call "!VCVARS!" >nul
if errorlevel 1 (set FAILMSG=vcvars& exit /b 1)
echo [build] Doppio...
pushd Doppio
call build.bat
set RC=!errorlevel!
popd
if not "!RC!"=="0" (set FAILMSG=build& exit /b 1)
if defined HAVERISTRETTO (
    echo [build] Ristretto...
    pushd Ristretto
    call build-ristretto.bat
    set RC=!errorlevel!
    popd
    if not "!RC!"=="0" (set FAILMSG=build& exit /b 1)
)

echo [build] The setup wizard...
pushd src
dotnet publish -c Release -r win-x64 --self-contained true ^
    -p:PublishSingleFile=true ^
    -p:IncludeNativeLibrariesForSelfExtract=true ^
    -p:EnableCompressionInSingleFile=true --nologo ^
    -o "!ROOT!wizard-out"
set RC=!errorlevel!
popd
if not "!RC!"=="0" (set FAILMSG=build& exit /b 1)
rem -o above pins the output folder. Without it the path depends on the
rem project's own settings - a csproj carrying <Platforms>x64</Platforms>
rem publishes to bin\x64\Release\... while one without it uses bin\Release\...
rem Guessing wrong made a successful build look like a failed one.
set "WIZARD=!ROOT!wizard-out\Barista.exe"
if not exist "!WIZARD!" (set FAILMSG=nowizard& exit /b 1)

rem A wizard left running from an earlier attempt holds its own exe open, so
rem the copy below fails - and a failed copy used to be invisible, which meant
rem this script cheerfully relaunched the OLD binary and every fix looked like
rem it had changed nothing. Close it first, and check that the copy worked.
taskkill /im Barista.exe /f >nul 2>&1

set "LAUNCH=!WIZARD!"
copy /y "!WIZARD!" "!ROOT!Barista.exe" >nul 2>&1
if errorlevel 1 (
    echo [build] note: could not refresh !ROOT!Barista.exe, running from wizard-out
) else (
    set "LAUNCH=!ROOT!Barista.exe"
)
echo [build] all built
exit /b 0


rem ===========================================================================
rem  Failure
rem ===========================================================================

:fail
echo.
if "%FAILMSG%"=="git" (
    echo   git is needed and could not be installed automatically.
    echo.
    echo   If winget complained about the msstore source and a certificate,
    echo   that source is broken on this machine. Either:
    echo       winget source reset --force
    echo   or just install git by hand:
    echo       https://git-scm.com/download/win
    echo   Then open a NEW window and run this again.
)
if "%FAILMSG%"=="dotnet" (
    echo   The .NET 10 SDK is needed and could not be installed automatically.
    echo   Get it from https://dotnet.microsoft.com/download/dotnet/10.0
    echo   then open a NEW window and run this again.
)
if "%FAILMSG%"=="msvc" (
    echo   The MSVC C++ toolset is needed and could not be installed
    echo   automatically. Install "Desktop development with C++" from the
    echo   Visual Studio Installer, or the standalone Build Tools, then run
    echo   this again.
)
if "%FAILMSG%"=="nodoppio" (
    echo   No Doppio\build.bat here, and Doppio could not be fetched from
    echo   github.com/Nobrac/Doppio. The lines above say which of clone,
    echo   release and zip was tried and how each one answered.
    echo.
    echo   If this machine has no way out to GitHub, put the Doppio repository
    echo   in a folder called Doppio beside this file and run with /noclone.
)
if "%FAILMSG%"=="pin" (
    echo   %PINFAIL% is here, but not at the commit payload.lock.json pins,
    echo   and it was left alone: it has local changes, or it is a folder of
    echo   unknown origin. The lines above say which.
    echo.
    echo   Either rename or delete that folder and run again, so the pinned
    echo   version is fetched - or, to build what is there anyway, run with
    echo   /unpinned. An unpinned build is not what the release contains.
)
if "%FAILMSG%"=="nosetup" (
    echo   No src\Barista.csproj here.
    echo.
    echo   This script belongs at the root of the Barista repository, with the
    echo   wizard project in src\ beside it:
    echo       %CD%\src\Barista.csproj
    echo.
    echo   If you unpacked a zip and got a Barista\Barista\ folder, move the
    echo   inner one up a level.
)
if "%FAILMSG%"=="noinstallscript" (
    echo   Doppio\install-doppio.ps1 is missing. The wizard calls it as its
    echo   first stage, so the install cannot start without it.
    echo.
    echo   It was looked for in the Doppio checkout, beside this file, and on
    echo   Doppio's default branch on GitHub. If this machine has no way out
    echo   to raw.githubusercontent.com, download it by hand and put it next
    echo   to this bootstrap, then run again.
)
if "%FAILMSG%"=="nofetcher" (
    echo   tools\fetch-from-github.ps1 is missing. That is the script that
    echo   fetches Doppio and Ristretto, and it ships with Barista.
    echo.
    echo   Expected at:
    echo       %ROOT%tools\fetch-from-github.ps1
    echo.
    echo   If you copied bootstrap.bat out of the repository on its own, take
    echo   the tools\ folder with it - or clone Barista instead.
)
if "%FAILMSG%"=="vcvars" (
    echo   vcvars64.bat failed. The C++ toolset looks installed but is not
    echo   usable. Repair it in the Visual Studio Installer.
)
if "%FAILMSG%"=="build" (
    echo   A build failed. The output above says which one and why.
    echo   Nothing has been installed on this machine - only built.
)
if "%FAILMSG%"=="nowizard" (
    echo   The wizard compiled, but Barista.exe is not in wizard-out\.
    echo   Look at the publish output above for where it actually went.
)
if defined REBOOTPENDING (
    echo.
    echo   NOTE: the Visual Studio installer asked for a restart earlier
    echo   ^(exit code 3010^). If the C++ build was the thing that failed,
    echo   reboot and run this again before looking any further.
)
echo.
pause
exit /b 1
