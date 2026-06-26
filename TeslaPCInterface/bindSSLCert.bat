@echo off
:: Generates a self-signed SSL certificate and binds it to the unified HTTPS port (8443).
:: Must be run as Administrator.

set APPID={A253521A-C31E-457C-AADD-C0E42A87EA0F}

echo Generating self-signed certificate...
powershell -NoProfile -Command ^
  "$cert = New-SelfSignedCertificate -DnsName 'localhost','TeslaPC' -CertStoreLocation Cert:\LocalMachine\My -KeyExportPolicy Exportable -NotAfter (Get-Date).AddYears(5) -FriendlyName 'TeslaPC Dev Cert'; ^
   certutil -repairstore my $cert.Thumbprint | Out-Null; ^
   $rsa = [System.Security.Cryptography.X509Certificates.RSACertificateExtensions]::GetRSAPrivateKey($cert); ^
   if ($rsa -is [System.Security.Cryptography.RSACryptoServiceProvider]) { $path = Join-Path $env:ProgramData ('Microsoft\Crypto\RSA\MachineKeys\' + $rsa.CspKeyContainerInfo.UniqueKeyContainerName) } ^
   elseif ($rsa -is [System.Security.Cryptography.RSACng]) { $path = Join-Path $env:ProgramData ('Microsoft\Crypto\Keys\' + $rsa.Key.UniqueName) } ^
   else { $path = $null }; ^
   if ($path) { icacls $path /grant 'NETWORK SERVICE:R' 'NT AUTHORITY\SYSTEM:R' 'NT AUTHORITY\LOCAL SERVICE:R' | Out-Null }; ^
   $cert.Thumbprint" > "%TEMP%\teslapc_thumbprint.txt"

set /p THUMBPRINT=<"%TEMP%\teslapc_thumbprint.txt"
del "%TEMP%\teslapc_thumbprint.txt"

if "%THUMBPRINT%"=="" (
    echo ERROR: Certificate generation failed.
    pause
    exit /b 1
)

echo Certificate thumbprint: %THUMBPRINT%

:: Remove any existing bindings (ignore errors if none exist)
netsh http delete sslcert ipport=0.0.0.0:8443 >nul 2>&1
netsh http delete sslcert ipport=0.0.0.0:8444 >nul 2>&1
netsh http delete sslcert ipport=0.0.0.0:8445 >nul 2>&1

echo Binding certificate to HTTPS port 8443...
netsh http add sslcert ipport=0.0.0.0:8443 certhash=%THUMBPRINT% appid=%APPID%
if errorlevel 1 (
    echo ERROR: Failed to bind certificate to port 8443.
    pause
    exit /b 1
)

echo.
echo Done. Certificate bound to port 8443.
echo Access the app at: https://localhost:8443/
echo Note: Browsers will show a security warning for self-signed certs.
pause