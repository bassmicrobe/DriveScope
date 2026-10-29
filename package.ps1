$ErrorActionPreference = 'Stop'
$appRoot = $PSScriptRoot

& (Join-Path $appRoot 'build.ps1')

$testOutput = Join-Path $appRoot 'artifacts\verification'
New-Item -ItemType Directory -Path $testOutput -Force | Out-Null
$testProcess = Start-Process -FilePath (Join-Path $appRoot 'DriveScope.exe') -ArgumentList @('--self-test', ('"' + $testOutput + '"')) -WindowStyle Hidden -Wait -PassThru
if ($testProcess.ExitCode -ne 0) {
    $reportPath = Join-Path $testOutput 'self-test.txt'
    if (Test-Path -LiteralPath $reportPath) { Get-Content -LiteralPath $reportPath }
    throw ('Self-test failed with exit code ' + $testProcess.ExitCode)
}

$distributionDirectory = Join-Path $appRoot 'dist'
New-Item -ItemType Directory -Path $distributionDirectory -Force | Out-Null
$packagePath = Join-Path $distributionDirectory 'DriveScope-portable.zip'
Add-Type -AssemblyName System.IO.Compression, System.IO.Compression.FileSystem
$temporaryPackage = Join-Path $distributionDirectory ('DriveScope-portable.' + [guid]::NewGuid().ToString('N') + '.zip')
$archive = [System.IO.Compression.ZipFile]::Open($temporaryPackage, [System.IO.Compression.ZipArchiveMode]::Create)
try {
    foreach ($entryName in @(
        'DriveScope.exe',
        'DriveScope.exe.config',
        'README.md',
        'LICENSE',
        'docs/usage.md',
        'docs/development.md',
        'docs/audits/2026-10-29.md',
        'assets/branding/drivescope-logo-local.svg',
        'assets/branding/drivescope-logo-on-light-local.svg',
        'assets/screenshots/drivescope-overview.png',
        'assets/social/drivescope-ogp.jpg'
    )) {
        [System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive, (Join-Path $appRoot $entryName), $entryName, [System.IO.Compression.CompressionLevel]::Optimal) | Out-Null
    }
} finally { $archive.Dispose() }
if (Test-Path -LiteralPath $packagePath) { [System.IO.File]::Replace($temporaryPackage, $packagePath, [NullString]::Value) }
else { [System.IO.File]::Move($temporaryPackage, $packagePath) }
Write-Output ('Tests passed. Packaged: ' + $packagePath)
