<#
.SYNOPSIS
Builds InterCat's Windows Installer package (ADR-115) from the Desktop's and the CLI's framework-dependent publish.

.DESCRIPTION
Publishes both clients, each staging the broker beside itself, into one folder, refusing any file the two would put
there with different bytes; signs InterCat's own binaries, then the package, when -Certificate names a certificate;
generates the files' fragment from the folder and builds the package with the WiX toolset's `wix` command. Writes one
line naming the package and its SHA-256.

.PARAMETER Version
The package's version, major.minor.build: an update installs over an earlier one and refuses to replace a later one.

.PARAMETER Output
Where the stage and the package go; the repository's git-ignored artifacts folder by default.

.PARAMETER Certificate
A code-signing certificate's .pfx, without a password - until a release certificate exists, the test certificate
New-TestCertificate.ps1 makes. Without it nothing is signed.
#>
param(
    [Parameter(Mandatory)] [ValidatePattern('^\d{1,3}\.\d{1,3}\.\d{1,5}$')] [string] $Version,
    [string] $Output = (Join-Path $PSScriptRoot '..\artifacts\installer'),
    [string] $Certificate
)
$ErrorActionPreference = 'Stop'
$repository = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$output = [IO.Path]::GetFullPath($Output)
$stage = Join-Path $output 'stage'
$app = Join-Path $stage 'app'
if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
$null = New-Item -ItemType Directory -Force $app

function Find-SignTool {
    $found = Get-Command signtool.exe -ErrorAction SilentlyContinue
    if ($found) { return $found.Source }
    $kits = Join-Path ${env:ProgramFiles(x86)} 'Windows Kits\10\bin'
    $latest = Get-ChildItem $kits -Directory -ErrorAction SilentlyContinue | Where-Object { Test-Path (Join-Path $_.FullName 'x64\signtool.exe') } |
        Sort-Object { [version]$_.Name } -Descending | Select-Object -First 1
    if (-not $latest) { throw 'No signtool.exe: install the Windows SDK, or put signtool on the path.' }
    return Join-Path $latest.FullName 'x64\signtool.exe'
}

function Invoke-Sign([string[]] $Paths) {
    & $script:signTool sign /q /fd SHA256 /f $Certificate @Paths
    if ($LASTEXITCODE -ne 0) { throw "Signing failed: $($Paths -join ', ')" }
}

# Both clients into one folder: the files they share must be the same bytes, the broker's among them.
foreach ($client in 'InterCat.Desktop', 'InterCat.Cli') {
    $published = Join-Path $stage $client
    dotnet publish (Join-Path $repository "src\$client\$client.csproj") -c Release -o $published "-p:Version=$Version" -nologo -v:q
    if ($LASTEXITCODE -ne 0) { throw "Publishing $client failed." }
    foreach ($file in Get-ChildItem $published -Recurse -File) {
        $relative = [IO.Path]::GetRelativePath($published, $file.FullName)
        $target = Join-Path $app $relative
        if (Test-Path $target) {
            if ((Get-FileHash $target -Algorithm SHA256).Hash -ne (Get-FileHash $file.FullName -Algorithm SHA256).Hash) {
                throw "The Desktop and the CLI put different bytes under $relative, so one folder cannot hold both."
            }
            continue
        }
        $null = New-Item -ItemType Directory -Force (Split-Path $target)
        Copy-Item $file.FullName $target
    }
}
foreach ($required in 'InterCat.Desktop.exe', 'InterCat.Cli.exe', 'InterCat.CaptureBroker.exe') {
    if (-not (Test-Path (Join-Path $app $required))) { throw "The publish holds no $required." }
}

if ($Certificate) {
    $Certificate = [IO.Path]::GetFullPath($Certificate)
    $script:signTool = Find-SignTool
    Invoke-Sign @(Get-ChildItem $app -Recurse -File | Where-Object { $_.Name -like 'InterCat.*' -and $_.Extension -in '.exe', '.dll' } |
        ForEach-Object FullName)
}

# The files' fragment: a component a file, its identifier a digest of its path, the three executables named.
$named = @{ 'intercat.desktop.exe' = 'DesktopExe'; 'intercat.cli.exe' = 'CliExe'; 'intercat.capturebroker.exe' = 'BrokerExe' }
function Get-Id([string] $prefix, [string] $relative) {
    $bytes = [Text.Encoding]::UTF8.GetBytes($relative.ToLowerInvariant())
    $prefix + [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($bytes)).Substring(0, 32)
}
$directories = [Collections.Generic.SortedDictionary[string, string]]::new([StringComparer]::OrdinalIgnoreCase)
$components = [Text.StringBuilder]::new()
foreach ($file in Get-ChildItem $app -Recurse -File | Sort-Object FullName) {
    $relative = [IO.Path]::GetRelativePath($app, $file.FullName)
    $folder = Split-Path $relative
    $directory = 'INSTALLFOLDER'
    if ($folder) {
        $parts = $folder.Split([IO.Path]::DirectorySeparatorChar)
        for ($at = 1; $at -le $parts.Length; $at++) {
            $path = $parts[0..($at - 1)] -join '\'
            if (-not $directories.ContainsKey($path)) { $directories[$path] = Get-Id 'd' $path }
        }
        $directory = $directories[$folder]
    }
    $id = $named[$relative.ToLowerInvariant()]
    if (-not $id) { $id = Get-Id 'f' $relative }
    $source = [Security.SecurityElement]::Escape($file.FullName)
    $null = $components.AppendLine("      <Component Id=`"c$id`" Directory=`"$directory`"><File Id=`"$id`" Source=`"$source`" KeyPath=`"yes`" /></Component>")
}
$tree = [Text.StringBuilder]::new()
function Write-Directories([string] $parent, [int] $depth) {
    foreach ($path in $directories.Keys) {
        $folder = Split-Path $path
        if ($folder -ne $parent) { continue }
        $name = [Security.SecurityElement]::Escape((Split-Path $path -Leaf))
        $null = $tree.AppendLine((' ' * (6 + 2 * $depth)) + "<Directory Id=`"$($directories[$path])`" Name=`"$name`">")
        Write-Directories $path ($depth + 1)
        $null = $tree.AppendLine((' ' * (6 + 2 * $depth)) + '</Directory>')
    }
}
Write-Directories '' 0
$fragment = Join-Path $stage 'Files.wxs'
@"
<?xml version="1.0" encoding="utf-8"?>
<Wix xmlns="http://wixtoolset.org/schemas/v4/wxs">
  <Fragment>
    <DirectoryRef Id="INSTALLFOLDER">
$($tree.ToString().TrimEnd())
    </DirectoryRef>
    <ComponentGroup Id="InterCatFiles">
$($components.ToString().TrimEnd())
    </ComponentGroup>
  </Fragment>
</Wix>
"@ | Set-Content -Path $fragment -Encoding utf8

$package = Join-Path $output "InterCat-$Version-x64.msi"
if (Test-Path $package) { Remove-Item $package -Force }
wix build -arch x64 -d "Version=$Version" -d "Stage=$app" -o $package (Join-Path $PSScriptRoot 'InterCat.wxs') $fragment
if ($LASTEXITCODE -ne 0) { throw 'wix build failed.' }
if ($Certificate) { Invoke-Sign @($package) }
"package $package $((Get-FileHash $package -Algorithm SHA256).Hash.ToLowerInvariant())"
