<#
.SYNOPSIS
    Publishes the extension service for the CB1 and installs it as a systemd unit.

.DESCRIPTION
    Builds a self-contained linux-arm64 binary, so the CB1 needs no .NET runtime installed,
    copies it to the printer and starts the systemd unit.

    Only the OpenSSH client is required (built into Windows 10 1809 and later). There is no
    rsync dependency: the payload is staged under a temporary name on the printer and swapped
    into place, so a failed copy never leaves a half-updated install running.

    appsettings.json holds the pin map and calibration and is edited on the printer, so it is
    carried across upgrades unless -DeployConfig is given.

.PARAMETER Target
    SSH destination for the printer.

.PARAMETER Destination
    Install directory on the printer.

.PARAMETER DeployConfig
    Overwrite appsettings.json on the printer with the one from this repository.

.EXAMPLE
    .\install.ps1

.EXAMPLE
    .\install.ps1 -Target biqu@voron.local

.EXAMPLE
    .\install.ps1 -DeployConfig
#>
[CmdletBinding()]
param(
    [string] $Target = 'biqu@192.168.100.81',
    [string] $Destination = '/home/biqu/voron-extensions',
    [switch] $DeployConfig
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function Assert-Tool {
    param([string] $Name, [string] $Hint)

    if (-not (Get-Command $Name -ErrorAction SilentlyContinue)) {
        throw "'$Name' was not found on PATH. $Hint"
    }
}

function Invoke-Remote {
    param(
        [string] $Command,

        # sudo cannot prompt without a terminal, and most printer images ask for a password.
        # Commands that need it get a pseudo-tty so the prompt reaches you.
        [switch] $Interactive
    )

    if ($Interactive) {
        & ssh -t $Target $Command
    }
    else {
        & ssh $Target $Command
    }

    if ($LASTEXITCODE -ne 0) {
        throw "Remote command failed (exit $LASTEXITCODE): $Command"
    }
}

Assert-Tool -Name 'dotnet' -Hint 'Install the .NET SDK from https://dotnet.microsoft.com/download.'
Assert-Tool -Name 'ssh' -Hint 'Enable the Windows OpenSSH Client optional feature.'
Assert-Tool -Name 'scp' -Hint 'Enable the Windows OpenSSH Client optional feature.'

$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$repoDir = Split-Path -Parent $scriptDir
$project = Join-Path $repoDir 'Voron.Extensions.Service\Voron.Extensions.Service.csproj'
$unitName = 'voron-extensions.service'

# The staging folder is named after the remote temporary directory, because scp -r copies the
# directory itself rather than its contents.
$stageRoot = Join-Path ([System.IO.Path]::GetTempPath()) ("voron-deploy-" + [guid]::NewGuid().ToString('N'))
$stageName = (Split-Path -Leaf $Destination) + '.new'
$stage = Join-Path $stageRoot $stageName
$remoteParent = ($Destination -replace '/[^/]+/?$', '')
$remoteStage = "$Destination.new"

New-Item -ItemType Directory -Path $stage -Force | Out-Null

try {
    Write-Host '==> Publishing for linux-arm64 (self-contained)' -ForegroundColor Cyan
    & dotnet publish $project `
        --configuration Release `
        --runtime linux-arm64 `
        --self-contained true `
        --output $stage `
        --nologo
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet publish failed (exit $LASTEXITCODE)."
    }

    if (-not $DeployConfig) {
        # Keep the printer's own configuration; it is restored into the payload below.
        Remove-Item (Join-Path $stage 'appsettings.json') -ErrorAction SilentlyContinue
    }
    else {
        Write-Host '    (appsettings.json will be overwritten on the printer)' -ForegroundColor Yellow
    }

    Write-Host "==> Copying to ${Target}:${remoteStage}" -ForegroundColor Cyan
    Invoke-Remote "rm -rf '$remoteStage' && mkdir -p '$remoteParent'"

    # scp reads everything before the first colon as a hostname, so an absolute Windows path like
    # C:\Temp\... is parsed as the host "C". Copy from inside the folder using a relative name.
    Push-Location $stageRoot
    try {
        & scp -r -q $stageName "${Target}:${remoteParent}/"
        if ($LASTEXITCODE -ne 0) {
            throw "scp failed (exit $LASTEXITCODE)."
        }
    }
    finally {
        Pop-Location
    }

    Write-Host '==> Swapping the new build into place' -ForegroundColor Cyan
    # Carry the live configuration over, then replace the install directory in one step.
    Invoke-Remote @"
set -e
if [ -f '$Destination/appsettings.json' ] && [ ! -f '$remoteStage/appsettings.json' ]; then
  cp '$Destination/appsettings.json' '$remoteStage/appsettings.json'
fi
chmod +x '$remoteStage/Voron.Extensions.Service'
rm -rf '$Destination'
mv '$remoteStage' '$Destination'
"@

    Write-Host "==> Installing $unitName" -ForegroundColor Cyan
    Push-Location $scriptDir
    try {
        & scp -q $unitName "${Target}:/tmp/$unitName"
        if ($LASTEXITCODE -ne 0) {
            throw "scp of the unit file failed (exit $LASTEXITCODE)."
        }
    }
    finally {
        Pop-Location
    }

    Write-Host '    (sudo on the printer may ask for your password)' -ForegroundColor Yellow
    Invoke-Remote -Interactive @"
sudo install -m 644 '/tmp/$unitName' '/etc/systemd/system/$unitName' && rm -f '/tmp/$unitName' && sudo systemctl daemon-reload && sudo systemctl enable $unitName && sudo systemctl restart $unitName
"@

    Write-Host ''
    Write-Host 'Done. Follow the log with:' -ForegroundColor Green
    Write-Host "  ssh $Target 'journalctl -u $unitName -f'"
}
finally {
    Remove-Item $stageRoot -Recurse -Force -ErrorAction SilentlyContinue
}
