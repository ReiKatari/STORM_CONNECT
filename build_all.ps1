# STORM CONNECT 1.0.0 - Automated Low-CPU Build, Sign & Package Pipeline
$ErrorActionPreference = "Stop"

# Lower process priority to avoid OS lag (Law 24)
try { [System.Diagnostics.Process]::GetCurrentProcess().PriorityClass = [System.Diagnostics.ProcessPriorityClass]::BelowNormal } catch {}

$baseDir = $PSScriptRoot
if (-not $baseDir) { $baseDir = "f:\STORM CONNECT" }

$appVersion = "1.3.3"
$appDisplayName = "STORM CONNECT"
$filesDir = Join-Path $baseDir "Files"
$secretsDir = "F:\STORM ALL PROJECTS\.storm-secrets"
$desktopProj = Join-Path $baseDir "src\StormConnect.Desktop\StormConnect.Desktop.csproj"
$installerProj = Join-Path $baseDir "installer\StormInstaller\StormInstaller.csproj"
$payloadDir = Join-Path $baseDir "installer\StormInstaller\Payload"
$testsProj = Join-Path $baseDir "tests\StormConnect.Tests\StormConnect.Tests.csproj"
$androidDir = Join-Path $baseDir "android"

New-Item -ItemType Directory -Path $filesDir -Force | Out-Null
Get-ChildItem $filesDir -Filter "STORM_CONNECT_*" -ErrorAction SilentlyContinue | Remove-Item -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Path $payloadDir -Force | Out-Null
Get-Process "STORM CONNECT", "STORM_CONNECT_*" -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue

Write-Host "=================================================================" -ForegroundColor Cyan
Write-Host "   $appDisplayName $appVersion - FULL PRODUCTION PIPELINE" -ForegroundColor Cyan
Write-Host "=================================================================" -ForegroundColor Cyan

# 1. Run Unit & Integration Tests
Write-Host "[1/6] Running automated test suite..." -ForegroundColor Yellow
dotnet test $testsProj -c Release -m:4
if ($LASTEXITCODE -ne 0) { throw "Unit tests failed!" }

# 2. Locate STORM SERTIFICATE in CurrentUser\My
$certThumb = $null
$propsPath = Join-Path $secretsDir "storm_signing.properties"
if (Test-Path $propsPath) {
    Get-Content $propsPath | ForEach-Object {
        if ($_ -match "^STORM_CERT_THUMBPRINT=(.+)$") { $certThumb = $Matches[1].Trim() }
    }
}
$store = [System.Security.Cryptography.X509Certificates.X509Store]::new('My', 'CurrentUser')
$store.Open('ReadOnly')
$cert = $store.Certificates | Where-Object {
    $_.HasPrivateKey -and ($_.Thumbprint -eq $certThumb -or $_.Subject -like "*CN=STORM TEAM*" -or $_.Subject -like "*CN=STORM SERTIFICATE*")
} | Select-Object -First 1
$store.Close()

$cerPath = Join-Path $payloadDir "STORM_SERTIFICATE.cer"
if ($cert) {
    $certThumb = $cert.Thumbprint
    Write-Host "[CERT] Found: $($cert.Subject) [$certThumb]" -ForegroundColor Green
    [System.IO.File]::WriteAllBytes($cerPath, $cert.Export([System.Security.Cryptography.X509Certificates.X509ContentType]::Cert))
}

$signtool = Get-ChildItem "C:\Program Files (x86)\Windows Kits\10\bin\*\x64\signtool.exe" -ErrorAction SilentlyContinue |
    Sort-Object FullName -Descending | Select-Object -First 1 -ExpandProperty FullName

# 3. Publish Self-Contained Windows x64 Desktop App
Write-Host "[2/6] Publishing Windows x64 Self-Contained Desktop App..." -ForegroundColor Yellow
$pubDir = Join-Path $baseDir "src\StormConnect.Desktop\bin\Release\net10.0-windows\win-x64\publish"
if (Test-Path $pubDir) { Remove-Item $pubDir -Recurse -Force }
dotnet publish $desktopProj -c Release -r win-x64 --self-contained true -m:4 -p:PublishSingleFile=false
if ($LASTEXITCODE -ne 0) { throw "Desktop publish failed!" }

$exePath = Join-Path $pubDir "STORM CONNECT.exe"
if (-not (Test-Path $exePath)) { throw "Published executable not found: $exePath" }

if ($signtool -and $certThumb) {
    Write-Host "[SIGN] Signing STORM CONNECT.exe..." -ForegroundColor Green
    & $signtool sign /fd SHA256 /d "STORM CONNECT" /sha1 $certThumb $exePath | Out-Null
}

