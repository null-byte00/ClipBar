$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$art  = Join-Path $root 'artifacts'
$app  = Join-Path $art 'app'
$zip  = Join-Path $art 'payload.zip'
$dist = Join-Path $root 'dist'

foreach ($tool in 'ffmpeg.exe', 'ffprobe.exe') {
    if (-not (Test-Path (Join-Path $root "tools\$tool"))) {
        throw "tools\$tool not found. Run tools\get-ffmpeg.ps1 first."
    }
}

Remove-Item $art -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory $art, $dist -Force | Out-Null

Write-Host '1/3  Publishing ClipBar...' -ForegroundColor Cyan
dotnet publish (Join-Path $root 'src\ClipBar\ClipBar.csproj') -c Release -r win-x64 --self-contained true `
    -p:PublishReadyToRun=true -p:DebugType=none -o $app --nologo -v q
if ($LASTEXITCODE -ne 0) { throw 'dotnet publish ClipBar failed' }
if (-not (Test-Path (Join-Path $app 'tools\ffmpeg.exe'))) { throw 'ffmpeg was not copied into the publish output' }
Copy-Item (Join-Path $root 'tools\FFMPEG-LICENSE.txt') (Join-Path $app 'tools') -ErrorAction SilentlyContinue

Write-Host '2/3  Packing payload...' -ForegroundColor Cyan
Add-Type -AssemblyName System.IO.Compression, System.IO.Compression.FileSystem
$archive = [IO.Compression.ZipFile]::Open($zip, [IO.Compression.ZipArchiveMode]::Create)
try {
    Get-ChildItem $app -Recurse -File | ForEach-Object {
        $rel = $_.FullName.Substring($app.Length + 1) -replace '\\', '/'
        [void][IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive, $_.FullName, $rel, [IO.Compression.CompressionLevel]::Optimal)
    }
}
finally { $archive.Dispose() }

Write-Host '3/3  Building installer...' -ForegroundColor Cyan
dotnet publish (Join-Path $root 'src\ClipBar.Setup\ClipBar.Setup.csproj') -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true `
    -p:DebugType=none -p:PayloadZip="$zip" -o (Join-Path $art 'setup') --nologo -v q
if ($LASTEXITCODE -ne 0) { throw 'dotnet publish ClipBar.Setup failed' }

$version = (Get-Item (Join-Path $app 'ClipBar.exe')).VersionInfo.ProductVersion -replace '\+.*$', ''
$out = Join-Path $dist "ClipBar-Setup-$version.exe"
Move-Item (Join-Path $art 'setup\ClipBar-Setup.exe') $out -Force
$mb = [math]::Round((Get-Item $out).Length / 1MB, 1)
Write-Host "Done: $out ($mb MB)" -ForegroundColor Green
