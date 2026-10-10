@echo off
rem ===========================================================================
rem  Barista - one-click setup for Doppio and Ristretto
rem
rem  This sits at the root of the Barista repository. Clone it and run this;
rem  everything else is fetched and built beside it. It will:
rem
rem    1. elevate itself
rem    2. install git, the .NET 10 SDK and the MSVC build tools if missing
rem    3. clone Doppio and Ristretto if they are not already beside it
rem    4. pull the Doppio submodule Ristretto needs
rem    5. build all three projects
rem    6. launch the setup wizard
rem
rem  What it CANNOT do for you:
rem    - take the VM snapshot (a VM cannot snapshot itself; that is the host)
rem    - type the code off your phone
rem    - decide whether to turn RunAsPPL off
rem
rem  Flags:  /nodeps   do not install anything with winget
rem          /noclone  do not clone; use the folders that are already here
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
for %%a in (%*) do (
    if /i "%%a"=="/nodeps"  set NODEPS=1
    if /i "%%a"=="/noclone" set NOCLONE=1
)

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
if not defined NOCLONE call :clone_missing

if not exist "Doppio\build.bat"               (set FAILMSG=nodoppio& exit /b 1)
if not exist "src\Barista.csproj" (set FAILMSG=nosetup& exit /b 1)
rem install-doppio.ps1 is the stage-1 script the wizard calls. It is new and
rem may not be committed to Doppio yet, so a copy sitting next to this file is
rem put in place. One already in the repo always wins: that is the version
rem under version control.
if exist "install-doppio.ps1" (
    if not exist "Doppio\install-doppio.ps1" (
        copy /y "install-doppio.ps1" "Doppio\install-doppio.ps1" >nul
        echo [src] placed install-doppio.ps1 into Doppio\
    )
)
if not exist "Doppio\install-doppio.ps1" (set FAILMSG=noinstallscript& exit /b 1)
set HAVERISTRETTO=
if exist "Ristretto\build-ristretto.bat" set HAVERISTRETTO=1
if defined HAVERISTRETTO call :fetch_submodule

echo [src] sources ok
exit /b 0

:clone_missing
if not exist "Doppio\build.bat" (
    if exist "Doppio" (
        echo [src] A folder called Doppio is here but has no build.bat - leaving it alone.
    ) else (
        echo [src] Cloning Doppio...
        git clone --quiet https://github.com/Nobrac/Doppio Doppio
    )
)
rem Ristretto is optional, and at the time of writing it is not published yet.
rem An anonymous clone of a repository that does not exist fails the same way a
rem private one does, so this treats a failure as "not available" and moves on
rem rather than making it look like a permissions problem.
if not exist "Ristretto\build-ristretto.bat" (
    if exist "Ristretto" (
        echo [src] A folder called Ristretto is here but has no build script - leaving it alone.
    ) else (
        echo [src] Trying to clone Ristretto...
        git clone --quiet https://github.com/Nobrac/Ristretto Ristretto 2>nul
        if not exist "Ristretto\build-ristretto.bat" (
            rmdir /s /q Ristretto 2>nul
            echo [src] Ristretto is not available from GitHub. Skipping it.
            echo       To include it, unpack Ristretto.zip into a folder called
            echo       Ristretto beside this file and run again.
        )
    )
)
exit /b 0

:fetch_submodule
if exist "Ristretto\extern\doppio\verify.cpp" exit /b 0

rem Two ways Ristretto can be here. A clone is a git repository and the
rem submodule command works. An unpacked zip is NOT a repository, and
rem `git submodule update` there fails with "not a git repository" - so in that
rem case the sources are copied straight out of the Doppio folder next door.
rem That also guarantees the two halves are built from the same Doppio.
if exist "Ristretto\.git" (
    echo [src] Fetching the Doppio submodule for Ristretto...
    pushd Ristretto
    git submodule update --init --recursive --quiet
    popd
) else (
    echo [src] Ristretto is not a git clone, so copying the Doppio sources
    echo       it links into Ristretto\extern\doppio\ ...
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
    echo   No Doppio\build.bat here. Either let this clone it, or put the
    echo   Doppio repository in a folder called Doppio beside this file.
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
    echo   Doppio\install-doppio.ps1 is missing and no copy was found beside
    echo   this file. The wizard calls it as its first stage.
    echo.
    echo   Put install-doppio.ps1 next to this bootstrap and run again.
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
