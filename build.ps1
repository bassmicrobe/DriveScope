param([string]$ExecutableName = 'DriveScope.exe')
$ErrorActionPreference = 'Stop'
$appRoot = $PSScriptRoot
$framework = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
$compiler = Join-Path $framework 'csc.exe'
$wpf = Join-Path $framework 'WPF'
$references = @('PresentationFramework.dll', 'PresentationCore.dll', 'WindowsBase.dll') | ForEach-Object { '/reference:' + (Join-Path $wpf $_) }
$references += '/reference:System.Xaml.dll'
$references += '/reference:System.Core.dll'
$references += '/reference:System.IO.Compression.dll'
& $compiler /nologo /target:winexe /platform:x64 /optimize+ /utf8output ('/out:' + (Join-Path $appRoot $ExecutableName)) ('/win32manifest:' + (Join-Path $appRoot 'src\app.manifest')) ('/win32icon:' + (Join-Path $appRoot 'assets\branding\drivescope.ico')) ('/resource:' + (Join-Path $appRoot 'assets\branding\drivescope.ico') + ',Branding.Icon') ('/resource:' + (Join-Path $appRoot 'assets\branding\drivescope-logo-local.png') + ',Branding.Logo') ('/resource:' + (Join-Path $appRoot 'src\MainWindow.xaml') + ',MainWindow.xaml') @references (Join-Path $appRoot 'src\Scanner.cs') (Join-Path $appRoot 'src\DriveManagement.cs') (Join-Path $appRoot 'src\App.cs') (Join-Path $appRoot 'src\Verification.cs')
if ($LASTEXITCODE -ne 0) { throw 'Build failed' }
if ($ExecutableName -ne 'DriveScope.exe') { Copy-Item -LiteralPath (Join-Path $appRoot 'DriveScope.exe.config') -Destination ((Join-Path $appRoot $ExecutableName) + '.config') -Force }
Write-Output ('Built: ' + (Join-Path $appRoot $ExecutableName))
