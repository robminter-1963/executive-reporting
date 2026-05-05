# Build + publish the Executive Reporting Suite (Web + Worker) to IIS.
# Usage (from any working dir):   powershell -ExecutionPolicy Bypass -File .\publish.ps1
# Requires admin elevation (for Stop/Start-WebAppPool) and the WebAdministration module.
# The script will UAC-elevate itself automatically if run from a normal shell.

[CmdletBinding()]
param(
    [string] $SolutionDir       = "D:\MyProjects\TLE-Reporting-Dashboard\src",
    [string] $Solution          = "TleReportingDashboard.sln",
    [string] $Configuration     = "Release",

    # Web app
    [string] $WebProject        = "TleReportingDashboard.Web",
    [string] $WebPublishPath    = "C:\WebApps\ReportingDashboard",
    [string] $WebAppPool        = "TLE.Reporting",

    # Worker (Hangfire scheduler)
    [string] $WorkerProject     = "TleReportingDashboard.Worker",
    [string] $WorkerPublishPath = "C:\WebApps\ReportingScheduler",
    [string] $WorkerAppPool     = "TleReporting-Worker"
)

$ErrorActionPreference = "Stop"

# --- Self-elevate (Stop/Start-WebAppPool need admin). -----------------------
$principal = [Security.Principal.WindowsPrincipal]::new(
    [Security.Principal.WindowsIdentity]::GetCurrent())
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltinRole]::Administrator)) {
    Write-Host "Not running as administrator - relaunching elevated..." -ForegroundColor Yellow

    $forwarded = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', "`"$PSCommandPath`"")
    foreach ($kv in $PSBoundParameters.GetEnumerator()) {
        $forwarded += "-$($kv.Key)"
        $forwarded += "`"$($kv.Value)`""
    }
    try {
        Start-Process -FilePath 'powershell.exe' -Verb RunAs -ArgumentList $forwarded
    }
    catch {
        Write-Error ("UAC prompt was cancelled or failed: " + $_.Exception.Message)
        exit 1
    }
    exit
}

function Write-Step($msg) {
    Write-Host ""
    Write-Host ("==> " + $msg) -ForegroundColor Cyan
}

# Stops an IIS app pool, clears its deployment folder, dotnet-publishes the
# project, and starts the pool back up. On publish failure, restarts the
# pool so the previous version stays serving traffic.
#
# Used twice below — once for the Web app, once for the Worker. Both
# follow the same shape; pulling it into a function keeps the script
# readable when a third app inevitably joins the rotation.
function Publish-IISApp {
    param(
        [Parameter(Mandatory)] [string] $ProjectName,
        [Parameter(Mandatory)] [string] $PublishPath,
        [Parameter(Mandatory)] [string] $AppPoolName,
        [Parameter(Mandatory)] [string] $Configuration
    )

    Write-Step ("---- Deploy: " + $ProjectName + " -> " + $PublishPath + " ----")

    # 1. Stop the IIS app pool so file copies do not hit locked DLLs.
    Write-Step ("Stopping IIS application pool " + $AppPoolName)
    $poolPath = "IIS:\AppPools\" + $AppPoolName
    if (-not (Test-Path $poolPath)) {
        throw ("Application pool " + $AppPoolName + " does not exist on this machine.")
    }
    $poolState = (Get-WebAppPoolState -Name $AppPoolName).Value
    Write-Host ("   current state: " + $poolState)
    if ($poolState -ne "Stopped") {
        Stop-WebAppPool -Name $AppPoolName
        for ($i = 0; $i -lt 30 -and (Get-WebAppPoolState -Name $AppPoolName).Value -ne "Stopped"; $i++) {
            Start-Sleep -Milliseconds 500
        }
        Write-Host "   stopped"
    }
    else {
        Write-Host "   already stopped"
    }

    # 2. Clear the deployment folder so stale files (razor components, static
    #    assets, removed DLLs) do not linger across deployments. The folder
    #    itself is preserved in case IIS bindings are pinned to the path.
    Write-Step ("Clearing deployment folder " + $PublishPath)
    if (Test-Path $PublishPath) {
        Get-ChildItem -Path $PublishPath -Force | Remove-Item -Recurse -Force -ErrorAction Stop
        Write-Host "   cleared"
    }
    else {
        Write-Host "   folder missing; will be created by publish"
    }

    # 3. Publish over the existing site.
    Write-Step ("Publishing " + $ProjectName + " to " + $PublishPath)
    $publishFailed = $false
    try {
        dotnet publish $ProjectName -c $Configuration -o $PublishPath --nologo -v minimal
        if ($LASTEXITCODE -ne 0) {
            throw ("dotnet publish failed with exit code " + $LASTEXITCODE)
        }
    }
    catch {
        $publishFailed = $true
        Write-Host "   publish failed; restarting app pool to restore previous version" -ForegroundColor Yellow
        Start-WebAppPool -Name $AppPoolName -ErrorAction SilentlyContinue
        throw
    }

    # 4. Start the app pool so IIS loads the freshly-published bits.
    if (-not $publishFailed) {
        Write-Step ("Starting IIS application pool " + $AppPoolName)
        Start-WebAppPool -Name $AppPoolName
        for ($i = 0; $i -lt 30 -and (Get-WebAppPoolState -Name $AppPoolName).Value -ne "Started"; $i++) {
            Start-Sleep -Milliseconds 500
        }
        Write-Host ("   state: " + (Get-WebAppPoolState -Name $AppPoolName).Value)
        Write-Host ("   published " + $ProjectName + " to " + $PublishPath) -ForegroundColor Green
    }
}

# --- Load IIS module up front so both the main flow and the catch block can use it.
Import-Module WebAdministration -ErrorAction Stop

# 1. Ensure working directory is the src folder.
Write-Step ("Ensuring working directory is " + $SolutionDir)
if ((Get-Location).Path -ne $SolutionDir) {
    if (-not (Test-Path $SolutionDir)) {
        throw ("Solution directory not found: " + $SolutionDir)
    }
    Set-Location -Path $SolutionDir
}
Write-Host ("   current: " + (Get-Location).Path)

# 2. Wipe both deploy targets' bin/obj so the build can't reuse cached
#    outputs. Web specifically guards against the razor source generator
#    not detecting markup-only changes (has caused stale deployments
#    historically). Worker doesn't have razor but the parallel cleanup
#    keeps both deploys deterministic — saw incremental-cache weirdness
#    when the shared Core assembly's interface surface changed and
#    Worker kept binding against a stale copy. Core / AppHost /
#    ServiceDefaults / Tests rebuild fine from incremental caches.
Write-Step "Removing bin/obj from $WebProject + $WorkerProject"
foreach ($projectName in @($WebProject, $WorkerProject)) {
    $projDir = Join-Path $SolutionDir $projectName
    foreach ($folder in 'bin','obj') {
        $target = Join-Path $projDir $folder
        if (Test-Path $target) {
            Write-Host ("   " + $target)
            try {
                Remove-Item -Path $target -Recurse -Force -ErrorAction Stop
            }
            catch {
                Write-Host ("   WARN: could not remove " + $target + " - " + $_.Exception.Message) -ForegroundColor Yellow
            }
        }
    }
}

# 3. Build the whole solution once. Fail fast before touching IIS.
#    Both the Web app and the Worker pick up their outputs from the
#    shared bin/Release folders this populates.
Write-Step ("Building solution (" + $Solution + ", " + $Configuration + ")")
dotnet build $Solution -c $Configuration --nologo -v minimal
if ($LASTEXITCODE -ne 0) {
    throw ("dotnet build failed with exit code " + $LASTEXITCODE)
}

# 4. Deploy each app. Web first (the user-facing site is more sensitive
#    to downtime, so it gets the pre-stopped pool ASAP), then the Worker
#    (Hangfire jobs in flight finish under the old worker until the pool
#    stops; new jobs queue and pick up under the freshly-deployed code).
Publish-IISApp -ProjectName $WebProject    -PublishPath $WebPublishPath    -AppPoolName $WebAppPool    -Configuration $Configuration
Publish-IISApp -ProjectName $WorkerProject -PublishPath $WorkerPublishPath -AppPoolName $WorkerAppPool -Configuration $Configuration

Write-Step "Done."
Write-Host ("Web    -> " + $WebPublishPath)    -ForegroundColor Green
Write-Host ("Worker -> " + $WorkerPublishPath) -ForegroundColor Green
# Wall-clock stamp so a leftover console window tells you at a
# glance when the last successful publish ran. 12-hour format
# with AM/PM matches the rest of the team's notes.
Write-Host ("Completed: " + (Get-Date).ToString("yyyy-MM-dd hh:mm:ss tt")) -ForegroundColor Green
