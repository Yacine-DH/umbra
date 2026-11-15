#!/usr/bin/env python3
"""TEST ONLY (linux/test/monitors.sh): reads or changes the monitor layout of the throwaway headless GNOME Shell through
org.gnome.Mutter.DisplayConfig, the way Settings > Displays does. Never run it against a real session.

    monitor-config.py list
    monitor-config.py apply CONNECTOR:SCALE[,CONNECTOR:SCALE]...   left to right, the first is primary; others are switched off
"""
import sys

from gi.repository import Gio, GLib

bus = Gio.bus_get_sync(Gio.BusType.SESSION, None)


def call(method, args, reply):
    return bus.call_sync("org.gnome.Mutter.DisplayConfig", "/org/gnome/Mutter/DisplayConfig", "org.gnome.Mutter.DisplayConfig",
                         method, args, GLib.VariantType(reply) if reply else None, Gio.DBusCallFlags.NONE, 5000, None)


serial, monitors, logical, props = call("GetCurrentState", None, "(ua((ssss)a(siiddada{sv})a{sv})a(iiduba(ssss)a{sv})a{sv})").unpack()


def current_mode(modes):
    return next((m for m in modes if m[6].get("is-current")), modes[0])


if sys.argv[1] == "list":
    for spec, modes, _ in monitors:
        m = current_mode(modes)
        on = any(spec in lm[5] for lm in logical)
        print(f"{spec[0]} {m[1]}x{m[2]} {'on' if on else 'off'} scales={sorted(set(m[5]))}")
    for lm in logical:
        print(f"logical {lm[0]},{lm[1]} scale={lm[2]} primary={lm[4]} {[s[0] for s in lm[5]]}")
    sys.exit(0)

wanted = [(c, float(s)) for c, s in (p.split(":") for p in sys.argv[2].split(","))]
by_name = {spec[0]: (spec, modes) for spec, modes, _ in monitors}
x = 0
out = []
for i, (connector, scale) in enumerate(wanted):
    spec, modes = by_name[connector]
    mode = current_mode(modes)
    out.append((x, 0, scale, 0, i == 0, [(connector, mode[0], {})]))
    x += int(round(mode[1] / scale))
call("ApplyMonitorsConfig", GLib.Variant("(uua(iiduba(ssa{sv}))a{sv})", (serial, 1, out, {})), None)
print("applied", sys.argv[2])