# 4. Package Portable ZIP in Files\ (with portable.marker)
Write-Host "[3/6] Creating Portable Archive in Files\..." -ForegroundColor Yellow
$portableStage = Join-Path $baseDir "WORK_PORTABLE_STAGE"
if (Test-Path $portableStage) { Remove-Item $portableStage -Recurse -Force }
Copy-Item $pubDir $portableStage -Recurse -Force
Set-Content -Path (Join-Path $portableStage "portable.marker") -Value "STORM CONNECT $appVersion PORTABLE" -Encoding UTF8
$zipPath = Join-Path $filesDir "STORM_CONNECT_${appVersion}.zip"
if (Test-Path $zipPath) { Remove-Item $zipPath -Force }
Compress-Archive -Path "$portableStage\*" -DestinationPath $zipPath -CompressionLevel Optimal -Force
Remove-Item $portableStage -Recurse -Force

# 5. Build & Sign Unified Dark Windows Installer (STORM_CONNECT_1.0.0_Setup.exe)
Write-Host "[4/6] Building Unified Dark Installer..." -ForegroundColor Yellow
$payloadZip = Join-Path $payloadDir "Payload.zip"
if (Test-Path $payloadZip) { Remove-Item $payloadZip -Force }
Compress-Archive -Path "$pubDir\*" -DestinationPath $payloadZip -CompressionLevel Optimal -Force

dotnet publish $installerProj -c Release -r win-x64 --self-contained true -m:4 -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true
if ($LASTEXITCODE -ne 0) { throw "Installer publish failed!" }

$rawSetupExe = Join-Path $baseDir "installer\StormInstaller\bin\Release\net10.0-windows\win-x64\publish\STORM_CONNECT_${appVersion}_Setup.exe"
$finalSetupExe = Join-Path $filesDir "STORM_CONNECT_${appVersion}_Setup.exe"
Copy-Item $rawSetupExe $finalSetupExe -Force
if ($signtool -and $certThumb) {
    Write-Host "[SIGN] Signing $finalSetupExe..." -ForegroundColor Green
    & $signtool sign /fd SHA256 /d "STORM CONNECT" /sha1 $certThumb $finalSetupExe | Out-Null
}

# Clean temporary payload intermediates
Remove-Item $payloadDir -Recurse -Force -ErrorAction SilentlyContinue

# 6. Build & Verify Android Release APK
Write-Host "[5/6] Building Signed Android APK..." -ForegroundColor Yellow
$env:JAVA_HOME = "C:\Users\ReiKa\.jdks\jdk-21.0.12.1+1"
$env:ANDROID_HOME = "C:\Users\ReiKa\AppData\Local\Android\Sdk"
$env:Path = "$env:JAVA_HOME\bin;$env:Path"

Push-Location $androidDir
try {
    & .\gradlew.bat assembleRelease --max-workers=3 "-Dorg.gradle.priority=low" --no-daemon
    if ($LASTEXITCODE -ne 0) { throw "Android Gradle build failed!" }
} finally {
    Pop-Location
}

$rawApk = Join-Path $androidDir "app\build\outputs\apk\release\app-release.apk"
if (-not (Test-Path $rawApk)) { throw "Release APK not found: $rawApk" }
$finalApk = Join-Path $filesDir "STORM_CONNECT_${appVersion}.apk"
Copy-Item $rawApk $finalApk -Force

$apksigner = Get-ChildItem "$env:ANDROID_HOME\build-tools\*\apksigner.bat" | Sort-Object FullName -Descending | Select-Object -First 1 -ExpandProperty FullName
if ($apksigner) {
    Write-Host "[6/6] Verifying APK signature..." -ForegroundColor Yellow
    & $apksigner verify --verbose $finalApk
    if ($LASTEXITCODE -ne 0) { throw "APK signature verification failed!" }
}

Write-Host "`n=================================================================" -ForegroundColor Green
Write-Host "   STORM CONNECT $appVersion ARTIFACTS READY IN Files\" -ForegroundColor Green
Get-ChildItem $filesDir | ForEach-Object {
    $hash = (Get-FileHash $_.FullName -Algorithm SHA256).Hash
    $mb = [math]::Round($_.Length / 1MB, 2)
    Write-Host ("   {0} ({1} MB) | SHA256: {2}" -f $_.Name, $mb, $hash) -ForegroundColor Cyan
}
Write-Host "=================================================================" -ForegroundColor Green
