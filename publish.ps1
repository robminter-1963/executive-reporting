# Build + publish the Executive Reporting Suite to IIS.
# Usage (from any working dir):   powershell -ExecutionPolicy Bypass -File .\publish.ps1
# Requires admin elevation (for Stop/Start-WebAppPool) and the WebAdministration module.
# The script will UAC-elevate itself automatically if run from a normal shell.

[CmdletBinding()]
param(
    [string] $SolutionDir   = "D:\MyProjects\TLE-Reporting-Dashboard\src",
    [string] $WebProject    = "TleReportingDashboard.Web",
    [string] $Solution      = "TleReportingDashboard.sln",
    [string] $PublishPath   = "C:\WebApps\ReportingDashboard",
    [string] $AppPoolName   = "TLE.Reporting",
    [string] $Configuration = "Release"
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

# 2. Wipe the Web project's bin/obj so the build can't reuse cached outputs.
#    Specifically guards against the razor source generator not detecting
#    markup-only changes, which has caused stale deployments in the past.
#    Other projects (Worker, AppHost, ServiceDefaults, Tests) don't have
#    razor components and rebuild reliably from incremental caches.
Write-Step "Removing bin/obj from $WebProject"
$webDir = Join-Path $SolutionDir $WebProject
foreach ($folder in 'bin','obj') {
    $target = Join-Path $webDir $folder
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

# 3. Build the whole solution. Fail fast before touching IIS.
Write-Step ("Building solution (" + $Solution + ", " + $Configuration + ")")
dotnet build $Solution -c $Configuration --nologo
if ($LASTEXITCODE -ne 0) {
    throw ("dotnet build failed with exit code " + $LASTEXITCODE)
}

# 4. Stop the IIS app pool so file copies do not hit locked DLLs.
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

# 5. Clear the deployment folder so stale files (razor components, static
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

# 6. Publish over the existing site.
Write-Step ("Publishing " + $WebProject + " to " + $PublishPath)
$publishFailed = $false
try {
    dotnet publish $WebProject -c $Configuration -o $PublishPath --nologo
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

# 7. Start the app pool so IIS loads the freshly-published bits.
if (-not $publishFailed) {
    Write-Step ("Starting IIS application pool " + $AppPoolName)
    Start-WebAppPool -Name $AppPoolName
    for ($i = 0; $i -lt 30 -and (Get-WebAppPoolState -Name $AppPoolName).Value -ne "Started"; $i++) {
        Start-Sleep -Milliseconds 500
    }
    Write-Host ("   state: " + (Get-WebAppPoolState -Name $AppPoolName).Value)

    Write-Step "Done."
    Write-Host ("Published to " + $PublishPath) -ForegroundColor Green
}
