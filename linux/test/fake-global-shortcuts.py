#!/usr/bin/env python3
"""A stand-in org.freedesktop.portal.GlobalShortcuts (plus the host Registry) for headless tests. Each call is written to the
file given as the first argument as one JSON line. BindShortcuts waits for its "dialog" to be answered: write "accept" or
"cancel" to "<file>.answer". Writing a shortcut id to "<file>.activate" emits Activated for the open session. Throwaway sessions only."""
import json, os, sys
from gi.repository import Gio, GLib

XML = """<node>
<interface name="org.freedesktop.portal.GlobalShortcuts">
<method name="CreateSession"><arg type="a{sv}" direction="in"/><arg type="o" direction="out"/></method>
<method name="BindShortcuts"><arg type="o" direction="in"/><arg type="a(sa{sv})" direction="in"/><arg type="s" direction="in"/>
<arg type="a{sv}" direction="in"/><arg type="o" direction="out"/></method>
<signal name="Activated"><arg type="o"/><arg type="s"/><arg type="t"/><arg type="a{sv}"/></signal>
<property name="version" type="u" access="read"/>
</interface>
<interface name="org.freedesktop.host.portal.Registry">
<method name="Register"><arg type="s" direction="in"/><arg type="a{sv}" direction="in"/></method>
</interface>
<interface name="org.freedesktop.portal.Session"><method name="Close"/></interface>
</node>"""
out = sys.argv[1]
node = Gio.DBusNodeInfo.new_for_xml(XML)
state = {"conn": None, "session": None, "pending": None, "n": 0}

def log(entry):
    with open(out, "a") as f: f.write(json.dumps(entry) + "\n")

def request_path(sender, token):
    return f"/org/freedesktop/portal/desktop/request/{sender[1:].replace('.', '_')}/{token}"

def respond(path, code, results):
    state["conn"].emit_signal(None, path, "org.freedesktop.portal.Request", "Response", GLib.Variant("(ua{sv})", (code, results)))

def session_call(conn, sender, path, iface, method, params, inv):
    log({"method": "Close", "session": path})
    if state["session"] == path: state["session"] = None
    inv.return_value(None)

def call(conn, sender, path, iface, method, params, inv):
    if method == "Register":
        log({"method": "Register", "app": params.unpack()[0]})
        inv.return_value(None); return
    if method == "CreateSession":
        opts = params.unpack()[0]
        req = request_path(sender, opts["handle_token"])
        state["n"] += 1
        spath = f"/org/freedesktop/portal/desktop/session/{sender[1:].replace('.', '_')}/{opts['session_handle_token']}"
        conn.register_object(spath, node.interfaces[2], session_call, None, None)
        state["session"] = spath
        log({"method": "CreateSession", "session": spath})
        inv.return_value(GLib.Variant("(o)", (req,)))
        GLib.idle_add(lambda: respond(req, 0, {"session_handle": GLib.Variant("s", spath)}) and False)
        return
    if method == "BindShortcuts":
        session, shortcuts, parent, opts = params.unpack()
        req = request_path(sender, opts["handle_token"])
        log({"method": "BindShortcuts", "session": session, "shortcuts": [[i, {k: str(v) for k, v in p.items()}] for i, p in shortcuts]})
        state["pending"] = (req, shortcuts)
        inv.return_value(GLib.Variant("(o)", (req,)))
        return
    inv.return_value(None)

def get_property(conn, sender, path, iface, prop):
    return GLib.Variant("u", 1) if prop == "version" else None

def poll():
    answer, activate = out + ".answer", out + ".activate"
    if state["pending"] and os.path.exists(answer):
        what = open(answer).read().strip(); os.remove(answer)
        req, shortcuts = state["pending"]; state["pending"] = None
        if what == "accept":
            bound = [(i, {"description": GLib.Variant("s", p.get("description", "")),
                          "trigger_description": GLib.Variant("s", "Press " + p.get("preferred_trigger", ""))}) for i, p in shortcuts]
            respond(req, 0, {"shortcuts": GLib.Variant("a(sa{sv})", bound)})
        else:
            respond(req, 1, {})
    if os.path.exists(activate) and state["session"]:
        sid = open(activate).read().strip(); os.remove(activate)
        state["conn"].emit_signal(None, "/org/freedesktop/portal/desktop", "org.freedesktop.portal.GlobalShortcuts", "Activated",
                                  GLib.Variant("(osta{sv})", (state["session"], sid, 0, {})))
    return True

def acquired(conn, name):
    state["conn"] = conn
    conn.register_object("/org/freedesktop/portal/desktop", node.interfaces[0], call, get_property, None)
    conn.register_object("/org/freedesktop/portal/desktop", node.interfaces[1], call, None, None)
    log({"ready": True})

Gio.bus_own_name(Gio.BusType.SESSION, "org.freedesktop.portal.Desktop", 0, acquired, None, None)
GLib.timeout_add(100, poll)
GLib.MainLoop().run()
