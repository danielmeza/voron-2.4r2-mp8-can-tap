#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Bring this Voron's MCU firmware up to match the Klipper host version.

.DESCRIPTION
    Klipper logs 'deprecated_mcu_code' warnings whenever MCU firmware is older than
    the host. Moonraker's "update all" updates host software only -- it has no
    concept of flashing microcontrollers -- so this has to run separately.

    The script is IDEMPOTENT and VERSION-AWARE: it queries each MCU, compares against
    the host, and flashes only what actually mismatches. Running it when everything
    already matches is a no-op, so it is safe to run after every "update all".

    Board order is deliberate: Linux MCU, then EBB, then Manta LAST -- the Manta is
    the USB-to-CAN bridge, so reflashing it drops the whole bus including the EBB.

    Katapult lives below the application offset on both CAN boards and is never
    overwritten, so a failed application flash is retryable rather than a brick.

.PARAMETER PrinterHost
    Hostname/IP for Moonraker's HTTP API.

.PARAMETER SshTarget
    SSH destination. Use a ~/.ssh/config alias so keys/user are handled there.

.PARAMETER RootSshTarget
    Root SSH destination, used only to install the Linux MCU binary into
    /usr/local/bin (the one step that genuinely needs root). If this is not
    reachable the script stages the binary and prints the manual commands instead.

