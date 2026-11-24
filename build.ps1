# Builds KosherLink-Google.exe using the C# compiler that ships with Windows (.NET Framework 4.8) - nothing to install.
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$fw = "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319"
$csc = Join-Path $fw 'csc.exe'
$out = Join-Path $root 'dist'
New-Item -ItemType Directory -Force $out | Out-Null

# app icon (green circle with K)
$ico = Join-Path $out 'app.ico'
Add-Type -AssemblyName System.Drawing
$bmp = New-Object System.Drawing.Bitmap 64, 64
$g = [System.Drawing.Graphics]::FromImage($bmp)
$g.SmoothingMode = 'AntiAlias'
$g.FillEllipse((New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(26,115,232))), 2, 2, 60, 60)
$sf = New-Object System.Drawing.StringFormat; $sf.Alignment = 'Center'; $sf.LineAlignment = 'Center'
$g.DrawString('K', (New-Object System.Drawing.Font 'Segoe UI', 30, 'Bold', 'Pixel'), [System.Drawing.Brushes]::White, (New-Object System.Drawing.RectangleF 0, 2, 64, 62), $sf)
$icon = [System.Drawing.Icon]::FromHandle($bmp.GetHicon())
$fs = [IO.File]::Create($ico); $icon.Save($fs); $fs.Close()

$refs = 'System.dll','System.Core.dll','System.Drawing.dll','System.Windows.Forms.dll','System.Xml.dll',
        'System.Xml.Linq.dll','System.IO.Compression.dll','System.IO.Compression.FileSystem.dll' |
        ForEach-Object { "/r:$(Join-Path $fw $_)" }

& $csc /nologo /target:winexe /optimize+ /codepage:65001 /platform:anycpu "/win32icon:$ico" "/win32manifest:$(Join-Path $root 'src\app.manifest')" `
    "/out:$(Join-Path $out 'KosherLink-Google.exe')" @refs (Get-ChildItem (Join-Path $root 'src') -Filter *.cs).FullName
if ($LASTEXITCODE -ne 0) { throw "Build failed" }
Remove-Item $ico
Write-Host "Built: $(Join-Path $out 'KosherLink-Google.exe')"
