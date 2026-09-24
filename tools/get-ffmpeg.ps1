$ErrorActionPreference = 'Stop'
$url  = 'https://www.gyan.dev/ffmpeg/builds/ffmpeg-release-essentials.zip'
$tmp  = Join-Path $env:TEMP 'clipbar-ffmpeg.zip'
$dest = $PSScriptRoot

Write-Host "Downloading $url ..."
Invoke-WebRequest $url -OutFile $tmp -UseBasicParsing
Add-Type -AssemblyName System.IO.Compression.FileSystem
$zip = [IO.Compression.ZipFile]::OpenRead($tmp)
try {
    foreach ($name in 'ffmpeg.exe', 'ffprobe.exe') {
        $entry = $zip.Entries | Where-Object { $_.Name -eq $name } | Select-Object -First 1
        if (-not $entry) { throw "$name not found in the archive" }
        [IO.Compression.ZipFileExtensions]::ExtractToFile($entry, (Join-Path $dest $name), $true)
        Write-Host "  $name"
    }
}
finally { $zip.Dispose(); Remove-Item $tmp -ErrorAction SilentlyContinue }
& (Join-Path $dest 'ffmpeg.exe') -hide_banner -version | Select-Object -First 1
