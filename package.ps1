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
Compress-Archive -LiteralPath @(
    (Join-Path $appRoot 'DriveScope.exe'),
    (Join-Path $appRoot 'DriveScope.exe.config'),
    (Join-Path $appRoot 'README.md'),
    (Join-Path $appRoot 'LICENSE')
) -DestinationPath $packagePath -Force
Write-Output ('Tests passed. Packaged: ' + $packagePath)
