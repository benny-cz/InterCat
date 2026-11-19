<#
.SYNOPSIS
Checks InterCat's Windows Installer package on a machine (ADR-115): installation, the broker's root read back, a capture
through the installed broker, an update over it, a removal refused while a capture records, a removal that keeps the
captures and, where nothing else was recorded, one that removes them, each followed by a look for anything left.

.DESCRIPTION
Runs elevated, in Windows PowerShell 5.1 or later, and writes one JSON report of counts, exit codes and yes-or-no
findings - no SID, process ID, user name or user folder. It refuses a machine where InterCat is installed. On a machine
whose broker root already exists it keeps that root and every capture in it: it never removes InterCat asking for the
captures there, says so in the report, and at the end deletes only the captures its own runs recorded. It changes no
certificate store: each file's signer is compared with the certificate the packages were signed with, and the report
says whether this machine trusts it.

.PARAMETER Package
The package to install first.

.PARAMETER Update
A later version of the package, installed over the first.

.PARAMETER Certificate
The .cer the packages were signed with.

.PARAMETER Output
The report's path; the installer's logs and each capture's output go to a logs folder beside it.
#>
param(
    [Parameter(Mandatory = $true)] [string] $Package,
    [Parameter(Mandatory = $true)] [string] $Update,
    [Parameter(Mandatory = $true)] [string] $Certificate,
    [Parameter(Mandatory = $true)] [string] $Output
)
$ErrorActionPreference = 'Stop'
$folder = Join-Path $env:ProgramFiles 'InterCat'
$root = Join-Path $env:ProgramData 'InterCat'
$work = Join-Path ([IO.Path]::GetTempPath()) ('intercat-checks-' + [Guid]::NewGuid().ToString('N'))
$logs = Join-Path (Split-Path ([IO.Path]::GetFullPath($Output))) 'logs'
$null = New-Item -ItemType Directory -Force $work, $logs
$thumbprint = (New-Object Security.Cryptography.X509Certificates.X509Certificate2 $Certificate).Thumbprint
$userSid = [Security.Principal.WindowsIdentity]::GetCurrent().User.Value
$ours = New-Object System.Collections.Generic.List[string]
$report = [ordered]@{ schema = 'intercat.installer-checks.v1'; measuredUtc = [DateTime]::UtcNow.ToString('o') }

