# Firewall

`win::open_firewall` runs `netsh advfirewall firewall add rule name=TeslaPC Server` for inbound TCP `8080` and `8443`, all profiles. It runs only when the process is not `--localhost`. A non-admin failure is printed. HTTP still starts.

Linux does not add a firewall rule. The operator's firewall is unchanged.
