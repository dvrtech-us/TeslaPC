@echo off
:: Generates a self-signed SSL certificate and binds it to the HTTPS ports.
:: Must be run as Administrator.

set APPID={A253521A-C31E-457C-AADD-C0E42A87EA0F}

echo Generating self-signed certificate...
powershell -NoProfile -Command ^
  "$cert = New-SelfSignedCertificate -DnsName 'localhost','TeslaPC' -CertStoreLocation Cert:\LocalMachine\My -NotAfter (Get-Date).AddYears(5) -FriendlyName 'TeslaPC Dev Cert'; ^
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

echo Binding certificate to HTTPS ports...
netsh http add sslcert ipport=0.0.0.0:8443 certhash=%THUMBPRINT% appid=%APPID%
netsh http add sslcert ipport=0.0.0.0:8444 certhash=%THUMBPRINT% appid=%APPID%
netsh http add sslcert ipport=0.0.0.0:8445 certhash=%THUMBPRINT% appid=%APPID%

echo.
echo Done. Certificate bound to ports 8443, 8444, 8445.
echo Note: Browsers will show a security warning for self-signed certs.
pause
