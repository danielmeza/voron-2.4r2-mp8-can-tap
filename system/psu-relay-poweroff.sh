#!/bin/sh
# ExecStop of psu-relay-poweroff.service.  Only a poweroff or halt releases the
# relay; a reboot must keep mains on.  The relay module cuts mains 7.5 s after
# PS_ON drops, so the drop is left this late, when Linux is already stopping.
# See docs/adr/0022-power-off-relay-on-ps-on.md.
if ! systemctl list-jobs --no-legend | grep -qE '(poweroff|halt)\.target +start'; then
    logger -t psu-relay-poweroff "not a poweroff; PS_ON left as is"
    exit 0
fi
if curl -sf -m 4 -X POST -H 'Content-Type: application/json' \
        -d '{"script": "_PSU_RELAY_RELEASE"}' \
        http://127.0.0.1:7125/printer/gcode/script >/dev/null; then
    logger -t psu-relay-poweroff "PS_ON released; the relay module cuts mains in 7.5 s"
else
    logger -t psu-relay-poweroff "could not release PS_ON (Klipper not ready?); mains stays on"
fi
exit 0
