#!/usr/bin/env python3
"""A stand-in MPRIS player (org.mpris.MediaPlayer2.umbratest) for headless tests. Method calls (Play, Next...) are appended to
the file given as the first argument. Art points at the PNG given as the second argument (file://). Throwaway sessions only."""
import sys
from gi.repository import Gio, GLib

XML = """<node>
<interface name="org.mpris.MediaPlayer2"><property name="Identity" type="s" access="read"/></interface>
<interface name="org.mpris.MediaPlayer2.Player">
<method name="Play"/><method name="Pause"/><method name="PlayPause"/><method name="Next"/><method name="Previous"/>
<property name="PlaybackStatus" type="s" access="read"/><property name="Metadata" type="a{sv}" access="read"/>
<property name="CanPlay" type="b" access="read"/><property name="CanPause" type="b" access="read"/>
<property name="CanGoNext" type="b" access="read"/><property name="CanGoPrevious" type="b" access="read"/>
</interface></node>"""
out, art = sys.argv[1], sys.argv[2]
state = {"status": "Playing"}

def call(conn, sender, path, iface, method, params, inv):
    with open(out, "a") as f: f.write(method + "\n")
    if method == "Pause": state["status"] = "Paused"
    if method in ("Play",): state["status"] = "Playing"
    if method == "PlayPause": state["status"] = "Paused" if state["status"] == "Playing" else "Playing"
    inv.return_value(None)

def get(conn, sender, path, iface, prop):
    meta = {"xesam:title": GLib.Variant("s", "Test Song"), "xesam:artist": GLib.Variant("as", ["Artist A", "Artist B"]),
            "xesam:album": GLib.Variant("s", "The Album"), "mpris:length": GLib.Variant("x", 180000000),
            "mpris:artUrl": GLib.Variant("s", "file://" + art)}
    return {"Identity": GLib.Variant("s", "Umbra Test Player"), "PlaybackStatus": GLib.Variant("s", state["status"]),
            "Metadata": GLib.Variant("a{sv}", meta), "CanPlay": GLib.Variant("b", True), "CanPause": GLib.Variant("b", True),
            "CanGoNext": GLib.Variant("b", True), "CanGoPrevious": GLib.Variant("b", False)}[prop]

def acquired(conn, name):
    for i in Gio.DBusNodeInfo.new_for_xml(XML).interfaces:
        conn.register_object("/org/mpris/MediaPlayer2", i, call, get, None)

Gio.bus_own_name(Gio.BusType.SESSION, "org.mpris.MediaPlayer2.umbratest", 0, acquired, None, None)
GLib.MainLoop().run()
