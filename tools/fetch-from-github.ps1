<#
.SYNOPSIS
    Fetches a repository, or a single file out of one, from GitHub.

.DESCRIPTION
    bootstrap.bat used to have exactly one way to get Doppio and Ristretto:
    `git clone`. That is fine on a machine with git and a public repository,
    and it is the only path that gives you history and submodules - so it is
    still tried first. But it is not the only situation:

      - git is not installed and /nodeps was passed, so nothing will install it
      - the repository publishes releases and a tagged release is what you want
        on a lab VM, not the tip of the default branch
      - a corporate proxy passes HTTPS GETs but not the git protocol handshake

    So this walks down a ladder and reports which rung worked:

      1. git clone                     (preferred: history + submodules)
      2. latest release source zip     (if the repo has published releases)
      3. default-branch zip            (codeload, no git needed)

    A repository that answers 404 to all three is "not available", which is a
    normal outcome for Ristretto - it may not be published - and not an error.

    -File fetches a single file from the default branch instead of the whole
    repository. That is for install-doppio.ps1: the wizard needs it, it lives
    in Doppio, and an older clone sitting beside the bootstrap may predate it.

.NOTES
    No authentication. Everything here is a public, anonymous GET; a private
    repository answers 404 the same way a missing one does, which is exactly
    how the caller wants to treat it.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $Owner,
    [Parameter(Mandatory)] [string] $Repo,

    # Where the repository should end up, or - with -File - the folder the
    # single file is written into.
    [Parameter(Mandatory)] [string] $Dest,

    # A file that must exist inside $Dest afterwards for the fetch to count as
    # successful. A zip that unpacked into the wrong nesting level, or a
    # repository whose layout changed, is otherwise "downloaded fine" and then
    # fails much later in the build with something unrelated.
    [string] $Marker,

    # Fetch this one file out of the default branch instead of the repository.
    [string] $File,

    # Prefer this ref over releases and the default branch.
    [string] $Ref,

    # Skip the release ladder rung and take the default branch.
    [switch] $NoRelease,

    # An existing git clone is brought up to date instead of left alone.
    [switch] $Update,

    # Pinned mode: take the commit for this repository from payload.lock.json
    # (the entry named by -LockKey: doppio or ristretto) and accept nothing
    # else. This is what the standalone release is built from, so a
    # self-built copy ends up with exactly the same sources. Overrides -Ref,
    # releases and branches; -Update then means "move to the pin".
    [string] $LockFile,
    [string] $LockKey,

    # Where source archives come from. Only ever changed to point at a mirror,
    # or at a local test server standing in for GitHub - the pinned zip path
    # is otherwise untestable on a machine that has git.
    [string] $ArchiveBase = 'https://codeload.github.com'
)

$ErrorActionPreference = 'Stop'
# Version 1.0, deliberately: 2.0 throws when a property does not exist, and
# the whole point of reading `default_branch` or `zipball_url` off an answer
# GitHub may not have given is to find out whether it is there.
Set-StrictMode -Version 1.0

# Windows PowerShell 5.1 still negotiates TLS 1.0 by default on some images,
# and github.com refuses that. The symptom is "could not create SSL/TLS secure
# channel", which reads like a certificate problem and is not one.
try {
    [Net.ServicePointManager]::SecurityProtocol =
        [Net.SecurityProtocolType]::Tls12 -bor [Net.ServicePointManager]::SecurityProtocol
} catch {
    # Pre-4.5 enum values; nothing to do but carry on and let the GET fail
    # with its own message.
}

# Invoke-WebRequest's own band; the hand-drawn bar below replaces it.
$ProgressPreference = 'SilentlyContinue'

# True when stdout is a file or a pipe rather than a console. A carriage-return
# progress line is unreadable there, so the bar collapses to one final line.
$script:Quiet = $true
try { $script:Quiet = [Console]::IsOutputRedirected } catch { $script:Quiet = $false }

function Write-Step([string] $Message) { Write-Host "      $Message" }

