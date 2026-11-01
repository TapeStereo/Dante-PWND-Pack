param(
    [string]$PayloadRoot = (Join-Path $PSScriptRoot 'payload'),
    [string]$OutputDirectory = (Join-Path $PSScriptRoot 'dist')
)
$ErrorActionPreference = 'Stop'
$taskRoot = $PSScriptRoot
$buildRoot = Join-Path $taskRoot 'build'
$stageRoot = Join-Path $buildRoot 'payload'
$checkedStage = [System.IO.Path]::GetFullPath($stageRoot)
$allowedPrefix = [System.IO.Path]::GetFullPath($taskRoot).TrimEnd('\') + '\'
if (-not $checkedStage.StartsWith($allowedPrefix, [System.StringComparison]::OrdinalIgnoreCase)) { throw 'Build staging is outside the project.' }
if (Test-Path -LiteralPath $checkedStage) { Remove-Item -LiteralPath $checkedStage -Recurse -Force }
New-Item -ItemType Directory -Path $buildRoot, $stageRoot, $OutputDirectory -Force | Out-Null
Add-Type -AssemblyName System.IO.Compression.FileSystem
Add-Type -AssemblyName System.Drawing
$sourceService = Join-Path $PayloadRoot 'dvs_service.exe'
$sourceLicensing = Join-Path $PayloadRoot 'Licensing'
if (-not (Test-Path -LiteralPath $sourceService -PathType Leaf)) { throw 'Missing dvs_service.exe' }
if (-not (Test-Path -LiteralPath $sourceLicensing -PathType Container)) { throw 'Missing Licensing folder' }
Copy-Item -LiteralPath $sourceService -Destination (Join-Path $stageRoot 'dvs_service.exe') -Force
New-Item -ItemType Directory -Path (Join-Path $stageRoot 'Licensing') -Force | Out-Null
$licenseFiles = @(Get-ChildItem -LiteralPath $sourceLicensing -File -Recurse | Sort-Object FullName)
foreach ($licenseFile in $licenseFiles) {
    $relative = $licenseFile.FullName.Substring($sourceLicensing.Length).TrimStart('\')
    $target = Join-Path (Join-Path $stageRoot 'Licensing') $relative
    New-Item -ItemType Directory -Path (Split-Path -Parent $target) -Force | Out-Null
    Copy-Item -LiteralPath $licenseFile.FullName -Destination $target -Force
}
$records = @()
$allFiles = @((Get-Item -LiteralPath (Join-Path $stageRoot 'dvs_service.exe'))) + @(Get-ChildItem -LiteralPath (Join-Path $stageRoot 'Licensing') -File -Recurse | Sort-Object FullName)
if ($allFiles.Count -ne 4) { throw 'Expected exactly four payload files for this release.' }
foreach ($payloadFile in $allFiles) {
    $relative = $payloadFile.FullName.Substring($stageRoot.Length).TrimStart('\').Replace('\','/')
    if ($relative.Contains('"')) { throw 'Unsupported payload filename.' }
    $hash = (Get-FileHash -LiteralPath $payloadFile.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
    $records += 'new PayloadFile("' + $relative + '", "' + $hash + '", ' + $payloadFile.Length + 'L)'
}
$buildInfo = @'
using System.Reflection;
[assembly: AssemblyTitle("DVS Repair")]
[assembly: AssemblyDescription("Portable DVS file replacement utility")]
[assembly: AssemblyProduct("DVS Repair")]
[assembly: AssemblyVersion("1.0.0.0")]
[assembly: AssemblyFileVersion("1.0.0.0")]
namespace DvsRepair { internal static class BuildInfo { internal static readonly PayloadFile[] Files = {
'@
$buildInfo += "`r`n" + ($records -join ",`r`n") + "`r`n}; } }`r`n"
[System.IO.File]::WriteAllText((Join-Path $buildRoot 'BuildInfo.cs'), $buildInfo, [System.Text.UTF8Encoding]::new($false))
$zipPath = Join-Path $buildRoot 'payload.zip'
if (Test-Path -LiteralPath $zipPath) { Remove-Item -LiteralPath $zipPath -Force }
[System.IO.Compression.ZipFile]::CreateFromDirectory($stageRoot, $zipPath, [System.IO.Compression.CompressionLevel]::Optimal, $false)
$logoPath = Join-Path $taskRoot 'assets\logo.png'
if (-not (Test-Path -LiteralPath $logoPath -PathType Leaf)) { throw 'Missing assets/logo.png' }
$originalLogo = [System.Drawing.Image]::FromFile($logoPath)
$iconSizes = @(16,24,32,48,64,128,256)
$iconImages = [System.Collections.Generic.List[byte[]]]::new()
try {
    foreach ($iconSize in $iconSizes) {
        $bitmap = [System.Drawing.Bitmap]::new($iconSize,$iconSize,[System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
        $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
        $imageStream = [System.IO.MemoryStream]::new()
        try {
            $graphics.Clear([System.Drawing.Color]::Transparent)
            $graphics.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
            $graphics.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
            $graphics.CompositingQuality = [System.Drawing.Drawing2D.CompositingQuality]::HighQuality
            $graphics.DrawImage($originalLogo,[System.Drawing.Rectangle]::new(0,0,$iconSize,$iconSize))
            $bitmap.Save($imageStream,[System.Drawing.Imaging.ImageFormat]::Png)
            $iconImages.Add($imageStream.ToArray())
        } finally { $imageStream.Dispose(); $graphics.Dispose(); $bitmap.Dispose() }
    }
} finally { $originalLogo.Dispose() }
$iconStream = [System.IO.File]::Create((Join-Path $buildRoot 'app.ico'))
$iconWriter = [System.IO.BinaryWriter]::new($iconStream)
try {
    $iconWriter.Write([uint16]0)
    $iconWriter.Write([uint16]1)
    $iconWriter.Write([uint16]$iconSizes.Count)
    $iconOffset = 6 + 16 * $iconSizes.Count
    for ($index = 0; $index -lt $iconSizes.Count; $index++) {
        $dimensionByte = if ($iconSizes[$index] -eq 256) { 0 } else { $iconSizes[$index] }
        $iconWriter.Write([byte]$dimensionByte)
        $iconWriter.Write([byte]$dimensionByte)
        $iconWriter.Write([byte]0)
        $iconWriter.Write([byte]0)
        $iconWriter.Write([uint16]1)
        $iconWriter.Write([uint16]32)
        $iconWriter.Write([uint32]$iconImages[$index].Length)
        $iconWriter.Write([uint32]$iconOffset)
        $iconOffset += $iconImages[$index].Length
    }
    foreach ($iconImage in $iconImages) { $iconWriter.Write([byte[]]$iconImage) }
} finally { $iconWriter.Dispose(); $iconStream.Dispose() }
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path -LiteralPath $compiler)) { $compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe' }
$outputExe = Join-Path $OutputDirectory 'DVS-Repair-v1.0.exe'
$compilerArgs = @('/nologo','/target:winexe','/platform:anycpu','/optimize+','/codepage:65001','/utf8output',('/out:' + $outputExe),('/win32manifest:' + (Join-Path $taskRoot 'app.manifest')),('/win32icon:' + (Join-Path $buildRoot 'app.ico')),('/resource:' + $zipPath + ',DvsRepair.Payload.zip'),'/reference:System.dll','/reference:System.Core.dll','/reference:System.Drawing.dll','/reference:System.Windows.Forms.dll','/reference:System.ServiceProcess.dll','/reference:System.IO.Compression.dll','/reference:System.IO.Compression.FileSystem.dll',(Join-Path $buildRoot 'BuildInfo.cs'))
$compilerArgs += @(Get-ChildItem -LiteralPath (Join-Path $taskRoot 'src') -Filter '*.cs' | Sort-Object Name | Select-Object -ExpandProperty FullName)
$compilerArgs += '/resource:' + $logoPath + ',DvsRepair.Logo.png'
& $compiler $compilerArgs
if ($LASTEXITCODE -ne 0) { throw ('C# compiler failed: ' + $LASTEXITCODE) }
Get-Item -LiteralPath $outputExe | Select-Object FullName,Length
Get-FileHash -LiteralPath $outputExe -Algorithm SHA256 | Select-Object Hash
