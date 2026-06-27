# Runbook: Tesla-trusted HTTPS cert (Let's Encrypt + Cloudflare DNS-01)

Provisions a publicly-trusted certificate for **`my.thelpers.com`** and binds it to `0.0.0.0:8443`
so the Tesla in-car browser loads TeslaPC over HTTPS with no warning. The app then prefers this
cert automatically (`--https-host my.thelpers.com`); it never creates or deletes it.

## Prerequisites (done)

- Cloudflare DNS: `my.thelpers.com` **A → 100.64.0.1**, **DNS-only (grey cloud)**, not proxied.
- Cloudflare API token with **DNS edit** on `thelpers.com`. Store it only where noted below;
  never commit it. (Token has no expiry — rotate after setup.)

## 1. Install win-acme (laptop, elevated)

```
winget install --id WinAcme.win-acme   # or download wacs.exe from win-acme.com
```

## 2. Post-renewal rebind script

Save as `C:\teslapc\rebind-cert.ps1` (outside the repo):

```powershell
param([Parameter(Mandatory=$true)][string]$Thumbprint)
$ipport = '0.0.0.0:8443'
$appid  = '{A253521A-C31E-457C-AADD-C0E42A87EA0F}'   # same AppId TeslaPC uses
& netsh http delete sslcert ipport=$ipport 2>$null | Out-Null
& netsh http add sslcert ipport=$ipport certhash=$Thumbprint appid=$appid
```

## 3. Issue the certificate (elevated)

Run `wacs.exe` once to create the order + auto-renew scheduled task. Provide the Cloudflare token
at the prompt (or via the parameter shown). The token is stored in win-acme's encrypted config.

```
wacs.exe --source manual --host my.thelpers.com ^
  --validation cloudflare --cloudflareapitoken <TOKEN> ^
  --store certificatestore --certificatestore My ^
  --installation script ^
  --script "C:\teslapc\rebind-cert.ps1" --scriptparameters "{CertThumbprint}" ^
  --accepttos --emailaddress <you@example.com>
```

win-acme installs the cert into `LocalMachine\My`, runs the rebind script, and registers a
scheduled task ("win-acme renew ...") that renews ~every 60 days and re-runs the script.

## 4. Run TeslaPC with the host

Add `--https-host my.thelpers.com` to the launcher (`run-full.bat` / desktop `Start-TeslaPC.bat`).
On startup `SslCertificateBootstrap.TryUseTrustedCertificate` finds and binds the cert; if it's not
present yet it falls back to self-signed.

## 5. Verify

```
netsh http show sslcert ipport=0.0.0.0:8443           # thumbprint = the LE cert
curl https://my.thelpers.com:8443/                    # 200, no -k, valid chain
```
On the Tesla (on the hotspot): browse `https://my.thelpers.com:8443/` → padlock, no warning.

## Renewal / failure notes

- Renewal is DNS-01 (no inbound needed) but requires the laptop to have internet at renewal time.
- Force a test renewal: `wacs.exe --renew --force` then re-verify the binding.
- If a specific (old) Tesla still distrusts Let's Encrypt, switch the ACME CA to ZeroSSL
  (`--baseuri https://acme.zerossl.com/v2/DV90` + EAB creds) — same flow.
- DNS-rebind protection on the Tesla's resolver could strip the private-IP answer; if so, add a
  local DNS resolver on the hotspot mapping `my.thelpers.com → 100.64.0.1`.