function Invoke-Msi([string[]] $Arguments, [string] $Name) {
    $log = Join-Path $logs "$Name.log"
    $process = Start-Process msiexec.exe -ArgumentList ($Arguments + @('/qn', '/l*v', "`"$log`"")) -Wait -PassThru
    $process.ExitCode
}

function Get-Product {
    $keys = 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\*'
    Get-ItemProperty $keys -ErrorAction SilentlyContinue | Where-Object { $_.DisplayName -eq 'InterCat' } | Select-Object -First 1
}

function Get-Captures {
    if (Test-Path $root) { @(Get-ChildItem $root -Directory -Filter 'capture-*' | ForEach-Object Name) } else { @() }
}

function Get-Leftovers {
    $entries = @([Environment]::GetEnvironmentVariable('Path', 'Machine').Split(';') | Where-Object { $_.TrimEnd('\') -ieq $folder })
    [ordered]@{
        installed = $null -ne (Get-Product)
        folder = Test-Path $folder
        pathEntry = $entries.Count -gt 0
        startMenuEntry = Test-Path (Join-Path $env:ProgramData 'Microsoft\Windows\Start Menu\Programs\InterCat.lnk')
        brokerProcesses = @(Get-Process InterCat.CaptureBroker -ErrorAction SilentlyContinue).Count
        traceSessions = @(logman query -ets | Select-String -Pattern '^\s*InterCat-').Count
        root = Test-Path $root
        capturesOfTheseChecks = @(Get-Captures | Where-Object { $ours.Contains($_) }).Count
        otherCaptures = @(Get-Captures | Where-Object { -not $ours.Contains($_) }).Count
    }
}

function Get-RootSecurity {
    # The production descriptor (broker-v1 §5.4) for the user who installed, compared ACE for ACE from the folder itself;
    # the label as icacls reads it. Nothing of either is recorded but whether it matched.
    $observed = New-Object Security.AccessControl.RawSecurityDescriptor ((Get-Acl $root).Sddl)
    $expected = New-Object Security.AccessControl.RawSecurityDescriptor "O:BAD:P(A;OICI;FA;;;SY)(A;OICI;FA;;;BA)(A;OICI;0x1200a9;;;$userSid)"
    $aces = @($observed.DiscretionaryAcl | ForEach-Object { '{0}|{1}|{2}' -f $_.SecurityIdentifier, $_.AccessMask, $_.AceFlags }) | Sort-Object
    $wanted = @($expected.DiscretionaryAcl | ForEach-Object { '{0}|{1}|{2}' -f $_.SecurityIdentifier, $_.AccessMask, $_.AceFlags }) | Sort-Object
    $label = @(icacls $root | Where-Object { $_ -match 'Mandatory Label\\High Mandatory Level:\(OI\)\(CI\)\(NW\)' })
    [ordered]@{
        ownerIsAdministrators = $observed.Owner.Value -eq 'S-1-5-32-544'
        daclProtected = ($observed.ControlFlags -band [Security.AccessControl.ControlFlags]::DiscretionaryAclProtected) -ne 0
        daclIsTheProductions = ($aces -join ';') -eq ($wanted -join ';')
        highNoWriteUpLabel = $label.Count -eq 1
    }
}

function Start-Capture([string] $Name, [int] $Seconds) {
    Start-Process (Join-Path $folder 'icat.exe') -ArgumentList @('capture', "`"$(Join-Path $work $Name)`"", '--duration', "$Seconds", '--json') -PassThru `
        -RedirectStandardOutput (Join-Path $logs "$Name.json") -RedirectStandardError (Join-Path $logs "$Name.err.txt") -WindowStyle Hidden
}

function Read-Capture([string] $Name, $Process) {
    # What the capture said stays in its log beside the report, which holds no path or text a machine wrote.
    $null = $Process.WaitForExit(300000)
    $result = [ordered]@{ exit = $Process.ExitCode }
    try {
        $document = Get-Content (Join-Path $logs "$Name.json") -Raw | ConvertFrom-Json
        $ours.Add('capture-' + $document.captureId)
        $result.finished = [bool]$document.finished
        $result.state = [string]$document.state
        $result.derivedRecords = [long]$document.derivedRecords
    }
    catch { $result.document = 'none' }
    $result
}

# Before anything: InterCat not installed, what installing it needs, and whether a root already holds captures.
$runtimes = @(& dotnet --list-runtimes 2>$null)
$before = @(Get-Captures)
$report.before = [ordered]@{
    elevated = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
    build = [Environment]::OSVersion.Version.ToString()
    desktopRuntime10 = @($runtimes | Where-Object { $_ -match '^Microsoft\.WindowsDesktop\.App 10\.' }).Count -gt 0
    certificateTrusted = (Get-AuthenticodeSignature $Package).Status -eq 'Valid'
    rootExisted = Test-Path $root
    capturesInIt = $before.Count
    leftovers = Get-Leftovers
}
if ($report.before.leftovers.installed) { throw 'InterCat is installed here: these checks install and remove it, so they run only where it is not.' }
$keepRoot = $report.before.rootExisted

try {
    # Install: the files and their signatures, the command, the path and the menu, the root and its security.
    $report.install = [ordered]@{ exit = Invoke-Msi @('/i', "`"$Package`"") 'install' }
    $own = @(Get-ChildItem $folder -File | Where-Object { ($_.Name -like 'InterCat.*' -or $_.Name -eq 'icat.exe') -and $_.Extension -in '.exe', '.dll' })
    $report.install.files = @(Get-ChildItem $folder -Recurse -File).Count
    $report.install.ownBinaries = $own.Count
    $signatures = @($own | ForEach-Object { Get-AuthenticodeSignature $_.FullName })
    $report.install.ownSignedByTheCertificate = @($signatures | Where-Object { $_.SignerCertificate -and $_.SignerCertificate.Thumbprint -eq $thumbprint }).Count
    $report.install.ownSignaturesTrustedHere = @($signatures | Where-Object { $_.Status -eq 'Valid' }).Count
    $signature = Get-AuthenticodeSignature $Package
    $report.install.packageSignedByTheCertificate = $null -ne $signature.SignerCertificate -and $signature.SignerCertificate.Thumbprint -eq $thumbprint
    $report.install.packageSignatureTrustedHere = $signature.Status -eq 'Valid'
    $help = @(& (Join-Path $folder 'icat.exe') --help 2>&1)
    $report.install.icatAnswers = $LASTEXITCODE -eq 0 -and (($help | Out-String) -match 'InterCat command line')
    $report.install.leftovers = Get-Leftovers
    $report.install.rootSecurity = Get-RootSecurity

    # A capture through the installed broker.
    $report.capture = Read-Capture 'first' (Start-Capture 'first' 10)
    Start-Sleep -Seconds 15
    $report.capture.leftovers = Get-Leftovers

    # An update over it keeps the root and what it recorded.
    $report.update = [ordered]@{ exit = Invoke-Msi @('/i', "`"$Update`"") 'update' }
    $report.update.version = (Get-Product).DisplayVersion
    $report.update.leftovers = Get-Leftovers
    $report.update.rootSecurity = Get-RootSecurity

    # A removal while a capture records is refused, the capture untouched.
    $second = Start-Capture 'second' 60
    $deadline = [DateTime]::UtcNow.AddSeconds(90)
    while ([DateTime]::UtcNow -lt $deadline -and @(Get-Process InterCat.CaptureBroker -ErrorAction SilentlyContinue).Count -eq 0) { Start-Sleep -Milliseconds 500 }
    Start-Sleep -Seconds 5
    $report.refusedWhileRecording = [ordered]@{ brokerRunning = @(Get-Process InterCat.CaptureBroker -ErrorAction SilentlyContinue).Count -gt 0 }
    $report.refusedWhileRecording.exit = Invoke-Msi @('/x', "`"$Update`"") 'refused'
    $report.refusedWhileRecording.stillInstalled = $null -ne (Get-Product)
    $report.refusedWhileRecording.brokerStillRunning = @(Get-Process InterCat.CaptureBroker -ErrorAction SilentlyContinue).Count -gt 0
    $report.refusedWhileRecording.capture = Read-Capture 'second' $second
    Start-Sleep -Seconds 15
    $report.refusedWhileRecording.leftovers = Get-Leftovers

    # A removal keeps the captures and leaves nothing else.
    $report.removeKeepingCaptures = [ordered]@{ exit = Invoke-Msi @('/x', "`"$Update`"") 'remove' }
    $report.removeKeepingCaptures.leftovers = Get-Leftovers

    if ($keepRoot) {
        # A root that held captures before these checks is never removed with its captures here.
        $report.removeWithCaptures = [ordered]@{ skipped = 'The machine''s broker root held captures before these checks; they are never removed.' }
    }
    else {
        $report.reinstall = [ordered]@{ exit = Invoke-Msi @('/i', "`"$Update`"") 'reinstall' }
        $report.removeWithCaptures = [ordered]@{ exit = Invoke-Msi @('/x', "`"$Update`"", 'INTERCAT_REMOVE_EVIDENCE=1') 'remove-evidence' }
        $report.removeWithCaptures.leftovers = Get-Leftovers
    }

    # Installed and removed with nothing recorded meanwhile: the root goes with it, unless it holds captures.
    $report.freshInstall = [ordered]@{ exit = Invoke-Msi @('/i', "`"$Package`"") 'fresh' }
    $report.freshInstall.root = Test-Path $root
    $report.removeUnused = [ordered]@{ exit = Invoke-Msi @('/x', "`"$Package`"") 'remove-unused' }
    $report.removeUnused.leftovers = Get-Leftovers
}
finally {
    # Only what these checks recorded is deleted, each by the capture ID its run reported.
    if ($keepRoot) {
        foreach ($name in $ours) {
            $path = Join-Path $root $name
            if (Test-Path $path) { Remove-Item $path -Recurse -Force }
        }
        $report.after = [ordered]@{ ownCapturesDeleted = $ours.Count; capturesInTheRoot = @(Get-Captures).Count; capturesBefore = $before.Count }
    }
    Remove-Item $work -Recurse -Force -ErrorAction SilentlyContinue
    $report | ConvertTo-Json -Depth 6 | Set-Content -Path $Output -Encoding UTF8
}
"report $Output"
