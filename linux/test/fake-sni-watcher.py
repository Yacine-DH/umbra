#!/usr/bin/env python3
"""A stand-in org.kde.StatusNotifierWatcher for headless tests: it takes registrations and writes "<sender> <service>"
lines to the file given as the first argument. Only for throwaway sessions (linux/test/tray.sh)."""
import sys
from gi.repository import Gio, GLib

XML = """<node><interface name="org.kde.StatusNotifierWatcher">
<method name="RegisterStatusNotifierItem"><arg type="s" direction="in"/></method>
<method name="RegisterStatusNotifierHost"><arg type="s" direction="in"/></method>
<property name="RegisteredStatusNotifierItems" type="as" access="read"/>
<property name="IsStatusNotifierHostRegistered" type="b" access="read"/>
<property name="ProtocolVersion" type="i" access="read"/>
</interface></node>"""
out = sys.argv[1]
items = []

def call(conn, sender, path, iface, method, params, inv):
    if method == "RegisterStatusNotifierItem":
        items.append(params.unpack()[0])
        with open(out, "a") as f:
            f.write(f"{sender} {params.unpack()[0]}\n")
    inv.return_value(None)

def get(conn, sender, path, iface, prop):
    return {"RegisteredStatusNotifierItems": GLib.Variant("as", items),
            "IsStatusNotifierHostRegistered": GLib.Variant("b", True),
            "ProtocolVersion": GLib.Variant("i", 0)}[prop]

def acquired(conn, name):
    info = Gio.DBusNodeInfo.new_for_xml(XML).interfaces[0]
    conn.register_object("/StatusNotifierWatcher", info, call, get, None)

Gio.bus_own_name(Gio.BusType.SESSION, "org.kde.StatusNotifierWatcher", 0, acquired, None, None)
GLib.MainLoop().run()
