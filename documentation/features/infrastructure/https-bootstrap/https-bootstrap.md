# HTTPS

TLS is terminated in-process with rustls (`certs.rs`). There is no http.sys URL ACL and no certificate-store bind.

When `TESLAPC_HTTPS_HOST` (or `--https-host`) and `TESLAPC_CF_TOKEN` are set, and the process is not `--localhost`, `maybe_issue` runs DNS-01 through Cloudflare. `needs_renewal` is true when `days_left <= 30` (`RENEW_WHEN_DAYS_LEFT`). `renewal_loop` waits `RENEWAL_CHECK_HOURS` (`12`) and reloads the `ServerConfig` after a new certificate is stored. `TESLAPC_ACME_STAGING=1` selects `https://acme-staging-v02.api.letsencrypt.org/directory`. Otherwise the production directory is used.

When no public hostname is set, `ensure_local_cert` writes a generated certificate so `8443` still listens. Files are `cert.pem` and `key.pem` in the config directory: `%ProgramData%\TeslaPC\` on Windows, `$XDG_CONFIG_HOME/teslapc/` (default `~/.config/teslapc/`) on Linux. The ACME account is `acme-account.json` in that directory.

A failed issuance is logged. The previous or local certificate stays in place. `--localhost` does not listen on `8443`.
