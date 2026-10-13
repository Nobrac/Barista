# Barista

**One-click setup for [Doppio](https://github.com/Nobrac/Doppio) and
[Ristretto](https://github.com/Nobrac/Ristretto).** Fetches them, builds them,
and walks you through installing them — one change per reboot, with a check
after each one.

Doppio puts a 2FA tile on the Windows logon screen. Ristretto gates network
logons behind a one-time code. Both need an unsigned DLL in `lsass.exe` for
their LSA halves, several registry keys in the right order, and `RunAsPPL`
off. That is a lot of places to make a quiet mistake, which is what this is for.

Blog series at <https://theadmincafe.ch>: Part 1 (credential provider),
Part 2 (inside LSA), Part 3 (the gate).

> **Test-VM only.** This installs code that runs inside `lsass.exe`. A fault
> there is a bugcheck on the logon path, and that can leave a machine that
> will not boot. Snapshot before every reboot. Barista asks you to confirm
> that, and will not start until you do.

## Download and run (standalone)

The release page has one file, `Barista.exe`, that brings everything along.
The machine you run it on needs **no git, no .NET, no C++ compiler and no
network** - only administrator rights.

1. Download `Barista.exe` and `SHA256SUMS` from the release page.
2. Compare: `certutil -hashfile Barista.exe SHA256` must print the value
   `SHA256SUMS` lists for `Barista.exe`.
3. Snapshot the VM, then right-click `Barista.exe` → *Run as administrator*.

Doppio and Ristretto are compiled by CI from the commits pinned in
[`payload.lock.json`](payload.lock.json) and embedded into the exe, with the
SHA-256 of every file. The second page of the wizard, **Integrity**, unpacks
them into `%ProgramFiles%\TheAdminCafe\Barista\payload\<id>` (writable only
by administrators), reads every file back and shows expected against actual
hash for each one. Continue stays off unless all of them match. The same
check runs again after every reboot and right before every stage.

How a release is made: push a tag `v*`. [`release.yml`](.github/workflows/release.yml)
checks out both repositories at their pins, runs
[`tools/build-payload.ps1`](tools/build-payload.ps1), publishes with
`-p:RequirePayload=true` and attaches `Barista.exe`, `SHA256SUMS` and
`payload-manifest.json` to a **draft** release for a person to publish.
To move to newer Doppio or Ristretto commits, change the pins in
`payload.lock.json` - nothing else.

The rest of this page is about the *building* machine: a developer build
without an embedded payload, which asks for folders and can build them.

## Use it

This is the *building* machine. If you only want to install Doppio and
Ristretto somewhere — or hand them to somebody — use the standalone
`Barista.exe` from the release page instead, see
[Download and run](#download-and-run-standalone): that needs no compiler at
all.

```bat
git clone https://github.com/Nobrac/Barista
cd Barista
bootstrap.bat
```

That is the whole thing. It elevates itself, installs git, the .NET 10 SDK and
the MSVC build tools through winget if they are missing, **fetches Doppio and
Ristretto from GitHub** beside itself, builds all three, and opens the wizard.
Nothing has to be downloaded by hand first.

| Flag | |
|---|---|
| `/nodeps` | install nothing through winget |
| `/noclone` | fetch nothing; use whatever folders are already there |
| `/update` | bring existing clones up to date (`git pull --ff-only`); only with `/unpinned` |
| `/unpinned` | ignore `payload.lock.json` and take the latest release or default branch |
| `/doppio-ref=<ref>` | build Doppio from this tag or branch instead of the pin |
| `/ristretto-ref=<ref>` | the same for Ristretto |

By default the bootstrap fetches Doppio and Ristretto at the commits in
[`payload.lock.json`](payload.lock.json) - the same ones the standalone release
is built from - so a self-built copy and a downloaded one contain the same
code. A clone at another commit is moved to the pin if it has no local
changes; a folder with local changes or of unknown origin is left alone and
the bootstrap stops and says so. Without git, it downloads a zip of exactly
that commit, and Ristretto's Doppio submodule at the commit Ristretto records.

### How the sources are fetched

`tools/fetch-from-github.ps1` is what actually goes and gets them, and it walks
down a ladder per repository rather than assuming `git clone` will work:

1. **`git clone --recurse-submodules`** — preferred, because it is the only rung
   that brings history and the Doppio submodule Ristretto links in.
2. **the latest release's source zip** — if the repository publishes releases.
3. **the default branch as a zip** from codeload — needs no git at all, which
   matters with `/nodeps` on a machine that never got git, and on a network
   that passes HTTPS GETs but not git's handshake.

Each rung says whether it worked, so a failure names what was tried instead of
just "could not get Doppio". Downloads draw a progress bar with percentage,
size and transfer rate — a source zip is tens of megabytes and a bare
"Downloading..." for forty seconds is indistinguishable from a hang. When the
output is redirected to a log the bar collapses to one final line instead of a
few thousand carriage returns.

A repository that is private, unpublished or renamed answers 404 to all three
rungs, anonymously, exactly as a missing one does. For **Ristretto** that is a
normal outcome and the bootstrap carries on with Doppio alone; for Doppio it is
fatal. A folder that is already there and not empty is never deleted or
overwritten — it may be your own checkout — it is reported and left alone.

Submodules are the one thing a zip cannot carry: GitHub source archives never
include them. Pinned, the bootstrap downloads Ristretto's Doppio submodule as a
second zip, at the commit `payload.lock.json` records for it. Unpinned, the
Doppio sources Ristretto links into `extern/doppio/` are copied out of the
Doppio folder next door.

`install-doppio.ps1` is fetched on its own if the Doppio checkout predates it —
the wizard's preflight blocks without it. It is looked for in `Doppio/` first
(version control wins), then beside the bootstrap, then on Doppio's default
branch. One file, not a re-clone: by that point Doppio may already be built,
and throwing that away for a 4 KB script means another MSVC run.

## What it does not do

Four things stay yours, three of them because they cannot be automated:

- **The VM snapshot.** A machine cannot snapshot itself; that is the host's
  job. Barista stops and reminds you at the point where it matters.
- **The code from your phone.** That is the second factor. Automating it would
  mean not having one.
- **The three risk checkboxes.** The only deliberately awkward screen. You are
  about to disable lsass protection and load an unsigned DLL into the logon
  path — that deserves one conscious confirmation.
- **The `RunAsPPL` decision.** Its own button, its own dialog. Turning it off
  makes the machine materially easier to loot and must never be a side effect
  of an install step.

## One change per reboot

Part 2 says it plainly:

> Change one thing per reboot. If you register both packages at once and the
> machine does not come back, you do not know which one did it.

So the install is cut into stages rather than run as one batch:

```
1. credential provider    → restart → "does the tile appear, next to the usual ones?"
2. enrollment + reindex      (no restart)
3. provider filter        → restart → "can you still get in, with only the tile?"
4. network policy         → restart → "did it come back? is the filter called?"
5. authentication package → restart → "did it come back?"
```

After each restart Barista opens again by itself and asks whether the machine
is healthy **before** running anything else. Answer no and it stops, tells you
what the last stage changed, and offers the matching uninstall script. At any
failure there is exactly one suspect.

Three things about that order.

Enrollment sits between the tile and any filter: the accounts a filter will
judge need a secret first, and `enroll /reindex` has to run before the filter
reads the RID index.

`install-doppio.ps1` is **two** script runs, not one, and stage 3 is the second
of them. Without `-EnableFilter` the tile is only ever offered *alongside* the
password tile — Part 1 puts it plainly: "without the filter you could choose
the normal password tile and skip the code." So stage 1 installs the tile and
stage 3 makes it the only option, with enrollment and a real sign-in test in
between. Stage 3 is also the only stage with a question *before* it runs:
whether you have actually signed in through the tile yet. No registry read can
answer that, and it is the last moment the password tile is there to catch you.

Stage 3 comes **before** the network stages, not after. If the tile turns out
to be broken, a network logon is still a way in; the other order means one bad
tile leaves no path at all.

### Surviving the reboot

Two independent mechanisms:

- `%ProgramData%\TheAdminCafe\Barista\pending-run.json` — the actual record.
  Paths, account names, which stages are done, the log so far. **No secrets**;
  those stay DPAPI-encrypted in Doppio's own store. Written after **every**
  finished stage, not only before a reboot: a stage with no reboot of its own
  (enrollment, the deny rights) was otherwise finished in memory and nowhere
  else, so closing the window after it lost the fact that it had run — and the
  next start then re-ran `enroll.exe` for an account that already had a secret.
  Only a pending reboot arms `RunOnce`, so nothing opens by itself unless
  something is actually waiting.
- `HKLM\...\CurrentVersion\RunOnce` — convenience, so the window opens after
  the next logon. It deletes its own value as it fires, so nothing lingers if
  you never continue.

The registry entry is not load-bearing. If it never fires, starting Barista by
hand finds the record and offers to continue. A resume that depends on one
registry value is a resume that silently does not happen.

## Enrolled and denied are two columns

The accounts page asks twice per account, because they are different questions:
give it a second factor, and close its non-interactive paths.

They have to be separable. The filter in stage 3 refuses to register while any
enabled local account has no secret — an account without one could not sign in
at all once the tile is the only option — so the break-glass admin *must* be
enrolled. And `deny-noninteractive.ps1` says in its own header to "keep a
second admin account that still works". Both at once only works if enrolling an
account does not also deny it: enrolled, so the tile accepts it; not denied, so
SMB, WinRM or RDP still get you in when the tile does not.

Denying is opt-in per account, and the page says so when a selection leaves
nothing that can reach the machine without the tile.

## It does not reimplement the install

Every registry write, every System32 copy and every LSA registration still
happens in Doppio's and Ristretto's own `install-*.ps1` scripts. Barista finds
them, explains what each one does, runs them in a defensible order and shows
the output.

That is deliberate. Those scripts already carry the guards — the `RunAsPPL`
check, the "Auth0 belongs to somebody else" check, the `PrevAuth0` bookkeeping,
the ordering that puts the DLL on disk before the registry names it.
Reimplementing that in C# would mean two copies of security-sensitive code
drifting apart, which is exactly the failure mode Ristretto's
`SECURITY-FIXES.md` flags for its duplicated registry reads.

Four things Barista does to the machine itself:

| Action | Why it is not in a script |
|---|---|
| Write `RunAsPPL` (and `RunAsPPLBoot`) | Not an install step. Its own dialog, and only offered when a component that loads into lsass was actually chosen. Both values, because every install script gates on both — clearing one and leaving the other means giving up the protection and staying blocked anyway. |
| Launch `enroll.exe` | In its own console window, because the `y/N` prompts use `_getwch()`, which reads the console directly and ignores redirected stdin. |
| Write the pending-run record and `RunOnce` | So a staged install survives its reboots. |
| `reg import unregister.reg` | The only way back out of the credential provider is one of Doppio's own `.reg` files, and `powershell -File` cannot run a `.reg`. The keys and their deletion order still live in that file, not here. |

The recovery path is built from what is **registered**, not from what was
chosen: the Auth0 value, the LSA package list, and the provider and filter
CLSIDs are all read before anything is offered. Those stop agreeing with the
plan the moment a stage fails, which is exactly when the recovery button gets
pressed.

## Building just the wizard

```bat
cd src
dotnet publish -c Release -r win-x64 --self-contained true ^
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true ^
    -p:EnableCompressionInSingleFile=true -o ..\wizard-out
```

Needs the **.NET 10 SDK** — not the same toolchain as Doppio and Ristretto,
which need MSVC. The wizard project pulls in `core/Barista.Core.csproj`, so
publishing `src/` builds both. Visual Studio with the C++ workload does not include it.

One NuGet dependency: **QRCoder** (MIT, pure managed). It renders the
`otpauth://` URI as a QR code in-process, so nobody has to paste a TOTP secret
into an online generator.

`-o` pins the output folder on purpose: a csproj carrying
`<Platforms>x64</Platforms>` publishes to `bin\x64\Release\...` while one
without it uses `bin\Release\...`, and guessing wrong makes a successful build
look like a failed one.

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

The split is not cosmetic. `core/` has no WPF in it, so the stage plan and the
stage runner - the parts where a mistake locks someone out of a machine - can be
tested without a desktop. They used to live in the install page's code-behind,
wrapped around `MessageBox` calls, which meant the rules they enforce (the
plan's order, stop at the first failed step, every finished stage written to
disk, `RunOnce` armed only when a reboot is actually pending) were asserted by
comments and by whoever last read them. `tests/` asserts them now, and CI runs
it on every push.

The dialogs stayed in `src/`: a confirmation is the one part that genuinely
needs a person looking at a screen.

```bat
dotnet test tests\Barista.Core.Tests\Barista.Core.Tests.csproj
```

## Still open

- No unattended / config-file mode. Worth adding if you rebuild lab VMs often.
- No live capture of the filter's `OutputDebugStringW` output. A
  `DBWIN_BUFFER` reader would answer "is MSV1_0 actually calling it?" without
  WinDbg, but lsass is in session 0 and it needs verifying that
  `Global\DBWIN_BUFFER` reaches it.
- Enrollment still opens a console window. A fresh enrollment could be driven
  over pipes — `ConfirmSecret` reads with `fgetws`, which honours redirected
  stdin — leaving only the re-enrollment prompt needing a real console.

## Licence

MIT, same as Doppio and Ristretto. See `LICENSE`.
