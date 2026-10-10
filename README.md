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

## Use it

```bat
git clone https://github.com/Nobrac/Barista
cd Barista
bootstrap.bat
```

That is the whole thing. It elevates itself, installs git, the .NET 10 SDK and
the MSVC build tools through winget if they are missing, clones Doppio and
Ristretto beside itself, builds all three, and opens the wizard.

Flags: `/nodeps` installs nothing through winget, `/noclone` uses whatever
folders are already there.

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
1. credential provider    → restart → "does the tile work?"
2. enrollment + reindex      (no restart)
3. network policy         → restart → "did it come back? is the filter called?"
4. authentication package → restart → "did it come back?"
```

After each restart Barista opens again by itself and asks whether the machine
is healthy **before** running anything else. Answer no and it stops, tells you
what the last stage changed, and offers the matching uninstall script. At any
failure there is exactly one suspect.

Enrollment deliberately sits between the tile and any filter: the accounts a
filter will judge need a secret first, and `enroll /reindex` has to run before
the filter reads the RID index.

### Surviving the reboot

Two independent mechanisms:

- `%ProgramData%\TheAdminCafe\Barista\pending-run.json` — the actual record.
  Paths, account names, which stages are done, the log so far. **No secrets**;
  those stay DPAPI-encrypted in Doppio's own store.
- `HKLM\...\CurrentVersion\RunOnce` — convenience, so the window opens after
  the next logon. It deletes its own value as it fires, so nothing lingers if
  you never continue.

The registry entry is not load-bearing. If it never fires, starting Barista by
hand finds the record and offers to continue. A resume that depends on one
registry value is a resume that silently does not happen.

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

Three things Barista does to the machine itself:

| Action | Why it is not in a script |
|---|---|
| Write `RunAsPPL` | Not an install step. Its own dialog, and only offered when a component that loads into lsass was actually chosen. |
| Launch `enroll.exe` | In its own console window, because the `y/N` prompts use `_getwch()`, which reads the console directly and ignores redirected stdin. |
| Write the pending-run record and `RunOnce` | So a staged install survives its reboots. |

## Building just the wizard

```bat
cd src
dotnet publish -c Release -r win-x64 --self-contained true ^
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true ^
    -p:EnableCompressionInSingleFile=true -o ..\wizard-out
```

Needs the **.NET 10 SDK** — not the same toolchain as Doppio and Ristretto,
which need MSVC. Visual Studio with the C++ workload does not include it.

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
src/                   the WPF wizard
  Services/            preflight checks, the stage plan, the pending-run record
  Views/               one file per wizard page
Doppio/                cloned by bootstrap, gitignored
Ristretto/             cloned by bootstrap, gitignored
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
