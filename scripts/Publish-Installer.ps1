[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $InnoCompiler,
    [string] $OutputDirectory,
    [string] $PortableArchive
)

$ErrorActionPreference = 'Stop'
$workspace = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..')).Path
$compiler = (Get-Command $InnoCompiler -CommandType Application -ErrorAction Stop).Source
if (!$OutputDirectory) { $OutputDirectory = Join-Path $workspace 'artifacts' }
$artifactDir = [IO.Path]::GetFullPath($OutputDirectory)
$version = ([xml](Get-Content -Raw -LiteralPath (Join-Path $workspace 'Directory.Build.props'))).Project.PropertyGroup.Version
if ($version -notmatch '^\d+\.\d+\.\d+(?:-[0-9A-Za-z.]+)?$') { throw 'Invalid release version.' }

$portableName = "GestureFlow-$version-win-x64-portable"
if (!$PortableArchive) {
    & (Join-Path $PSScriptRoot 'Publish-Portable.ps1') -OutputDirectory $artifactDir | Out-Host
    $PortableArchive = Join-Path $artifactDir "$portableName.zip"
}

$archive = (Resolve-Path -LiteralPath $PortableArchive).Path
$stage = Join-Path $artifactDir ("setup-stage-" + [Guid]::NewGuid().ToString('N'))
$package = Join-Path $stage $portableName
$installerName = "GestureFlow-$version-win-x64-setup.exe"
New-Item -ItemType Directory -Path $stage -Force | Out-Null

try {
    [IO.Compression.ZipFile]::ExtractToDirectory($archive, $stage)
    foreach ($requiredFile in @('Desktop\GestureFlow.Desktop.exe', 'Lab\GestureFlow.Lab.exe', 'docs\USER_GUIDE.md', 'licenses')) {
        if (!(Test-Path -LiteralPath (Join-Path $package $requiredFile))) {
            throw "Installer payload is missing $requiredFile."
        }
    }

    $definition = Join-Path $PSScriptRoot 'GestureFlow.iss'
    & $compiler /Qp "/DAppVersion=$version" "/DPackageDirectory=$package" "/O$stage" $definition | Out-Host
    if ($LASTEXITCODE -ne 0) { throw "Installer compilation failed ($LASTEXITCODE)." }

    $installer = Join-Path $artifactDir $installerName
    [IO.File]::Move((Join-Path $stage $installerName), $installer, $true)
    Write-Output $installer
} finally {
    $resolved = (Resolve-Path -LiteralPath $stage).Path
    $expectedParent = (Resolve-Path -LiteralPath $artifactDir).Path
    if ((Split-Path $resolved -Parent) -eq $expectedParent -and (Split-Path $resolved -Leaf) -match '^setup-stage-[0-9a-f]{32}$') {
        Remove-Item -LiteralPath $resolved -Recurse -Force
    }
}
