//! In-process rustls. A generated local certificate keeps 8443 up.
//! DNS-01 (Cloudflare) runs when a hostname and token are set, and again when
//! fewer than or equal to 30 days remain. The check loop waits 12 hours.

use std::io::Cursor;
use std::sync::{Arc, RwLock};
use std::time::Duration;

use rcgen::generate_simple_self_signed;
use rustls::pki_types::{CertificateDer, PrivateKeyDer};
use rustls::ServerConfig;
use x509_parser::prelude::*;

use crate::decisions::renewal::{acme_directory_url, needs_renewal, staging_from_env, RENEWAL_CHECK_HOURS};
use crate::paths;
use crate::state::App;

pub type CertSlot = Arc<RwLock<Arc<ServerConfig>>>;

pub fn install_provider() {
    let _ = rustls::crypto::aws_lc_rs::default_provider().install_default();
}

pub fn ensure_local_cert() -> Result<(), String> {
    if paths::cert_pem().is_file() && paths::key_pem().is_file() {
        return Ok(());
    }
    if let Some(dir) = paths::cert_pem().parent() {
        std::fs::create_dir_all(dir).map_err(|e| e.to_string())?;
    }
    let certified = generate_simple_self_signed(vec!["localhost".to_string(), "TeslaPC".to_string()]).map_err(|e| e.to_string())?;
    std::fs::write(paths::cert_pem(), certified.cert.pem()).map_err(|e| e.to_string())?;
    std::fs::write(paths::key_pem(), certified.signing_key.serialize_pem()).map_err(|e| e.to_string())?;
    println!("[HTTPS] Generated a local certificate for port 8443.");
    Ok(())
}

pub fn load_server_config() -> Result<Arc<ServerConfig>, String> {
    let cert_pem = std::fs::read(paths::cert_pem()).map_err(|e| e.to_string())?;
    let key_pem = std::fs::read(paths::key_pem()).map_err(|e| e.to_string())?;
    let mut cert_reader = Cursor::new(cert_pem);
    let certs = rustls_pemfile::certs(&mut cert_reader).collect::<Result<Vec<CertificateDer>, _>>().map_err(|e| e.to_string())?;
    let mut key_reader = Cursor::new(key_pem);
    let key = rustls_pemfile::private_key(&mut key_reader).map_err(|e| e.to_string())?.ok_or("no private key")?;
    let key = PrivateKeyDer::from(key);
    let config = ServerConfig::builder()
        .with_no_client_auth()
        .with_single_cert(certs, key)
        .map_err(|e| e.to_string())?;
    Ok(Arc::new(config))
}

pub fn days_left_for_host(host: &str) -> i64 {
    let Ok(pem) = std::fs::read_to_string(paths::cert_pem()) else {
        return -1;
    };
    if !pem_covers_host(&pem, host) {
        return -1;
    }
    cert_days_left(&pem).unwrap_or(-1)
}

fn pem_covers_host(pem: &str, host: &str) -> bool {
    let host = host.trim().to_ascii_lowercase();
    if host.is_empty() {
        return false;
    }
    let Ok(der) = first_cert_der(pem) else {
        return false;
    };
    let Ok((_, cert)) = X509Certificate::from_der(&der) else {
        return false;
    };
    if cert.subject().to_string().to_ascii_lowercase().contains(&host) {
        return true;
    }
    cert.subject_alternative_name()
        .ok()
        .flatten()
        .map(|san| {
            san.value.general_names.iter().any(|name| match name {
                GeneralName::DNSName(dns) => dns.eq_ignore_ascii_case(&host),
                _ => false,
            })
        })
        .unwrap_or(false)
}

fn cert_days_left(pem: &str) -> Option<i64> {
    let der = first_cert_der(pem).ok()?;
    let (_, cert) = X509Certificate::from_der(&der).ok()?;
    let not_after = cert.validity().not_after.timestamp();
    let now = chrono::Utc::now().timestamp();
    Some((not_after - now) / 86_400)
}

fn first_cert_der(pem: &str) -> Result<Vec<u8>, String> {
    let mut reader = Cursor::new(pem.as_bytes());
    let cert = rustls_pemfile::certs(&mut reader).next().ok_or("no cert")?.map_err(|e| e.to_string())?;
    Ok(cert.as_ref().to_vec())
}

pub async fn maybe_issue(app: &App) -> bool {
    let host = app.inner.https_host.lock().map(|s| s.trim().to_string()).unwrap_or_default();
    let token = std::env::var("TESLAPC_CF_TOKEN").unwrap_or_default();
    if host.is_empty() || token.trim().is_empty() {
        return false;
    }
    let days = days_left_for_host(&host);
    if !needs_renewal(days) {
        println!("[ACME] Certificate for {host} is valid for {days} more day(s); no action.");
        return true;
    }
    println!(
        "[ACME] Certificate for {host} needs renewal ({days} day(s) left). Requesting via DNS-01."
    );
    let email = app.inner.acme_email.lock().map(|s| s.clone()).unwrap_or_default();
    let staging = staging_from_env(std::env::var("TESLAPC_ACME_STAGING").ok().as_deref());
    match issue_dns01(&host, token.trim(), &email, staging).await {
        Ok(()) => {
            println!("[ACME] Stored a new certificate for {host}.");
            true
        }
        Err(err) => {
            println!("[ACME] Issuance failed: {err}");
            false
        }
    }
}

pub async fn renewal_loop(app: App, slot: CertSlot) {
    loop {
        tokio::time::sleep(Duration::from_secs(RENEWAL_CHECK_HOURS * 3600)).await;
        if maybe_issue(&app).await {
            if let Ok(config) = load_server_config() {
                if let Ok(mut guard) = slot.write() {
                    *guard = config;
                    println!("[ACME] Reloaded the TLS certificate.");
                }
            }
        }
    }
}

