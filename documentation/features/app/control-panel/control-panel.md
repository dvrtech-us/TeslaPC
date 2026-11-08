# Process Control

There is no WinForms dashboard. `teslapc` logs to stdout. `teslapc service install` registers it.

## Windows service

`windows_service_create_args` builds `sc.exe create TeslaPC binPath= "<exe> --service" start= auto`. `--service` asks the Service Control Manager to dispatch `service_main`. If the process was not started by the SCM, it logs that and runs in the console instead. The service sets status Running and then blocks in `serve`.

## Linux service

`linux_systemd_unit` is a `Type=simple` unit. `service install` writes `/etc/systemd/system/teslapc.service` and runs `systemctl daemon-reload`. A permission error is printed. The unit is not required for `teslapc` itself to serve HTTP.

## Hotspot control

On and off live on the Settings page (`POST /hotspot`), not in a desktop window.
