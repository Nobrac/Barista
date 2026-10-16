# Barista

Setup tool for [Doppio](https://github.com/Nobrac/Doppio) and
[Ristretto](https://github.com/Nobrac/Ristretto). It fetches and builds them,
then walks you through installing them one change per reboot, with a check
after each one.

Doppio puts a 2FA tile on the Windows logon screen. Ristretto requires a
one-time code for network logons. For their LSA halves both need an unsigned
DLL in `lsass.exe`, several registry keys written in the right order, and
`RunAsPPL` off.

Blog series at <https://theadmincafe.ch>: Part 1 (credential provider),
Part 2 (inside LSA), Part 3 (the gate).

> **Test-VM only.** This installs code that runs inside `lsass.exe`. A fault
> there is a bugcheck on the logon path, and that can leave a machine that
> will not boot. Snapshot before every reboot. Barista asks you to confirm
> that and will not start until you do.

## Download and run (standalone)

The release page has one file, `Barista.exe`, with everything embedded. The
machine you run it on needs administrator rights and nothing else: no git,
.NET, C++ compiler or network.

1. Download `Barista.exe` and `SHA256SUMS` from the release page.
2. Compare: `certutil -hashfile Barista.exe SHA256` must print the value
   `SHA256SUMS` lists for `Barista.exe`.
3. Snapshot the VM, then right-click `Barista.exe` → *Run as administrator*.

CI compiles Doppio and Ristretto from the commits pinned in
[`payload.lock.json`](payload.lock.json) and embeds them in the exe along with
the SHA-256 of every file. The wizard's Integrity page unpacks them into
`%ProgramFiles%\TheAdminCafe\Barista\payload\<id>` (writable only by
administrators), reads every file back and compares hashes. Continue stays off
unless all of them match. The check runs again after every reboot and before
every stage.

To make a release, push a tag `v*`. [`release.yml`](.github/workflows/release.yml)
checks out both repositories at their pins, runs
[`tools/build-payload.ps1`](tools/build-payload.ps1), publishes with
`-p:RequirePayload=true` and attaches `Barista.exe`, `SHA256SUMS` and
`payload-manifest.json` to a draft release for a person to publish. To move to
newer Doppio or Ristretto commits, change the pins in `payload.lock.json`.

## Building from source

The rest of this page is about a developer build without an embedded payload,
which asks for folders and can build them.

```bat
git clone https://github.com/Nobrac/Barista
cd Barista
bootstrap.bat
```

It elevates itself, installs git, the .NET 10 SDK and the MSVC build tools
through winget if they are missing, fetches Doppio and Ristretto from GitHub
beside itself, builds all three and opens the wizard.

| Flag | |
|---|---|
| `/nodeps` | install nothing through winget |
| `/noclone` | fetch nothing; use whatever folders are already there |
| `/update` | bring existing clones up to date (`git pull --ff-only`); only with `/unpinned` |
| `/unpinned` | ignore `payload.lock.json` and take the latest release or default branch |
| `/doppio-ref=<ref>` | build Doppio from this tag or branch instead of the pin |
| `/ristretto-ref=<ref>` | the same for Ristretto |

By default the bootstrap fetches the commits in
[`payload.lock.json`](payload.lock.json), the same ones the standalone release
is built from. A clone at another commit is moved to the pin if it has no local
changes. A folder with local changes or of unknown origin is left alone and the
bootstrap stops and says so.

### How the sources are fetched

`tools/fetch-from-github.ps1` tries three sources per repository and reports
each attempt:

1. `git clone --recurse-submodules`, the only one that brings history and the
   Doppio submodule Ristretto links in.
2. The latest release's source zip, if the repository publishes releases.
3. The default branch as a zip from codeload. This needs no git, which helps
   with `/nodeps` or on a network that passes HTTPS GETs but not git.

Pinned, it downloads a zip of exactly the pinned commit when git is missing.
GitHub source zips never include submodules, so the bootstrap then downloads
Ristretto's Doppio submodule as a second zip at the commit `payload.lock.json`
records. Unpinned, it copies the Doppio sources into Ristretto's
`extern/doppio/` from the Doppio folder next door.

A private, unpublished or renamed repository answers 404 to all three. For
Ristretto the bootstrap carries on with Doppio alone; for Doppio it stops. A
folder that already exists and is not empty is reported and never deleted or
overwritten.

If the Doppio checkout predates `install-doppio.ps1`, that script is fetched on
its own, since the wizard's preflight blocks without it. Barista looks for it
in `Doppio/` first, then beside the bootstrap, then on Doppio's default branch.

## What stays manual

- The VM snapshot. A machine cannot snapshot itself, so Barista stops and
  reminds you at the point where it matters.
- The code from your phone, which is the second factor.
- The three risk checkboxes. You are about to disable lsass protection and
  load an unsigned DLL into the logon path.
- The `RunAsPPL` decision, which has its own button and dialog. Turning it off
  makes the machine much easier to loot, so no install step does it as a side
  effect.

## One change per reboot

Part 2 of the blog: "Change one thing per reboot. If you register both packages
at once and the machine does not come back, you do not know which one did it."
The install runs in stages:

```
1. credential provider    → restart → "does the tile appear, next to the usual ones?"
2. enrollment + reindex      (no restart)
3. provider filter        → restart → "can you still get in, with only the tile?"
4. network policy         → restart → "did it come back? is the filter called?"
5. authentication package → restart → "did it come back?"
```

After each restart Barista opens again and asks whether the machine is healthy
before running anything else. If you answer no, it stops, tells you what the
last stage changed and offers the matching uninstall script.

Enrollment comes before the filter because the accounts the filter will judge
need a secret first, and `enroll /reindex` has to run before the filter reads
the RID index.

Stages 1 and 3 are two runs of `install-doppio.ps1`. Without `-EnableFilter`
the tile is only offered alongside the password tile, so stage 1 installs the
tile and stage 3 makes it the only option. Before stage 3 runs, Barista asks
whether you have signed in through the tile yet, since no registry read can
tell and it is the last point where the password tile can still get you in.

Stage 3 comes before the network stages so that a broken tile still leaves a
network logon as a way in.

### Resuming after a reboot

`%ProgramData%\TheAdminCafe\Barista\pending-run.json` holds paths, account
names, finished stages and the log so far. It holds no secrets; those stay
DPAPI-encrypted in Doppio's own store. Barista writes it after every finished
stage, including stages without a reboot, so closing the window cannot cause
`enroll.exe` to run twice for the same account. The folder gets an ACL only
SYSTEM and Administrators can change, even if someone created it first, and
files in it that neither of them owns are deleted before anything is read.

When a reboot is pending, Barista also sets a value under
`HKLM\...\CurrentVersion\RunOnce` so the window opens after the next logon.
The value deletes itself when it fires. If it never fires, starting Barista by
hand finds the record and offers to continue.

## Enrolled and denied

The accounts page asks two questions per account: give it a second factor, and
close its non-interactive paths.

The stage 3 filter refuses to register while any enabled local account has no
secret, so the break-glass admin has to be enrolled. `deny-noninteractive.ps1`
says in its header to "keep a second admin account that still works". So
enrolling an account does not deny it: enrolled, the tile accepts it; not
denied, SMB, WinRM or RDP still get you in when the tile does not. Denying is
opt-in per account, and the page warns when a selection leaves nothing that can
reach the machine without the tile.

## What Barista changes itself

Every registry write, System32 copy and LSA registration happens in Doppio's
and Ristretto's own `install-*.ps1` scripts. Barista finds them, explains what
each one does, runs them in order and shows the output. Those scripts carry the
guards (the `RunAsPPL` check, the "Auth0 belongs to somebody else" check, the
`PrevAuth0` bookkeeping, the DLL on disk before the registry names it), and a
second copy in C# would drift from them.

Barista does four things to the machine directly:

| Action | Why it is not in a script |
|---|---|
| Write `RunAsPPL` (and `RunAsPPLBoot`) | Not an install step. Its own dialog, offered only when a component that loads into lsass was chosen. Both values, because every install script checks both. |
| Launch `enroll.exe` | In its own console window, because the `y/N` prompts use `_getwch()`, which reads the console directly and ignores redirected stdin. |
| Write the pending-run record and `RunOnce` | So a staged install survives its reboots. |
| `reg import unregister.reg` | The only way back out of the credential provider is one of Doppio's own `.reg` files, and `powershell -File` cannot run a `.reg`. The keys and their deletion order still live in that file. |

Recovery is built from what is registered (the Auth0 value, the LSA package
list, the provider and filter CLSIDs), read before anything is offered. Those
stop matching the plan when a stage fails, which is when you need recovery.

## Building just the wizard

```bat
cd src
dotnet publish -c Release -r win-x64 --self-contained true ^
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true ^
    -p:EnableCompressionInSingleFile=true -o ..\wizard-out
```

Needs the .NET 10 SDK, which Visual Studio with the C++ workload does not
include. Doppio and Ristretto need MSVC instead. The wizard project pulls in
`core/Barista.Core.csproj`, so publishing `src/` builds both.

One NuGet dependency: QRCoder (MIT, pure managed), which renders the
`otpauth://` URI as a QR code in-process.

Keep `-o`: depending on whether a csproj sets `<Platforms>x64</Platforms>`, the
output lands in `bin\x64\Release\...` or `bin\Release\...`.

## Layout

```
bootstrap.bat          fetch, build, launch
tools/
  fetch-from-github.ps1  clone -> release -> branch zip, with a progress bar
  build-payload.ps1      pinned checkouts -> the payload a release embeds
core/                  everything that decides what happens to the machine
  Preflight.cs           the checks the install scripts would otherwise fail on
  InstallPlan.cs         the stage plan and the recovery plan
  StageRunner.cs         runs one stage, stops at the first failure, saves
  StateStore.cs          the pending-run record and the RunOnce entry
  ProcessRunner.cs       runs the repo's own scripts
  BuildRunner.cs         builds Doppio and Ristretto through vcvars64
src/                   the WPF wizard - windows, pages, dialogs
  Views/               one file per wizard page
tests/                 what the stage plan, the runner and the payload are held to
payload.lock.json      the Doppio and Ristretto commits a release is built from
Doppio/                fetched by bootstrap, gitignored
Ristretto/             fetched by bootstrap, gitignored
```

`core/` has no WPF in it, so the stage plan and runner can be tested without a
desktop. `tests/` covers the plan's order, stopping at the first failed step,
saving every finished stage, and arming `RunOnce` only when a reboot is
pending. CI runs it on every push.

```bat
dotnet test tests\Barista.Core.Tests\Barista.Core.Tests.csproj
```

## Still open

- No unattended or config-file mode.
- No live capture of the filter's `OutputDebugStringW` output. A
  `DBWIN_BUFFER` reader would show whether MSV1_0 calls it without WinDbg, but
  lsass is in session 0 and it is unverified that `Global\DBWIN_BUFFER`
  reaches it.
- Enrollment still opens a console window. A fresh enrollment could be driven
  over pipes (`ConfirmSecret` reads with `fgetws`, which honours redirected
  stdin), leaving only the re-enrollment prompt needing a real console.

## Licence

MIT, same as Doppio and Ristretto. See `LICENSE`.
