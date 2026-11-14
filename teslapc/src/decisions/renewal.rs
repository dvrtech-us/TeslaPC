//! Certificate renewal decision. Issuance itself lives in the TLS module.

/// Renew when this many days or fewer remain (C# skips only when `daysLeft > 30`).
pub const RENEW_WHEN_DAYS_LEFT: i64 = 30;
/// The renewal loop waits this long between checks.
pub const RENEWAL_CHECK_HOURS: u64 = 12;

pub const LE_PRODUCTION: &str = "https://acme-v02.api.letsencrypt.org/directory";
pub const LE_STAGING: &str = "https://acme-staging-v02.api.letsencrypt.org/directory";

/// True when the certificate should be renewed, including a missing certificate (`days_left <= 0`).
pub fn needs_renewal(days_left: i64) -> bool {
    days_left <= RENEW_WHEN_DAYS_LEFT
}

pub fn acme_directory_url(staging: bool) -> &'static str {
    if staging { LE_STAGING } else { LE_PRODUCTION }
}

pub fn staging_from_env(value: Option<&str>) -> bool {
    value == Some("1")
}
