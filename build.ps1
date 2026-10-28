$ErrorActionPreference = "Stop"

$projectRoot = "C:\Users\durre\.gemini\antigravity\scratch\RevitEnscapeCloud"
$srcDir      = "$projectRoot\src"
$distDir     = "$projectRoot\dist"
$revitDir    = "C:\Program Files\Autodesk\Revit 2026"
$latestNet   = Get-ChildItem "C:\Program Files\dotnet\shared\Microsoft.NETCore.App" | Sort-Object { [version]$_.Name } -Descending | Select-Object -First 1
$latestWpf   = Get-ChildItem "C:\Program Files\dotnet\shared\Microsoft.WindowsDesktop.App" | Sort-Object { [version]$_.Name } -Descending | Select-Object -First 1
$netDir      = $latestNet.FullName
$wpfDir      = $latestWpf.FullName
$csc         = "C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe"

New-Item -ItemType Directory -Force -Path $distDir | Out-Null
New-Item -ItemType Directory -Force -Path "$distDir\WebViewer" | Out-Null

Write-Host "Compilando RevitEnscapeCloud.dll para Autodesk Revit 2026 (.NET 8.0 x64)..." -ForegroundColor Cyan

$csFiles = @(
    "$srcDir\SceneModels.cs",
    "$srcDir\SceneExportContext.cs",
    "$srcDir\PointCloudReader.cs",
    "$srcDir\LasFileReader.cs",
    "$srcDir\SmFileReader.cs",
    "$srcDir\LiveSyncExternalHandler.cs",
    "$srcDir\RendererWindow.cs",
    "$srcDir\App.cs"
)

$outDll = "$distDir\RevitEnscapeCloud.dll"
try {
    if (Test-Path $outDll) {
        Remove-Item $outDll -Force -ErrorAction Stop
    }
} catch {
    # Revit 2026 is currently open and locking RevitEnscapeCloud.dll; compile to a side-by-side build folder
    $stamp = Get-Date -Format "yyyyMMdd_HHmmss"
    $distDir = "$projectRoot\dist_$stamp"
    New-Item -ItemType Directory -Force -Path "$distDir\WebViewer" | Out-Null
    $outDll = "$distDir\RevitEnscapeCloud.dll"
    Write-Host "Revit 2026 tiene cargado el DLL anterior. Compilando nueva versión en: $outDll" -ForegroundColor Yellow
}

& $csc /nostdlib /noconfig /target:library /platform:x64 /optimize+ `
    /r:"$netDir\System.Private.CoreLib.dll" `
    /r:"$netDir\System.Runtime.dll" `
    /r:"$netDir\mscorlib.dll" `
    /r:"$netDir\netstandard.dll" `
    /r:"$netDir\System.ComponentModel.dll" `
    /r:"$netDir\System.Collections.dll" `
    /r:"$netDir\System.Linq.dll" `
    /r:"$netDir\System.Private.Uri.dll" `
    /r:"$netDir\System.ComponentModel.Primitives.dll" `
    /r:"$netDir\System.ObjectModel.dll" `
    /r:"$netDir\System.IO.Compression.dll" `
    /r:"$netDir\System.Threading.Tasks.dll" `
    /r:"$wpfDir\PresentationFramework.dll" `
    /r:"$wpfDir\PresentationCore.dll" `
    /r:"$wpfDir\WindowsBase.dll" `
    /r:"$wpfDir\System.Xaml.dll" `
    /r:"$revitDir\RevitAPI.dll" `
    /r:"$revitDir\RevitAPIUI.dll" `
    /r:"$revitDir\Microsoft.Web.WebView2.Core.dll" `
    /r:"$revitDir\Microsoft.Web.WebView2.Wpf.dll" `
    /out:"$outDll" `
    $csFiles

if ($LASTEXITCODE -ne 0) {
    throw "Error de compilación en csc.exe (código $LASTEXITCODE)"
}

# Copy updated WebViewer HTML/JS to ALL dist* folders so any currently running Revit 2026 session gets the new UI immediately
Get-ChildItem -Path $projectRoot -Directory -Filter "dist*" | ForEach-Object {
    $wv = Join-Path $_.FullName "WebViewer"
    New-Item -ItemType Directory -Force -Path $wv | Out-Null
    Copy-Item "$srcDir\WebViewer\*" $wv -Recurse -Force -ErrorAction SilentlyContinue
}

# Update .addin manifest to point to the latest compiled DLL for next Revit startup
$addinXml = @"
<?xml version="1.0" encoding="utf-8" standalone="no"?>
<RevitAddIns>
  <AddIn Type="Application">
    <Name>EnscapeCloud RT — Renderizador + Nube de Puntos (Revit 2026)</Name>
    <Assembly>$outDll</Assembly>
    <AddInId>8D94A1E0-5F3C-4B98-9D42-7A1E2B3C4D5E</AddInId>
    <FullClassName>RevitEnscapeCloud.App</FullClassName>
    <VendorId>ENSCAPECLOUD</VendorId>
    <VendorDescription>EnscapeCloud Real-Time Renderer with Point Cloud Density Control for Revit 2026</VendorDescription>
  </AddIn>
</RevitAddIns>
"@
Set-Content -Path "$projectRoot\RevitEnscapeCloud.addin" -Value $addinXml -Encoding UTF8

$userAddinsDir = "$env:APPDATA\Autodesk\Revit\Addins\2026"
New-Item -ItemType Directory -Force -Path $userAddinsDir | Out-Null
Copy-Item "$projectRoot\RevitEnscapeCloud.addin" "$userAddinsDir\RevitEnscapeCloud.addin" -Force

# Compilar RevitEnscapeCloud.exe (Lanzador Autónomo & Gestor de Instalación)
$launcherSrc = "$srcDir\Launcher\Program.cs"
if (Test-Path $launcherSrc) {
    Write-Host "Compilando RevitEnscapeCloud.exe..." -ForegroundColor Cyan
    & $csc /target:winexe /platform:x64 /optimize+ `
        /r:System.Windows.Forms.dll `
        /r:System.Drawing.dll `
        /r:System.dll `
        /r:System.Core.dll `
        /out:"$projectRoot\RevitEnscapeCloud.exe" `
        "$launcherSrc"
    Copy-Item "$projectRoot\RevitEnscapeCloud.exe" "$distDir\RevitEnscapeCloud.exe" -Force -ErrorAction SilentlyContinue
}

Write-Host "Compilación exitosa: $outDll" -ForegroundColor Green
Write-Host "Ejecutable listo: $projectRoot\RevitEnscapeCloud.exe" -ForegroundColor Green
Write-Host "Add-in actualizado en: $userAddinsDir\RevitEnscapeCloud.addin" -ForegroundColor Green