function Invoke-Api([string] $Url) {
    # Returns $null for 404 and for a rate-limited answer, because every
    # caller treats "cannot tell" and "not there" the same way: drop to the
    # next rung of the ladder.
    try {
        return Invoke-RestMethod -Uri $Url -Headers @{
            'Accept'     = 'application/vnd.github+json'
            'User-Agent' = 'barista-bootstrap'
        } -TimeoutSec 30
    } catch {
        return $null
    }
}

function Format-Size([double] $Bytes) {
    if ($Bytes -lt 1KB)  { return ("{0:N0} B"  -f $Bytes) }
    if ($Bytes -lt 1MB)  { return ("{0:N0} KB" -f ($Bytes / 1KB)) }
    if ($Bytes -lt 1GB)  { return ("{0:N1} MB" -f ($Bytes / 1MB)) }
    return ("{0:N2} GB" -f ($Bytes / 1GB))
}

function Write-Bar([long] $Done, [long] $Total, [double] $Seconds, [switch] $Final) {
    # Drawn by hand rather than with Write-Progress. Write-Progress paints
    # into a reserved band at the top of the host window, which a console
    # launched from bootstrap.bat scrolls away from, and it draws nothing at
    # all when the output is redirected to a log. This is one carriage-return
    # line in the normal flow of the bootstrap's output, right under the
    # [src] line that says which repository is being fetched.
    $rate = if ($Seconds -gt 0.5) { " at " + (Format-Size ($Done / $Seconds)) + "/s" } else { "" }

    if ($Total -gt 0) {
        $fraction = [Math]::Min(1.0, $Done / [double] $Total)
        $width = 28
        $filled = [int] [Math]::Round($fraction * $width)

        # Full blocks and a shaded remainder. Both are in code page 437 and
        # 850 as well as in UTF-8, so they survive a console that never got
        # chcp'd - which a double-clicked .bat does not.
        $bar = ([string][char]0x2588) * $filled + ([string][char]0x2591) * ($width - $filled)
        $text = "      [{0}] {1,3:N0}%  {2} / {3}{4}" -f `
            $bar, ($fraction * 100), (Format-Size $Done), (Format-Size $Total), $rate
    } else {
        # No Content-Length. codeload generates source archives on the fly and
        # streams them, so this is the normal case for a branch zip, not an
        # oddity: show what has arrived instead of a percentage of nothing.
        $text = "      {0} received{1}" -f (Format-Size $Done), $rate
    }

    if ($script:Quiet) {
        # Redirected to a file: a carriage-return line would be one very long
        # line of overwrites. Only the last one is worth keeping.
        if ($Final) { Write-Host $text.TrimEnd() }
        return
    }

    # Pad to clear whatever the previous, possibly longer, line left behind.
    Write-Host ("`r" + $text.PadRight(78)) -NoNewline
    if ($Final) { Write-Host "" }
}

function Get-ToTempFile([string] $Url, [string] $Extension) {
    $tmp = Join-Path ([IO.Path]::GetTempPath()) ("barista-" + [Guid]::NewGuid().ToString('n') + $Extension)

    $request = [Net.HttpWebRequest]::Create($Url)
    $request.UserAgent = 'barista-bootstrap'
    $request.Timeout = 60000           # to the first byte
    $request.ReadWriteTimeout = 300000 # between bytes, for a slow link
    $request.AllowAutoRedirect = $true # api.github.com's zipball_url is a 302

    $response = $null
    $stream = $null
    $file = $null
    try {
        $response = $request.GetResponse()
        $total = [long] $response.ContentLength   # -1 when the server streams
        $stream = $response.GetResponseStream()
        $file = [IO.File]::Create($tmp)

        $buffer = New-Object byte[] 262144
        $done = [long] 0
        $clock = [Diagnostics.Stopwatch]::StartNew()
        $lastPaint = [double] -1

        while (($read = $stream.Read($buffer, 0, $buffer.Length)) -gt 0) {
            $file.Write($buffer, 0, $read)
            $done += $read

            # Repainting on every 256 KB chunk is thousands of writes on a
            # large archive, and on a slow console that costs more time than
            # the download. Ten a second is smooth and free.
            $now = $clock.Elapsed.TotalSeconds
            if (($now - $lastPaint) -ge 0.1) {
                $lastPaint = $now
                Write-Bar $done $total $now
            }
        }

        $clock.Stop()
        Write-Bar $done $total $clock.Elapsed.TotalSeconds -Final
        return $tmp
    } catch {
        # The partially written file is useless and would otherwise sit in
        # %TEMP% until the next reboot.
        if ($file) { $file.Dispose(); $file = $null }
        Remove-Item -LiteralPath $tmp -Force -ErrorAction SilentlyContinue
        throw
    } finally {
        # Close() and not Dispose() on the last two: WebResponse and the
        # response stream implement IDisposable explicitly, and PowerShell
        # cannot call an explicit interface member - it would fail with
        # "method not found" inside the finally block, which replaces whatever
        # real error brought us here.
        if ($file)     { $file.Dispose() }
        if ($stream)   { $stream.Close() }
        if ($response) { $response.Close() }
    }
}

function Expand-ZipIntoDest([string] $Zip, [string] $Destination) {
    # GitHub source zips contain exactly one top-level folder, named
    # <repo>-<ref-or-sha>. Unpacking the zip straight into $Destination would
    # give Doppio\Doppio-main\build.bat, which every later path in the
    # bootstrap and in the wizard would then miss. So unpack beside it and
    # move that single folder's *contents* up.
    $staging = Join-Path ([IO.Path]::GetTempPath()) ("barista-x-" + [Guid]::NewGuid().ToString('n'))
    New-Item -ItemType Directory -Path $staging -Force | Out-Null
    try {
        Expand-Archive -LiteralPath $Zip -DestinationPath $staging -Force

        $roots = @(Get-ChildItem -LiteralPath $staging)
        $source = if ($roots.Count -eq 1 -and $roots[0].PSIsContainer) { $roots[0].FullName } else { $staging }

        New-Item -ItemType Directory -Path $Destination -Force | Out-Null
        Get-ChildItem -LiteralPath $source -Force |
            Move-Item -Destination $Destination -Force
    } finally {
        Remove-Item -LiteralPath $staging -Recurse -Force -ErrorAction SilentlyContinue
    }
}

function Get-DefaultBranch() {
    $info = Invoke-Api "https://api.github.com/repos/$Owner/$Repo"
    if ($info -and $info.default_branch) { return $info.default_branch }

    # The API is unreachable or rate-limited. Both common default names are
    # tried by the caller rather than guessing one here.
    return $null
}

# ---------------------------------------------------------------------------
#  -File: one file out of the default branch
# ---------------------------------------------------------------------------

if ($File) {
    $branches = @()
    if ($Ref) { $branches += $Ref }
    $detected = Get-DefaultBranch
    if ($detected) { $branches += $detected } else { $branches += @('main', 'master') }

    foreach ($branch in ($branches | Select-Object -Unique)) {
        $url = "https://raw.githubusercontent.com/$Owner/$Repo/$branch/$File"
        try {
            $target = Join-Path $Dest $File
            New-Item -ItemType Directory -Path (Split-Path -Parent $target) -Force | Out-Null

            # Through the same helper as the archives, so a 4 KB script and a
            # 40 MB zip report the same way. Downloaded to a temp file first:
            # a failed GET must not truncate a copy that is already in the
            # Doppio folder.
            $tmp = Get-ToTempFile $url ([IO.Path]::GetExtension($File))
            Move-Item -LiteralPath $tmp -Destination $target -Force
            Write-Step "$File fetched from $Owner/$Repo@$branch"
            exit 0
        } catch {
            continue
        }
    }

    Write-Step "$File is not in $Owner/$Repo on GitHub."
    exit 1
}

# ---------------------------------------------------------------------------
#  Pinned: exactly one commit, from payload.lock.json
# ---------------------------------------------------------------------------
#
# Exit codes: 0 = at the pin, 1 = could not be fetched at all (for Ristretto
# the ordinary "not available"), 2 = something is here that is NOT the pin and
# was left alone. The bootstrap stops on 2 for either repository: building a
# different commit than the release while believing it is the same one is the
# thing pinning exists to prevent.

if ($LockFile -and $LockKey) {
    $lock = Get-Content -LiteralPath $LockFile -Raw | ConvertFrom-Json
    $entry = $lock.$LockKey
    if (-not $entry -or $entry.commit -notmatch '^[0-9a-f]{40}$') {
        Write-Step "payload.lock.json has no full commit id for '$LockKey'."
        exit 2
    }
    $Commit = $entry.commit
    $Sub = $entry.doppioSubmodule      # Ristretto only; $null otherwise

    $haveGit = [bool] (Get-Command git -ErrorAction SilentlyContinue)
    $pinFile = '.barista-pin'           # what a zip download records instead of .git

    function Get-Pin([string] $Dir) {
        if (Test-Path -LiteralPath (Join-Path $Dir '.git')) {
            if (-not $haveGit) { return $null }
            $h = (& git -C $Dir rev-parse HEAD 2>$null)
            if ($h) { return $h.Trim() } else { return $null }
        }
        $f = Join-Path $Dir $pinFile
        if (Test-Path -LiteralPath $f) { return (Get-Content -LiteralPath $f -Raw).Trim() }
        return $null
    }

    function Test-Clean([string] $Dir) {
        $dirty = (& git -C $Dir status --porcelain --untracked-files=no)
        return -not $dirty
    }

    # Clone or fetch, then a detached checkout of the pin. Fetching the sha
    # itself as a fallback covers a commit that is no longer on any branch
    # head; GitHub serves any reachable commit by id.
    function Set-GitPin([string] $Dir, [string] $Sha) {
        & git -C $Dir checkout --quiet --detach $Sha 2>$null
        if ($LASTEXITCODE -ne 0) {
            & git -C $Dir fetch --quiet origin $Sha
            & git -C $Dir checkout --quiet --detach $Sha
        }
        if ($LASTEXITCODE -ne 0) { return $false }
        & git -C $Dir submodule update --init --recursive --quiet
        return ((Get-Pin $Dir) -eq $Sha)
    }

    # A zip of one commit. codeload serves /zip/<sha> for any commit, and the
    # sha in the URL is what pins it - a branch zip is a moving target.
    function Get-ZipPin([string] $Owner_, [string] $Repo_, [string] $Sha, [string] $Into) {
        $zip = $null
        try {
            Write-Step "downloading $Repo_ at $($Sha.Substring(0, 7)) as a zip..."
            $zip = Get-ToTempFile "$ArchiveBase/$Owner_/$Repo_/zip/$Sha" '.zip'
            Expand-ZipIntoDest $zip $Into
            Set-Content -LiteralPath (Join-Path $Into $pinFile) -Value $Sha -Encoding Ascii
            return $true
        } catch {
            Write-Step "download failed: $($_.Exception.Message)"
            return $false
        } finally {
            if ($zip) { Remove-Item -LiteralPath $zip -Force -ErrorAction SilentlyContinue }
        }
    }

    # Ristretto's Doppio submodule, for a Ristretto that arrived as a zip and
    # so has an empty extern\doppio. At the commit Ristretto records, which is
    # not necessarily the Doppio pin - copying the Doppio folder next door
    # across, as the unpinned path does, would build something the release
    # was not built from.
    function Add-ZipSubmodule([string] $Dir) {
        if (-not $Sub) { return $true }
        $target = Join-Path (Join-Path $Dir 'extern') 'doppio'
        if ((Get-Pin $target) -eq $Sub.commit) { return $true }
        if (Test-Path -LiteralPath $target) {
            if (@(Get-ChildItem -LiteralPath $target -Force).Count -gt 0) {
                Write-Step "$target holds something other than the pinned Doppio sources - leaving it alone."
                return $false
            }
        }
        $parts = ($Sub.repository -replace '\.git$', '').TrimEnd('/') -split '/'
        return (Get-ZipPin $parts[-2] $parts[-1] $Sub.commit $target)
    }

    Write-Step "pinned to $Commit (payload.lock.json)"

    # --- already here ------------------------------------------------------
    if (Test-Path -LiteralPath $Dest) {
        $now = Get-Pin $Dest
        $isGit = Test-Path -LiteralPath (Join-Path $Dest '.git')

        if ($now -eq $Commit) {
            if ($isGit) { & git -C $Dest submodule update --init --recursive --quiet }
            elseif (-not (Add-ZipSubmodule $Dest)) { exit 2 }
            Write-Step "$Repo is already at the pin."
            exit 0
        }

        if ($isGit -and $haveGit) {
            # A clone at some other commit. Moved to the pin only when that
            # throws nothing away: local edits are somebody's work, and a pinned
            # build is not a reason to discard them.
            if (-not (Test-Clean $Dest)) {
                Write-Step "$Repo has local changes and is at $now, not the pin. Leaving it alone."
                exit 2
            }
            Write-Step "moving the existing $Repo clone to the pin..."
            & git -C $Dest fetch --quiet origin
            if (Set-GitPin $Dest $Commit) { Write-Step "$Repo is at the pin."; exit 0 }
            Write-Step "could not check out the pin in the existing $Repo clone."
            exit 2
        }

        if (@(Get-ChildItem -LiteralPath $Dest -Force).Count -gt 0) {
            $what = if ($now) { "commit $now" } else { 'an unknown version' }
            Write-Step "a folder called $Repo is here with $what, not the pin. Leaving it alone."
            Write-Step "Rename or delete it to have the pinned version fetched, or run with /unpinned."
            exit 2
        }
        Remove-Item -LiteralPath $Dest -Force -ErrorAction SilentlyContinue
    }

    # --- fresh: clone ------------------------------------------------------
    if ($haveGit) {
        $env:GIT_TERMINAL_PROMPT = '0'
        $env:GCM_INTERACTIVE = 'never'
        Write-Step "cloning $Owner/$Repo..."
        & git clone --progress "https://github.com/$Owner/$Repo" $Dest
        if ((Test-Path -LiteralPath (Join-Path $Dest '.git')) -and (Set-GitPin $Dest $Commit)) {
            Write-Step "cloned $Owner/$Repo at the pin"
            exit 0
        }
        if (Test-Path -LiteralPath $Dest) {
            Remove-Item -LiteralPath $Dest -Recurse -Force -ErrorAction SilentlyContinue
        }
        Write-Step "clone at the pin did not work; trying a download instead."
    }

    # --- fresh: zip of the commit -----------------------------------------
    $hasMarker = { -not $Marker -or (Test-Path -LiteralPath (Join-Path $Dest $Marker)) }
    if ((Get-ZipPin $Owner $Repo $Commit $Dest) -and (& $hasMarker) -and (Add-ZipSubmodule $Dest)) {
        Write-Step "unpacked $Repo at the pin"
        exit 0
    }
    if (Test-Path -LiteralPath $Dest) {
        Remove-Item -LiteralPath $Dest -Recurse -Force -ErrorAction SilentlyContinue
    }
    Write-Step "$Owner/$Repo could not be fetched at the pin."
    exit 1
}

# ---------------------------------------------------------------------------
#  Whole repository
# ---------------------------------------------------------------------------

function Test-Marker() {
    if (-not $Marker) { return (Test-Path -LiteralPath $Dest) }
    return (Test-Path -LiteralPath (Join-Path $Dest $Marker))
}

# Already here and complete.
if (Test-Marker) {
    if ($Update -and (Test-Path -LiteralPath (Join-Path $Dest '.git'))) {
        Write-Step "updating the existing $Repo clone..."
        & git -C $Dest pull --ff-only --quiet
        if ($LASTEXITCODE -ne 0) {
            # A dirty working tree or a local branch is the user's business,
            # not something to force past. The checkout they have still
            # builds, so this is a note and not a failure.
            Write-Step "could not fast-forward $Repo - leaving the checkout as it is."
        }
    }
    exit 0
}

# Here but incomplete: a half-finished download, or a folder the user made.
# Never delete it - that folder may be their own work-in-progress checkout.
if (Test-Path -LiteralPath $Dest) {
    $contents = @(Get-ChildItem -LiteralPath $Dest -Force)
    if ($contents.Count -gt 0) {
        Write-Step "a folder called $Repo is here but does not contain $Marker - leaving it alone."
        exit 1
    }
    Remove-Item -LiteralPath $Dest -Force -ErrorAction SilentlyContinue
}

$haveGit = [bool] (Get-Command git -ErrorAction SilentlyContinue)

# --- rung 1: git clone ------------------------------------------------------
if ($haveGit) {
    Write-Step "cloning $Owner/$Repo..."

    # GIT_TERMINAL_PROMPT=0 is also set by the caller. Repeated here because
    # this script is runnable on its own, and without it a private or missing
    # repository makes git stop and ask for a username - which inside a
    # bootstrap looks indistinguishable from a hang.
    $env:GIT_TERMINAL_PROMPT = '0'
    $env:GCM_INTERACTIVE = 'never'

    # Not $args - that is an automatic variable inside a function and
    # assigning to it is a quiet way to break the parameter binding.
    # --progress, not --quiet: a clone of Doppio plus its submodule is the
    # longest single step in the bootstrap, and git's own counter is better
    # than anything reconstructible from the outside. It writes to stderr.
    $gitArgs = @('clone', '--progress', '--recurse-submodules')
    if ($Ref) { $gitArgs += @('--branch', $Ref) }
    $gitArgs += @("https://github.com/$Owner/$Repo", $Dest)

    & git @gitArgs
    if (Test-Marker) {
        Write-Step "cloned $Owner/$Repo"
        exit 0
    }

    # A clone that got part of the way leaves a folder behind that would make
    # the zip rungs below refuse to write.
    if (Test-Path -LiteralPath $Dest) {
        Remove-Item -LiteralPath $Dest -Recurse -Force -ErrorAction SilentlyContinue
    }
    Write-Step "clone did not produce $Marker; trying a download instead."
}

# --- rung 2: latest release ------------------------------------------------
if (-not $NoRelease -and -not $Ref) {
    $release = Invoke-Api "https://api.github.com/repos/$Owner/$Repo/releases/latest"
    if ($release -and $release.zipball_url) {
        Write-Step "downloading release $($release.tag_name) of $Repo..."
        $zip = $null
        try {
            $zip = Get-ToTempFile $release.zipball_url '.zip'
            Expand-ZipIntoDest $zip $Dest
            if (Test-Marker) {
                Write-Step "unpacked $Repo $($release.tag_name)"
                exit 0
            }
            Write-Step "the release zip does not contain $Marker; trying the default branch."
            Remove-Item -LiteralPath $Dest -Recurse -Force -ErrorAction SilentlyContinue
        } catch {
            Write-Step "release download failed: $($_.Exception.Message)"
            Remove-Item -LiteralPath $Dest -Recurse -Force -ErrorAction SilentlyContinue
        } finally {
            if ($zip) { Remove-Item -LiteralPath $zip -Force -ErrorAction SilentlyContinue }
        }
    }
}

# --- rung 3: default-branch zip -------------------------------------------
$branches = @()
if ($Ref) { $branches += $Ref }
$detected = Get-DefaultBranch
if ($detected) { $branches += $detected } else { $branches += @('main', 'master') }

foreach ($branch in ($branches | Select-Object -Unique)) {
    Write-Step "downloading $Repo ($branch) as a zip..."
    $zip = $null
    try {
        $zip = Get-ToTempFile "$ArchiveBase/$Owner/$Repo/zip/refs/heads/$branch" '.zip'
        Expand-ZipIntoDest $zip $Dest
        if (Test-Marker) {
            Write-Step "unpacked $Repo ($branch)"
            # A zip is not a git repository, so submodule contents are not in
            # it - GitHub source archives never carry them. The caller handles
            # that for Ristretto by copying the Doppio sources across, and
            # saying it here keeps that from looking like a surprise.
            Write-Step "note: a zip carries no submodules, so extern\ is filled in by the caller."

            exit 0
        }
        Write-Step "that zip does not contain $Marker."
    } catch {
        # 404 on both branch names is the ordinary "no such public repo".
    } finally {
        if ($zip) { Remove-Item -LiteralPath $zip -Force -ErrorAction SilentlyContinue }
        if (-not (Test-Marker) -and (Test-Path -LiteralPath $Dest)) {
            Remove-Item -LiteralPath $Dest -Recurse -Force -ErrorAction SilentlyContinue
        }
    }
}

Write-Step "$Owner/$Repo could not be fetched from GitHub."
exit 1
