#!/usr/bin/env python3
"""A stand-in org.freedesktop.Notifications for headless tests. Each Notify call is written to the file given as the first
argument as one JSON line; touching "<file>.click" with "<id> <action>" emits ActionInvoked. Throwaway sessions only."""
import json, os, sys
from gi.repository import Gio, GLib

XML = """<node><interface name="org.freedesktop.Notifications">
<method name="Notify"><arg type="s" direction="in"/><arg type="u" direction="in"/><arg type="s" direction="in"/>
<arg type="s" direction="in"/><arg type="s" direction="in"/><arg type="as" direction="in"/><arg type="a{sv}" direction="in"/>
<arg type="i" direction="in"/><arg type="u" direction="out"/></method>
<method name="GetCapabilities"><arg type="as" direction="out"/></method>
<method name="CloseNotification"><arg type="u" direction="in"/></method>
<method name="GetServerInformation"><arg type="s" direction="out"/><arg type="s" direction="out"/><arg type="s" direction="out"/><arg type="s" direction="out"/></method>
<signal name="ActionInvoked"><arg type="u"/><arg type="s"/></signal>
<signal name="NotificationClosed"><arg type="u"/><arg type="u"/></signal>
</interface></node>"""
out = sys.argv[1]
state = {"next": 1, "conn": None}

def call(conn, sender, path, iface, method, params, inv):
    if method == "Notify":
        app, replaces, icon, summary, body, actions, hints, timeout = params.unpack()
        nid = replaces or state["next"]
        if not replaces: state["next"] += 1
        with open(out, "a") as f:
            f.write(json.dumps({"id": nid, "app": app, "replaces": replaces, "icon": icon, "summary": summary, "body": body,
                                "actions": actions, "hints": {k: str(v) for k, v in hints.items()}}) + "\n")
        inv.return_value(GLib.Variant("(u)", (nid,)))
    elif method == "GetCapabilities":
        inv.return_value(GLib.Variant("(as)", (["body", "body-markup", "actions"],)))
    elif method == "GetServerInformation":
        inv.return_value(GLib.Variant("(ssss)", ("fake", "umbra-tests", "1", "1.2")))
    else:
        inv.return_value(None)

def poll():
    click = out + ".click"
    if os.path.exists(click):
        nid, action = open(click).read().split()
        os.remove(click)
        state["conn"].emit_signal(None, "/org/freedesktop/Notifications", "org.freedesktop.Notifications", "ActionInvoked",
                                  GLib.Variant("(us)", (int(nid), action)))
    return True

def acquired(conn, name):
    state["conn"] = conn
    info = Gio.DBusNodeInfo.new_for_xml(XML).interfaces[0]
    conn.register_object("/org/freedesktop/Notifications", info, call, None, None)

Gio.bus_own_name(Gio.BusType.SESSION, "org.freedesktop.Notifications", 0, acquired, None, None)
GLib.timeout_add(200, poll)
GLib.MainLoop().run()