.PARAMETER ConfigDir
    Directory holding the per-board Klipper .config files (repo's firmware/).

.PARAMETER Boards
    Which boards to consider: ebb, manta, linux, or all. Default is ebb+manta;
    'linux' needs root on the printer and is opt-in.

.PARAMETER Force
    Flash even when the version already matches.

.PARAMETER DryRun
    Report what would happen; make no changes.

.EXAMPLE
    ./Update-VoronMcuFirmware.ps1 -DryRun

.EXAMPLE
    ./Update-VoronMcuFirmware.ps1 -Boards ebb,manta

.NOTES
    Requires: pwsh 7+, an ssh client, key-based SSH to the printer.
    See firmware/README.md for the manual equivalent and the gotchas.
#>
[CmdletBinding()]
param(
    [string]   $PrinterHost   = '192.168.68.69',
    [string]   $SshTarget     = 'voron',
    [string]   $RootSshTarget = 'voron-root',
    [string]   $ConfigDir   = (Join-Path $PSScriptRoot '..' 'firmware'),
    [ValidateSet('ebb', 'manta', 'linux', 'all')]
    [string[]] $Boards      = @('ebb', 'manta'),
    [switch]   $Force,
    [switch]   $DryRun
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# --------------------------------------------------------------------------- #
# Board definitions
#   Order in this array IS the flash order. Manta must remain last.
# --------------------------------------------------------------------------- #
$BoardSpecs = @(
    [pscustomobject]@{
        Key        = 'linux'
        McuName    = 'mcu'
        Label      = 'CB1 host (Linux MCU)'
        ConfigFile = 'cb1-linux-mcu.config'
        Artifact   = 'out/klipper.elf'
        Uuid       = $null
        NeedsRoot  = $true
    }
    [pscustomobject]@{
        Key        = 'ebb'
        McuName    = 'mcu EBB'
        Label      = 'EBB SB2209 (RP2040)'
        ConfigFile = 'ebb-sb2209-rp2040.config'
        Artifact   = 'out/klipper.bin'
        Uuid       = '19de38651b72'
        NeedsRoot  = $false
    }
    [pscustomobject]@{
        Key        = 'manta'
        McuName    = 'mcu MP8'
        Label      = 'Manta M8P v2.0 (STM32H723) -- USB-CAN BRIDGE'
        ConfigFile = 'manta-m8p-h723.config'
        Artifact   = 'out/klipper.bin'
        Uuid       = 'f24b112c272e'
        NeedsRoot  = $false
    }
)

# --------------------------------------------------------------------------- #
# Helpers
# --------------------------------------------------------------------------- #
function Write-Step { param([string]$Message) Write-Host "`n==> $Message" -ForegroundColor Cyan }
function Write-Ok   { param([string]$Message) Write-Host "    [ok]   $Message" -ForegroundColor Green }
function Write-Warn { param([string]$Message) Write-Host "    [warn] $Message" -ForegroundColor Yellow }
function Write-Err  { param([string]$Message) Write-Host "    [FAIL] $Message" -ForegroundColor Red }
function Write-Info { param([string]$Message) Write-Host "    $Message" }

function Invoke-Moonraker {
    <# Call Moonraker's HTTP API. #>
    param(
        [Parameter(Mandatory)][string] $Path,
        [ValidateSet('GET', 'POST')][string] $Method = 'GET',
        [int] $TimeoutSec = 20
    )
    $uri = "http://$PrinterHost$Path"
    try {
        return Invoke-RestMethod -Uri $uri -Method $Method -TimeoutSec $TimeoutSec
    } catch {
        throw "Moonraker $Method $Path failed: $($_.Exception.Message)"
    }
}

function Invoke-Ssh {
    <# Run a bash snippet on the printer. Returns combined output; throws on non-zero. #>
    param(
        [Parameter(Mandatory)][string] $Script,
        [switch] $AllowFailure,
        [switch] $AsRoot
    )
    $target = if ($AsRoot) { $RootSshTarget } else { $SshTarget }
    $output = $Script | & ssh -o BatchMode=yes -o ConnectTimeout=10 $target 'bash -s' 2>&1
    $code = $LASTEXITCODE
    if ($code -ne 0 -and -not $AllowFailure) {
        throw "Remote command failed (exit $code):`n$($output -join "`n")"
    }
    return ($output -join "`n")
}

function Test-RootSsh {
    <# Is key-based root available? Determines whether the Linux MCU can be installed. #>
    try {
        $out = Invoke-Ssh -Script 'id -u' -AsRoot -AllowFailure
        return ($out.Trim() -eq '0')
    } catch { return $false }
}

function Install-LinuxMcu {
    <#
      Replicates klipper's scripts/flash-linux.sh, which is all 'make flash' does for
      a Linux MCU target: stop services, copy the ELF to /usr/local/bin/klipper_mcu,
      restart. Backs up the outgoing binary so the change is reversible.
    #>
    $script = @'
set -e
STAGED=/home/biqu/klipper-fw-backups/klipper_mcu-staged.elf
[ -f "$STAGED" ] || { echo "STAGED_BINARY_MISSING"; exit 1; }
mkdir -p /home/biqu/klipper-fw-backups
if [ -f /usr/local/bin/klipper_mcu ] && [ ! -f /home/biqu/klipper-fw-backups/klipper_mcu-PREVIOUS.elf ]; then
    cp -a /usr/local/bin/klipper_mcu /home/biqu/klipper-fw-backups/klipper_mcu-PREVIOUS.elf
fi
systemctl stop klipper
systemctl stop klipper-mcu
sleep 2
rm -f /usr/local/bin/klipper_mcu
cp "$STAGED" /usr/local/bin/klipper_mcu
chown root:root /usr/local/bin/klipper_mcu
chmod 755 /usr/local/bin/klipper_mcu
sync
systemctl start klipper-mcu
sleep 3
systemctl start klipper
echo "INSTALL_OK"
'@
    $out = Invoke-Ssh -Script $script -AsRoot
    if ($out -match 'STAGED_BINARY_MISSING') { throw 'Staged Linux MCU binary not found on the printer.' }
    if ($out -notmatch 'INSTALL_OK')         { throw "Linux MCU install did not complete:`n$out" }
}

function Get-PrinterFacts {
    <# Host version, per-MCU versions, job state, warning count. #>
    $info = Invoke-Moonraker -Path '/printer/info'
    $q = Invoke-Moonraker -Path '/printer/objects/query?configfile&print_stats&mcu&mcu%20EBB&mcu%20MP8'
    $status = $q.result.status

    $mcuVersions = @{}
    foreach ($spec in $BoardSpecs) {
        $node = $status.PSObject.Properties[$spec.McuName]
        $mcuVersions[$spec.Key] = if ($node) { $node.Value.mcu_version } else { $null }
    }

    $warnings = @($status.configfile.warnings)
    [pscustomobject]@{
        HostVersion  = $info.result.software_version
        McuVersions  = $mcuVersions
        JobState     = $status.print_stats.state
        WarningCount = $warnings.Count
        DeprecatedCount = @($warnings | Where-Object { $_.type -eq 'deprecated_mcu_code' }).Count
    }
}

function Wait-KlipperReady {
    param([int] $TimeoutSec = 150)
    $deadline = (Get-Date).AddSeconds($TimeoutSec)
    while ((Get-Date) -lt $deadline) {
        Start-Sleep -Seconds 4
        try {
            $r = Invoke-Moonraker -Path '/printer/objects/query?webhooks' -TimeoutSec 10
            $state = $r.result.status.webhooks.state
            if ($state -in @('ready', 'error', 'shutdown')) {
                if ($state -ne 'ready') {
                    Write-Err "Klipper came back '$state'"
                    Write-Info $r.result.status.webhooks.state_message
                }
                return $state
            }
        } catch { }   # Moonraker/Klipper are restarting; keep waiting
    }
    return 'timeout'
}

function Set-KlipperService {
    param([ValidateSet('start', 'stop', 'restart')][string] $Action)
    # Moonraker's service API avoids needing sudo on the printer.
    Invoke-Moonraker -Path "/machine/services/$Action`?service=klipper" -Method POST | Out-Null
}

function Build-Firmware {
    <# Copy the board config in, build, and confirm the built version. #>
    param([Parameter(Mandatory)][pscustomobject] $Spec, [Parameter(Mandatory)][string] $ExpectedVersion)

    $localConfig = Join-Path $ConfigDir $Spec.ConfigFile
    if (-not (Test-Path $localConfig)) { throw "Missing board config: $localConfig" }

    Write-Info "uploading $($Spec.ConfigFile) and building..."
    $configText = (Get-Content -Raw -Path $localConfig)

    # Heredoc-safe: base64 the config so no quoting/newline surprises.
    $b64 = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($configText))

    $script = @"
set -e
mkdir -p ~/klipper-fw-backups
cd ~/klipper
echo '$b64' | base64 -d > /tmp/board.config
make clean >/dev/null 2>&1
cp /tmp/board.config .config
make olddefconfig >/dev/null 2>&1
make -j4 >/tmp/build.log 2>&1 || { echo "BUILD FAILED"; tail -25 /tmp/build.log; exit 1; }
grep -m1 '^Version:' /tmp/build.log || true
ls -l $($Spec.Artifact)
"@
    $out = Invoke-Ssh -Script $script
    Write-Info ($out -split "`n" | Select-Object -Last 2 | ForEach-Object { "  $_" })

    # The built version string must match the host, else we would flash a mismatch.
    $built = Invoke-Ssh -Script "grep -m1 -oE 'v[0-9]+\.[0-9]+\.[0-9]+-[0-9]+-g[0-9a-f]+' /tmp/build.log || true"
    $built = $built.Trim()
    if ($built -and $built -ne $ExpectedVersion) {
        throw "Built firmware is '$built' but host is '$ExpectedVersion' -- refusing to flash a mismatch."
    }
    Write-Ok "built $built"
}

function Invoke-FlashCanBoard {
    param([Parameter(Mandatory)][pscustomobject] $Spec)

    if ($Spec.Key -eq 'manta') {
        # The bridge cannot be flashed over the bus it provides: request it into
        # Katapult over CAN, then talk to it over USB serial (1d50:6177).
        $script = @"
set -e
cd ~/klipper
~/klippy-env/bin/python ~/katapult/scripts/flashtool.py -i can0 -u $($Spec.Uuid) -r 2>&1 | tail -3
for i in \$(seq 1 20); do
  sleep 2
  DEV=\$(ls /dev/serial/by-id/usb-katapult_* 2>/dev/null | head -1)
  [ -n "\$DEV" ] && break
done
DEV=\$(ls /dev/serial/by-id/usb-katapult_* 2>/dev/null | head -1)
if [ -z "\$DEV" ]; then echo "NO_KATAPULT_USB_DEVICE"; exit 1; fi
echo "katapult usb device: \$DEV"
~/klippy-env/bin/python ~/katapult/scripts/flashtool.py -d "\$DEV" -f ~/klipper/out/klipper.bin 2>&1 | tail -8
"@
    } else {
        $script = @"
set -e
cd ~/klipper
if ! ~/klippy-env/bin/python ~/katapult/scripts/flashtool.py -i can0 -q 2>&1 | grep -q '$($Spec.Uuid), Application: Katapult'; then
  ~/klippy-env/bin/python ~/katapult/scripts/flashtool.py -i can0 -u $($Spec.Uuid) -r 2>&1 | tail -2
  sleep 3
fi
~/klippy-env/bin/python ~/katapult/scripts/flashtool.py -i can0 -u $($Spec.Uuid) -f ~/klipper/out/klipper.bin 2>&1 | tail -8
"@
    }

    $out = Invoke-Ssh -Script $script
    if ($out -match 'NO_KATAPULT_USB_DEVICE') {
        throw "Manta did not enumerate as a Katapult USB device. It is likely sitting in the bootloader; retry, or recover via SD-card firmware.bin."
    }
    if ($out -notmatch 'Programming Complete') {
        throw "Flash did not report 'Programming Complete':`n$out"
    }
    foreach ($line in ($out -split "`n" | Where-Object { $_ -match 'SHA|Application Start|MCU type|Programming Complete' })) {
        Write-Info $line.Trim()
    }
}

function Test-ProbeAfterEbbFlash {
    <# TAP: the EBB carries the ONLY Z endstop. Never home Z without checking this. #>
    Write-Step 'TAP safety check (EBB carries the only Z endstop)'
    try {
        Invoke-Moonraker -Path '/printer/gcode/script?script=QUERY_PROBE' -Method POST | Out-Null
        $q = Invoke-Moonraker -Path '/printer/objects/query?probe'
        Write-Ok "QUERY_PROBE responded (last_query=$($q.result.status.probe.last_query))"
        Write-Warn 'Confirm the probe TRIGGERS by hand before any G28 Z.'
    } catch {
        Write-Err "QUERY_PROBE failed: $($_.Exception.Message)"
        Write-Err 'DO NOT home Z until this is resolved -- the toolhead would drive into the bed.'
        throw
    }
}

# --------------------------------------------------------------------------- #
# Main
# --------------------------------------------------------------------------- #
if ($Boards -contains 'all') { $Boards = @('linux', 'ebb', 'manta') }

Write-Step "Voron MCU firmware update  (host=$PrinterHost, ssh=$SshTarget)"
if ($DryRun) { Write-Warn 'DRY RUN -- no changes will be made' }

# --- preflight -------------------------------------------------------------
Write-Step 'Preflight'
$sshCheck = Invoke-Ssh -Script 'echo ssh-ok; test -d ~/katapult && echo katapult-ok; test -d ~/klipper && echo klipper-ok' -AllowFailure
foreach ($token in @('ssh-ok', 'katapult-ok', 'klipper-ok')) {
    if ($sshCheck -match $token) { Write-Ok $token } else { Write-Err "$token MISSING"; throw "Preflight failed: $token" }
}

$facts = Get-PrinterFacts
Write-Ok "host klipper: $($facts.HostVersion)"
Write-Info "job state: $($facts.JobState) | deprecated_mcu_code warnings: $($facts.DeprecatedCount)"

if ($facts.JobState -in @('printing', 'paused')) {
    throw "Printer job state is '$($facts.JobState)'. Refusing to flash. Cancel or finish the print first."
}

# --- decide ----------------------------------------------------------------
Write-Step 'Version comparison'
$todo = @()
foreach ($spec in $BoardSpecs) {
    if ($spec.Key -notin $Boards) { continue }
    $current = $facts.McuVersions[$spec.Key]
    # NB: do not name this $matches -- that is a PowerShell automatic variable.
    $isCurrent = ($current -eq $facts.HostVersion)
    $mark = if ($isCurrent) { 'up to date' } else { 'NEEDS FLASH' }
    Write-Info ("{0,-10} {1,-30} {2}" -f $spec.Key, $current, $mark)
    if (-not $isCurrent -or $Force) { $todo += $spec }
}

if (-not $todo) {
    Write-Step 'Nothing to do'
    Write-Ok 'All selected MCUs already match the host. (Use -Force to reflash anyway.)'
    exit 0
}

$script:HasRoot = $false
if ($todo | Where-Object { $_.NeedsRoot }) {
    $script:HasRoot = Test-RootSsh
    if ($script:HasRoot) {
        Write-Ok "key-based root available ($RootSshTarget) -- Linux MCU can be installed automatically"
    } else {
        Write-Warn "No key-based root at '$RootSshTarget'. The Linux MCU will be built and staged only."
        Write-Info "  Fix with: ssh-copy-id -i ~/.ssh/id_ed25519_voron.pub root@$PrinterHost"
    }
}

Write-Step ("Will flash: " + (($todo | ForEach-Object { $_.Key }) -join ', '))
if ($DryRun) { Write-Ok 'Dry run complete.'; exit 0 }

# --- execute ---------------------------------------------------------------
$flashedEbb = $false
foreach ($spec in $todo) {
    Write-Step "$($spec.Label)"

    Build-Firmware -Spec $spec -ExpectedVersion $facts.HostVersion

    if ($spec.NeedsRoot) {
        Invoke-Ssh -Script 'cp ~/klipper/out/klipper.elf ~/klipper-fw-backups/klipper_mcu-staged.elf' | Out-Null
        if ($script:HasRoot) {
            Write-Info 'installing to /usr/local/bin/klipper_mcu (via root)...'
            Install-LinuxMcu
            $state = Wait-KlipperReady
            if ($state -eq 'ready') { Write-Ok 'linux MCU installed; klipper ready' }
            else { Write-Err "klipper state after linux MCU install: $state" }
        } else {
            Write-Warn 'No key-based root available. Staged at ~/klipper-fw-backups/klipper_mcu-staged.elf; finish with:'
            Write-Info '  sudo systemctl stop klipper'
            Write-Info '  sudo cp ~/klipper-fw-backups/klipper_mcu-staged.elf /usr/local/bin/klipper_mcu'
            Write-Info '  sudo systemctl restart klipper-mcu && sudo systemctl start klipper'
        }
        continue
    }

    Write-Info 'stopping klipper...'
    Set-KlipperService -Action stop
    Start-Sleep -Seconds 4

    try {
        Invoke-FlashCanBoard -Spec $spec
        Write-Ok "$($spec.Key) flashed"
        if ($spec.Key -eq 'ebb') { $flashedEbb = $true }
    } finally {
        Write-Info 'starting klipper...'
        Set-KlipperService -Action start
        $state = Wait-KlipperReady
        if ($state -eq 'ready') { Write-Ok 'klipper ready' } else { Write-Err "klipper state: $state" }
    }
}

# --- verify ----------------------------------------------------------------
Write-Step 'Verification'
$after = Get-PrinterFacts
$allMatch = $true
foreach ($spec in $BoardSpecs) {
    if ($spec.Key -notin $Boards) { continue }
    $v = $after.McuVersions[$spec.Key]
    if ($v -eq $after.HostVersion) { Write-Ok ("{0,-10} {1}" -f $spec.Key, $v) }
    else { Write-Warn ("{0,-10} {1} (host {2})" -f $spec.Key, $v, $after.HostVersion); if (-not $spec.NeedsRoot) { $allMatch = $false } }
}
Write-Info "deprecated_mcu_code warnings: $($facts.DeprecatedCount) -> $($after.DeprecatedCount)"

if ($flashedEbb) { Test-ProbeAfterEbbFlash }

if ($allMatch -and $after.DeprecatedCount -eq 0) {
    Write-Step 'Done -- all selected MCUs match the host and no firmware warnings remain.'
    exit 0
} else {
    Write-Step 'Done with warnings -- review the output above.'
    exit 1
}
