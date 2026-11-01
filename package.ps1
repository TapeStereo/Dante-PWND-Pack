param([string]$OutputDirectory = (Join-Path $PSScriptRoot 'dist'))
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression.FileSystem
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
$packageName = 'DVS-Repair-v1.0'
$exePath = Join-Path $OutputDirectory ($packageName + '.exe')
if (-not (Test-Path -LiteralPath $exePath -PathType Leaf)) { throw 'Build the EXE first.' }
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'README.txt') -Destination (Join-Path $OutputDirectory ($packageName + '-README.txt')) -Force
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'reports\desktop.png') -Destination (Join-Path $OutputDirectory ($packageName + '-preview.png')) -Force
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'reports\self-test.txt') -Destination (Join-Path $OutputDirectory ($packageName + '-tests.txt')) -Force
$sourceZip = Join-Path $OutputDirectory ($packageName + '-source.zip')
if (Test-Path -LiteralPath $sourceZip) { Remove-Item -LiteralPath $sourceZip -Force }
$sourceFiles = @('README.md','README.txt','PRODUCT.md','DESIGN.md','build.ps1','package.ps1','app.manifest','.gitignore','.gitattributes','release-notes.md','.impeccable\design.json','assets\logo.png','assets\logo.png.json','docs\screenshot.png')
$sourceFiles += @(Get-ChildItem -LiteralPath (Join-Path $PSScriptRoot 'src') -Filter '*.cs' -File | ForEach-Object { 'src\' + $_.Name })
$zip = [System.IO.Compression.ZipFile]::Open($sourceZip, [System.IO.Compression.ZipArchiveMode]::Create)
try {
    foreach ($source in $sourceFiles) {
        $sourcePath = Join-Path $PSScriptRoot $source
        if (-not (Test-Path -LiteralPath $sourcePath -PathType Leaf)) { throw ('Missing source file: ' + $source) }
        [void][System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip, $sourcePath, $source.Replace('\','/'), [System.IO.Compression.CompressionLevel]::Optimal)
    }
} finally { $zip.Dispose() }
$checksums = @(Get-ChildItem -LiteralPath $OutputDirectory -Filter ($packageName + '*') -File | Where-Object Extension -ne '.sha256' | Sort-Object Name | ForEach-Object { (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant() + '  ' + $_.Name })
[System.IO.File]::WriteAllLines((Join-Path $OutputDirectory ($packageName + '.sha256')), $checksums, [System.Text.Encoding]::ASCII)
Get-ChildItem -LiteralPath $OutputDirectory -Filter ($packageName + '*') -File | Select-Object Name,Length