async fn issue_dns01(host: &str, token: &str, email: &str, staging: bool) -> Result<(), String> {
    use instant_acme::{AuthorizationStatus, ChallengeType, Identifier, NewOrder, RetryPolicy};
    let directory = acme_directory_url(staging).to_string();
    let account = load_or_create_account(&directory, email).await?;
    let identifier = Identifier::Dns(host.to_string());
    let mut order = account.new_order(&NewOrder::new(&[identifier])).await.map_err(|e| e.to_string())?;
    let mut record_ids: Vec<(String, String)> = Vec::new();
    {
        let mut authz = order.authorizations();
        while let Some(result) = authz.next().await {
            let mut auth = result.map_err(|e| e.to_string())?;
            if auth.status != AuthorizationStatus::Pending {
                continue;
            }
            let mut challenge = auth.challenge(ChallengeType::Dns01).ok_or("no dns-01 challenge")?;
            let name = challenge.identifier().to_string();
            let value = challenge.key_authorization().dns_value();
            let (zone, id) = cloudflare_txt(token, &name, &value).await?;
            record_ids.push((zone, id));
            tokio::time::sleep(Duration::from_secs(8)).await;
            challenge.set_ready().await.map_err(|e| e.to_string())?;
        }
    }
    let policy = RetryPolicy::new().timeout(Duration::from_secs(180));
    order.poll_ready(&policy).await.map_err(|e| e.to_string())?;
    let key_pem = order.finalize().await.map_err(|e| e.to_string())?;
    let cert_pem = order.poll_certificate(&policy).await.map_err(|e| e.to_string())?;
    for (zone, id) in &record_ids {
        let _ = cloudflare_delete(token, zone, id).await;
    }
    if let Some(dir) = paths::cert_pem().parent() {
        std::fs::create_dir_all(dir).map_err(|e| e.to_string())?;
    }
    std::fs::write(paths::cert_pem(), cert_pem).map_err(|e| e.to_string())?;
    std::fs::write(paths::key_pem(), key_pem).map_err(|e| e.to_string())?;
    Ok(())
}

async fn load_or_create_account(directory: &str, email: &str) -> Result<instant_acme::Account, String> {
    use instant_acme::{Account, AccountCredentials, NewAccount};
    if let Ok(text) = std::fs::read_to_string(paths::acme_account()) {
        if let Ok(creds) = serde_json::from_str::<AccountCredentials>(&text) {
            if let Ok(account) = Account::builder().map_err(|e| e.to_string())?.from_credentials(creds).await {
                return Ok(account);
            }
        }
    }
    let mailto_owned = if email.trim().is_empty() { None } else { Some(format!("mailto:{}", email.trim())) };
    let contact_refs: Vec<&str> = mailto_owned.iter().map(String::as_str).collect();
    let (account, creds) = Account::builder()
        .map_err(|e| e.to_string())?
        .create(
            &NewAccount {
                contact: &contact_refs,
                terms_of_service_agreed: true,
                only_return_existing: false,
            },
            directory.to_string(),
            None,
        )
        .await
        .map_err(|e| e.to_string())?;
    if let Ok(text) = serde_json::to_string(&creds) {
        if let Some(dir) = paths::acme_account().parent() {
            let _ = std::fs::create_dir_all(dir);
        }
        let _ = std::fs::write(paths::acme_account(), text);
    }
    Ok(account)
}

async fn cloudflare_txt(token: &str, dns_name: &str, value: &str) -> Result<(String, String), String> {
    let zone = cloudflare_zone(token, dns_name).await?;
    let client = reqwest::Client::new();
    let name = if dns_name.starts_with("_acme-challenge") {
        dns_name.to_string()
    } else {
        format!("_acme-challenge.{dns_name}")
    };
    let response = client
        .post(format!("https://api.cloudflare.com/client/v4/zones/{zone}/dns_records"))
        .bearer_auth(token)
        .json(&serde_json::json!({
            "type": "TXT",
            "name": name,
            "content": value,
            "ttl": 60
        }))
        .send()
        .await
        .map_err(|e| e.to_string())?;
    let body: serde_json::Value = response.json().await.map_err(|e| e.to_string())?;
    let id = body["result"]["id"].as_str().ok_or_else(|| format!("cloudflare txt failed: {body}"))?;
    Ok((zone, id.to_string()))
}

async fn cloudflare_zone(token: &str, name: &str) -> Result<String, String> {
    let host = name.trim_start_matches("_acme-challenge.").trim_start_matches("*.");
    let labels: Vec<&str> = host.split('.').filter(|s| !s.is_empty()).collect();
    let client = reqwest::Client::new();
    for i in 0..labels.len().saturating_sub(1) {
        let zone_name = labels[i..].join(".");
        let response = client
            .get(format!("https://api.cloudflare.com/client/v4/zones?name={zone_name}"))
            .bearer_auth(token)
            .send()
            .await
            .map_err(|e| e.to_string())?;
        let body: serde_json::Value = response.json().await.map_err(|e| e.to_string())?;
        if let Some(id) = body["result"][0]["id"].as_str() {
            return Ok(id.to_string());
        }
    }
    Err(format!("no Cloudflare zone for {host}"))
}

async fn cloudflare_delete(token: &str, zone: &str, id: &str) -> Result<(), String> {
    let client = reqwest::Client::new();
    client
        .delete(format!("https://api.cloudflare.com/client/v4/zones/{zone}/dns_records/{id}"))
        .bearer_auth(token)
        .send()
        .await
        .map_err(|e| e.to_string())?;
    Ok(())
}
