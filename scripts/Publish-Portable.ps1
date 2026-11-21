[CmdletBinding()]
param(
    [string] $OutputDirectory
)

$ErrorActionPreference = 'Stop'
$workspace = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..')).Path
if (!$OutputDirectory) { $OutputDirectory = Join-Path $workspace 'artifacts' }
$artifactDir = [IO.Path]::GetFullPath($OutputDirectory)
$version = ([xml](Get-Content -Raw -LiteralPath (Join-Path $workspace 'Directory.Build.props'))).Project.PropertyGroup.Version
if ($version -notmatch '^\d+\.\d+\.\d+(?:-[0-9A-Za-z.]+)?$') { throw 'Invalid release version.' }

$packageName = "GestureFlow-$version-win-x64-portable"
$stage = Join-Path $artifactDir ("stage-" + [Guid]::NewGuid().ToString('N'))
$root = Join-Path $stage $packageName
New-Item -ItemType Directory -Path $root -Force | Out-Null

Push-Location -LiteralPath $workspace
try {
    foreach ($app in @('Desktop', 'Lab')) {
        $project = Join-Path $workspace "GestureFlow.$app\GestureFlow.$app.csproj"
        dotnet publish $project -c Release -r win-x64 --self-contained true -p:DebugType=None -p:DebugSymbols=false -o (Join-Path $root $app)
        if ($LASTEXITCODE -ne 0) { throw "Publish failed for $app ($LASTEXITCODE)." }
        foreach ($name in @("GestureFlow.$app.exe", 'coreclr.dll', 'PresentationFramework.dll')) {
            if (!(Test-Path -LiteralPath (Join-Path (Join-Path $root $app) $name) -PathType Leaf)) {
                throw "$app output missing $name."
            }
        }
    }

    foreach ($document in @('README.md', 'CHANGELOG.md')) {
        Copy-Item -LiteralPath (Join-Path $workspace $document) -Destination $root
    }
    $projectLicense = Join-Path $workspace 'LICENSE'
    if (Test-Path -LiteralPath $projectLicense -PathType Leaf) {
        Copy-Item -LiteralPath $projectLicense -Destination $root
    }
    $documentation = Join-Path $root 'docs'
    New-Item -ItemType Directory -Path $documentation | Out-Null
    foreach ($document in @('COMPATIBILITY.md', 'PROGRESS.md', 'USER_GUIDE.md', 'SAFETY.md', 'PRIVACY.md', 'FAQ.md', 'THIRD_PARTY.md')) {
        Copy-Item -LiteralPath (Join-Path (Join-Path $workspace 'docs') $document) -Destination $documentation
    }
    $runtime = (Get-Content -Raw -LiteralPath (Join-Path $root 'Desktop\GestureFlow.Desktop.runtimeconfig.json') | ConvertFrom-Json).runtimeOptions.includedFrameworks
    $assetFile = Join-Path $workspace 'GestureFlow.Desktop\obj\project.assets.json'
    $packageFolders = (Get-Content -Raw -LiteralPath $assetFile | ConvertFrom-Json).packageFolders.PSObject.Properties.Name
    $licenses = Join-Path $root 'licenses'
    New-Item -ItemType Directory -Path $licenses | Out-Null
    foreach ($framework in $runtime) {
        $pack = $framework.name.ToLowerInvariant() + '.runtime.win-x64\' + $framework.version
        $package = $packageFolders | ForEach-Object { Join-Path $_ $pack } |
            Where-Object { Test-Path -LiteralPath $_ -PathType Container } | Select-Object -First 1
        if (!$package) { throw "Runtime notices not found for $($framework.name)." }
        Get-ChildItem -LiteralPath $package -File | Where-Object { $_.Name -match '^(LICENSE|THIRD-PARTY-NOTICES)' } |
            ForEach-Object { Copy-Item -LiteralPath $_.FullName -Destination (Join-Path $licenses "$($framework.name)-$($_.Name)") }
    }

    $archive = Join-Path $artifactDir "$packageName.zip"
    $workingArchive = Join-Path $stage "$packageName.zip"
    Compress-Archive -LiteralPath $root -DestinationPath $workingArchive -CompressionLevel Optimal
    [IO.File]::Move($workingArchive, $archive, $true)
    $hash = (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash.ToLowerInvariant()
    Set-Content -LiteralPath "$archive.sha256" -Value "$hash  $packageName.zip" -Encoding ascii
    Write-Output $archive
    Write-Output "$archive.sha256"
} finally {
    Pop-Location
    $resolved = (Resolve-Path -LiteralPath $stage).Path
    $expectedParent = (Resolve-Path -LiteralPath $artifactDir).Path
    if ((Split-Path $resolved -Parent) -eq $expectedParent -and (Split-Path $resolved -Leaf) -match '^stage-[0-9a-f]{32}$') {
        Remove-Item -LiteralPath $resolved -Recurse -Force
    }
}
