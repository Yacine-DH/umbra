// SPDX-License-Identifier: GPL-2.0-or-later OR MIT
//
// Umbra desktop widgets: the window-manager half of Umbra's desktop pinning on GNOME Wayland (#187).
// Copyright (C) 2026 the Umbra contributors
//
// This program is free software: you can redistribute it and/or modify it under the terms of the GNU General Public
// License as published by the Free Software Foundation, either version 2 of the License, or (at your option) any later
// version, or, at your option, under the terms of the MIT License (the license of the rest of Umbra).
// This program is distributed in the hope that it will be useful, but WITHOUT ANY WARRANTY; without even the implied
// warranty of MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the GNU General Public License for more details.
//
// Mutter has no layer-shell and a Wayland client cannot place itself, so Umbra's host asks this extension over the
// session bus (io.github.aminechr54.Umbra.Shell) to pin its widget windows. A window is treated as Umbra's only when all
// of these hold: it is a native Wayland client (so its pid comes from the socket, not from the client), its app id is
// io.github.aminechr54.Umbra, its title is "umbra:<token>" with a random 128-bit token, that token was registered with
// AddWindow, and the process that registered it (by D-Bus unique name) has the window's pid. Another app cannot get its
// windows treated as widgets, and a caller can only move or focus windows of its own process.
//
// Focus gating (the CLAUDE.md invariant): an Umbra window never keeps the keyboard focus unless Umbra asked, right after
// a physical press the compositor itself saw on that window (SetFocusable re-checks the pointer); any other focus
// (a click outside edit mode, the focus falling back when a window closes) is handed straight back.

import Gio from 'gi://Gio';
import GLib from 'gi://GLib';
import Meta from 'gi://Meta';

import {Extension} from 'resource:///org/gnome/shell/extensions/extension.js';
import * as Main from 'resource:///org/gnome/shell/ui/main.js';
import * as Workspace from 'resource:///org/gnome/shell/ui/workspace.js';

const APP_ID = 'io.github.aminechr54.Umbra';
const BUS_NAME = 'io.github.aminechr54.Umbra.Shell';
const OBJECT_PATH = '/io/github/aminechr54/Umbra/Shell';
const PROTOCOL_VERSION = 1;
const TOKEN = /^[0-9a-f]{32}$/;
const TITLE = /^umbra:([0-9a-f]{32})$/;
const MAX_WINDOWS_PER_CLIENT = 512;
const PRESS_WINDOW_US = 2000 * 1000; // a press the compositor saw counts this long for SetFocusable
const LEASE_MS = 1500; // a granted focus that was never taken is withdrawn after this
const BUTTONS = (1 << 8) | (1 << 9) | (1 << 10); // Clutter.ModifierType BUTTON1..3
const COORD_LIMIT = 1 << 20;

const IFACE = `
<node>
  <interface name="${BUS_NAME}">
    <method name="GetVersion"><arg type="u" direction="out" name="version"/></method>
    <method name="AddWindow"><arg type="s" direction="in" name="token"/><arg type="s" direction="in" name="kind"/></method>
    <method name="RemoveWindow"><arg type="s" direction="in" name="token"/></method>
    <method name="MoveWindow">
      <arg type="s" direction="in" name="token"/><arg type="i" direction="in" name="x"/><arg type="i" direction="in" name="y"/>
      <arg type="i" direction="in" name="width"/><arg type="i" direction="in" name="height"/>
    </method>
    <method name="SetFocusable">
      <arg type="s" direction="in" name="token"/><arg type="b" direction="in" name="focusable"/><arg type="b" direction="out" name="granted"/>
    </method>
    <method name="GetMonitors"><arg type="i" direction="out" name="primary"/><arg type="a(iiiiiiii)" direction="out" name="monitors"/></method>
    <method name="GetPointer">
      <arg type="b" direction="out" name="known"/><arg type="i" direction="out" name="x"/><arg type="i" direction="out" name="y"/>
      <arg type="u" direction="out" name="mask"/>
    </method>
    <signal name="MonitorsChanged"/>
  </interface>
</node>`;

/** Mutter 49+ can make a Wayland window a desktop window itself; 46-48 only through a Meta.WaylandClient Umbra is not. */
const NATIVE = typeof Meta.Window.prototype.set_type === 'function' &&
    typeof Meta.Window.prototype.hide_from_window_list === 'function';

export default class UmbraExtension extends Extension {
    enable() {
        this._entries = new Map(); // token -> {token, kind, sender, window, move, focusable, pressedAt, lease}
        this._clients = new Map(); // unique name -> {pid, watch, tokens:Set, grab}
        this._byWindow = new Map(); // Meta.Window -> entry
        this._watched = new Map(); // Meta.Window -> [signal ids] (every window with our app id)
        this._waiting = new Map(); // Meta.Window -> [signal ids] (Wayland windows whose app id has not arrived yet)
        this._lastOther = null;
        this._reverting = false;
        this._patches = [];

        this._dbus = Gio.DBusExportedObject.wrapJSObject(IFACE, this);
        this._dbus.export(Gio.DBus.session, OBJECT_PATH);
        this._nameId = Gio.bus_own_name_on_connection(Gio.DBus.session, BUS_NAME, Gio.BusNameOwnerFlags.NONE, null, null);

        this._displaySignals = [
            global.display.connect('window-created', (_d, w) => this._watch(w)),
            global.display.connect('notify::focus-window', () => this._onFocusWindow()),
            global.display.connect('workareas-changed', () => this._monitorsChanged()),
            global.display.connect('restacked', () => this._checkStacking()),
        ];
        this._layoutSignal = Main.layoutManager.connect('monitors-changed', () => this._monitorsChanged());
        for (const actor of global.get_window_actors()) this._watch(actor.meta_window);
        if (!NATIVE) this._patchShell();
    }

    disable() {
        for (const id of this._displaySignals) global.display.disconnect(id);
        Main.layoutManager.disconnect(this._layoutSignal);
        for (const [w, ids] of this._watched) for (const id of ids) w.disconnect(id);
        for (const w of [...this._waiting.keys()]) this._stopWaiting(w);
        for (const entry of this._entries.values()) this._detach(entry);
        for (const client of this._clients.values()) Gio.bus_unwatch_name(client.watch);
        for (const [obj, name, old] of this._patches) obj[name] = old;
        Gio.bus_unown_name(this._nameId);
        this._dbus.unexport();
        this._dbus = null;
        this._entries = this._clients = this._byWindow = this._watched = this._waiting = null;
        this._lastOther = null;
        this._patches = [];
    }

    // ---------- D-Bus methods (the Async forms receive the invocation, so the caller is known) ----------

    GetVersionAsync(_params, invocation) {
        invocation.return_value(new GLib.Variant('(u)', [PROTOCOL_VERSION]));
    }

    AddWindowAsync([token, kind], invocation) {
        const sender = invocation.get_sender();
        if (!TOKEN.test(token) || (kind !== 'widget' && kind !== 'overlay'))
            return invocation.return_dbus_error('org.freedesktop.DBus.Error.InvalidArgs', 'bad token or kind');
        const existing = this._entries.get(token);
        if (existing && existing.sender !== sender)
            return invocation.return_dbus_error('org.freedesktop.DBus.Error.AccessDenied', 'token in use');
        const client = this._client(sender);
        if (!existing && client.tokens.size >= MAX_WINDOWS_PER_CLIENT)
            return invocation.return_dbus_error('org.freedesktop.DBus.Error.LimitsExceeded', 'too many windows');
        if (!existing) {
            this._entries.set(token, {token, kind, sender, window: null, move: null, focusable: false, pressedAt: 0, lease: 0});
            client.tokens.add(token);
        }
        this._rescan();
        invocation.return_value(null);
    }

    RemoveWindowAsync([token], invocation) {
        const entry = this._own(token, invocation);
        if (entry) this._forget(entry);
        invocation.return_value(null);
    }

    MoveWindowAsync([token, x, y, width, height], invocation) {
        const entry = this._own(token, invocation);
        if (!entry) return invocation.return_dbus_error('org.freedesktop.DBus.Error.AccessDenied', 'unknown token');
        if ([x, y, width, height].some(v => Math.abs(v) > COORD_LIMIT))
            return invocation.return_dbus_error('org.freedesktop.DBus.Error.InvalidArgs', 'out of range');
        entry.move = {x, y, width, height};
        this._applyMove(entry);
        invocation.return_value(null);
    }

    SetFocusableAsync([token, focusable], invocation) {
        const entry = this._own(token, invocation);
        invocation.return_value(new GLib.Variant('(b)', [entry ? this._setFocusable(entry, focusable) : false]));
    }

    GetMonitorsAsync(_params, invocation) {
        const list = [];
        for (let i = 0; i < global.display.get_n_monitors(); i++) {
            const g = global.display.get_monitor_geometry(i);
            const a = Main.layoutManager.getWorkAreaForMonitor(i);
            list.push([g.x, g.y, g.width, g.height, a.x, a.y, a.width, a.height]);
        }
        invocation.return_value(new GLib.Variant('(ia(iiiiiiii))', [global.display.get_primary_monitor(), list]));
    }

    /**
     * The pointer and which buttons and modifiers are down, as the compositor sees them (a page cannot fake this). Only
     * known to a caller while the pointer is over one of its own windows, or while a press that began there is held
     * (a drag or resize), so the bus does not become a way to follow the pointer.
     */
    GetPointerAsync(_params, invocation) {
        const client = this._clients.get(invocation.get_sender());
        const [x, y, mask] = global.get_pointer();
        let known = false;
        if (client) {
            const down = (mask & BUTTONS) !== 0;
            const over = [...client.tokens].some(t => this._contains(this._entries.get(t)?.window, x, y));
            if (!down) client.grab = false;
            else if (over) client.grab = true;
            known = over || client.grab;
        }
        invocation.return_value(new GLib.Variant('(biiu)', known ? [true, x, y, mask] : [false, 0, 0, 0]));
    }

    // ---------- clients and windows ----------

    _client(sender) {
        let client = this._clients.get(sender);
        if (client) return client;
        client = {pid: null, tokens: new Set(), grab: false, watch: 0};
        this._clients.set(sender, client);
        client.watch = Gio.bus_watch_name_on_connection(Gio.DBus.session, sender, Gio.BusNameWatcherFlags.NONE, null,
            () => this._dropClient(sender));
        Gio.DBus.session.call('org.freedesktop.DBus', '/org/freedesktop/DBus', 'org.freedesktop.DBus',
            'GetConnectionUnixProcessID', new GLib.Variant('(s)', [sender]), new GLib.VariantType('(u)'),
            Gio.DBusCallFlags.NONE, 5000, null, (bus, result) => {
                try {
                    const [pid] = bus.call_finish(result).deepUnpack();
                    if (this._clients?.get(sender) === client) { client.pid = pid; this._rescan(); }
                } catch (e) {
                    console.warn(`Umbra: no pid for ${sender}: ${e.message}`);
                }
            });
        return client;
    }

    _dropClient(sender) {
        const client = this._clients?.get(sender);
        if (!client) return;
        for (const token of client.tokens) { const e = this._entries.get(token); if (e) this._forget(e); }
        Gio.bus_unwatch_name(client.watch);
        this._clients.delete(sender);
    }

    /** The entry for this token if the caller registered it. */
    _own(token, invocation) {
        const entry = typeof token === 'string' ? this._entries.get(token) : null;
        return entry && entry.sender === invocation.get_sender() ? entry : null;
    }

    /** The host is closing the window (or went away): its window is being destroyed, so nothing is restored. */
    _forget(entry) {
        this._detach(entry, false);
        this._entries.delete(entry.token);
        this._clients.get(entry.sender)?.tokens.delete(entry.token);
    }

    /** Follows every window with Umbra's app id (its title may arrive or change after it was created). */
    _watch(window) {
        if (!window || this._watched.has(window)) return;
        if (window.get_wm_class() !== APP_ID && window.get_gtk_application_id() !== APP_ID) {
            // A Wayland client's app id can arrive after the window: look again once it does.
            if (window.get_client_type() === Meta.WindowClientType.WAYLAND && !this._waiting.has(window)) {
                this._waiting.set(window, [
                    window.connect('notify::wm-class', () => { this._stopWaiting(window); this._watch(window); }),
                    window.connect('unmanaged', () => this._stopWaiting(window)),
                ]);
            }
            return;
        }
        this._watched.set(window, [
            window.connect('notify::title', () => this._consider(window)),
            window.connect('unmanaged', () => this._unwatch(window)),
            window.connect('shown', () => { const e = this._byWindow.get(window); if (e) { this._applyMove(e); this._restack(e); } }),
            window.connect('raised', () => { const e = this._byWindow.get(window); if (e) this._restack(e); }),
        ]);
        this._consider(window);
    }

    _stopWaiting(window) {
        for (const id of this._waiting.get(window) ?? []) window.disconnect(id);
        this._waiting.delete(window);
    }

    _unwatch(window) {
        const entry = this._byWindow.get(window);
        if (entry) { this._byWindow.delete(window); entry.window = null; }
        for (const id of this._watched.get(window) ?? []) window.disconnect(id);
        this._watched.delete(window);
        if (this._lastOther === window) this._lastOther = null;
    }

    _rescan() {
        for (const window of this._watched.keys()) this._consider(window);
    }

    /** Attaches the window to its entry when every identity check passes, and detaches it when one stops passing. */
    _consider(window) {
        const match = TITLE.exec(window.get_title() ?? '');
        const entry = match ? this._entries.get(match[1]) : null;
        const client = entry ? this._clients.get(entry.sender) : null;
        const ok = entry && client && client.pid !== null && client.pid === window.get_pid() &&
            window.get_client_type() === Meta.WindowClientType.WAYLAND && (!entry.window || entry.window === window);
        const current = this._byWindow.get(window);
        if (current && current !== entry) this._detach(current);
        if (!ok || current === entry) return;
        entry.window = window;
        this._byWindow.set(window, entry);
        if (NATIVE) {
            window.set_type(Meta.WindowType.DESKTOP); // below every window, out of the overview, Alt+Tab and the dock
            window.hide_from_window_list();
        }
        window.stick();
        this._applyMove(entry);
        this._restack(entry);
        if (global.display.focus_window === window) this._onFocusWindow();
    }

    _detach(entry, restore = true) {
        const window = entry.window;
        if (entry.lease) { GLib.source_remove(entry.lease); entry.lease = 0; }
        entry.focusable = false;
        if (!window) return;
        this._byWindow.delete(window);
        entry.window = null;
        if (!restore) return;
        try {
            if (NATIVE) { window.set_type(Meta.WindowType.NORMAL); window.show_in_window_list(); }
            window.unstick();
        } catch (e) {
            console.warn(`Umbra: could not restore a window: ${e.message}`);
        }
    }

    _applyMove(entry) {
        const {window, move} = entry;
        if (!window || !move) return;
        if (move.width > 0 && move.height > 0) window.move_resize_frame(false, move.x, move.y, move.width, move.height);
        else window.move_frame(false, move.x, move.y);
    }

    /**
     * Widgets sit above the desktop icons (DING keeps its windows at the bottom of the desktop layer) and the host's own
     * overlays (edit pill, snap guides, tab strip) above the widgets. On Mutter 46-48 the windows stay normal windows kept
     * at the bottom of the normal layer instead.
     */
    _restack(entry) {
        if (this._restacking) return;
        this._restacking = true;
        try {
            const mine = [...this._byWindow.values()];
            if (NATIVE) {
                if (entry.kind === 'widget') entry.window.raise();
                for (const e of mine) if (e.kind === 'overlay') e.window.raise();
            } else {
                for (const e of mine) if (e.kind === 'overlay') e.window.lower();
                for (const e of mine) if (e.kind === 'widget') e.window.lower();
            }
        } finally {
            this._restacking = false;
        }
    }

    /**
     * After any restack: when another desktop window (DING raising its own) ended above a widget, or a widget above an
     * overlay, the order is put back. The raises restack again, which then finds the order right and stops.
     */
    _checkStacking() {
        if (!NATIVE || this._restacking || !this._byWindow?.size) return;
        const sorted = global.get_window_actors().map(a => a.meta_window); // the painted order, bottom to top
        let desktopTop = -1, firstOverlay = Infinity, lastWidget = -1, firstWidget = Infinity;
        sorted.forEach((w, i) => {
            const e = this._byWindow.get(w);
            if (!e) { if (w.get_window_type() === Meta.WindowType.DESKTOP) desktopTop = i; return; }
            if (e.kind === 'overlay') firstOverlay = Math.min(firstOverlay, i);
            else { lastWidget = i; firstWidget = Math.min(firstWidget, i); }
        });
        if (firstWidget > desktopTop && firstOverlay > lastWidget && firstOverlay > desktopTop) return;
        this._restacking = true;
        try {
            for (const w of sorted) if (this._byWindow.get(w)?.kind === 'widget') w.raise();
            for (const w of sorted) if (this._byWindow.get(w)?.kind === 'overlay') w.raise();
        } finally {
            this._restacking = false;
        }
    }

    _contains(window, x, y) {
        if (!window || window.minimized || !window.showing_on_its_workspace()) return false;
        const r = window.get_frame_rect();
        return x >= r.x && y >= r.y && x < r.x + r.width && y < r.y + r.height;
    }

    // ---------- focus gating ----------

    /** Grants the focus only right after a press the compositor saw on that window, with the pointer still over it. */
    _setFocusable(entry, on) {
        const window = entry.window;
        if (entry.lease) { GLib.source_remove(entry.lease); entry.lease = 0; }
        if (!on || !window) {
            entry.focusable = false;
            if (window && global.display.focus_window === window) this._giveBack();
            return !on;
        }
        const [x, y, mask] = global.get_pointer();
        const pressed = (mask & BUTTONS) !== 0 || GLib.get_monotonic_time() - entry.pressedAt <= PRESS_WINDOW_US;
        if (!pressed || !this._contains(window, x, y)) return false;
        entry.focusable = true;
        entry.hadFocus = false;
        window.focus(global.display.get_current_time_roundtrip());
        entry.lease = GLib.timeout_add(GLib.PRIORITY_DEFAULT, LEASE_MS, () => {
            entry.lease = 0;
            if (global.display.focus_window !== window) entry.focusable = false;
            return GLib.SOURCE_REMOVE;
        });
        return true;
    }

    _onFocusWindow() {
        if (!this._byWindow) return;
        const focused = global.display.focus_window;
        // The focus is lent, not given: once it moves on, an Umbra window cannot take it back by itself.
        for (const e of this._byWindow.values())
            if (e.focusable && e.hadFocus && e.window !== focused) { e.focusable = false; e.hadFocus = false; }
        const entry = focused ? this._byWindow.get(focused) : null;
        if (!entry) { if (focused) this._lastOther = focused; return; }
        if (entry.focusable) { entry.hadFocus = true; return; }
        entry.pressedAt = GLib.get_monotonic_time(); // a click (or a focus fallback): only SetFocusable may keep it
        if (this._reverting) return;
        this._reverting = true;
        try { this._giveBack(); } finally { this._reverting = false; }
    }

    /** Back to the window the user had, or to nothing. */
    _giveBack() {
        const time = global.display.get_current_time_roundtrip();
        const other = this._lastOther;
        const workspace = global.workspace_manager.get_active_workspace();
        if (other && !other.minimized && global.display.list_all_windows().includes(other) && other.located_on_workspace(workspace))
            other.focus(time);
        else global.display.unset_input_focus(time);
    }

    _monitorsChanged() {
        this._dbus?.emit_signal('MonitorsChanged', null);
    }

    // ---------- GNOME 46-48: keep normal windows out of Alt+Tab and the overview ----------

    _patchShell() {
        const ours = w => this._byWindow?.has(w);
        const display = Meta.Display.prototype;
        const getTabList = display.get_tab_list;
        this._patch(display, 'get_tab_list', function (...args) {
            return getTabList.apply(this, args).filter(w => !ours(w));
        });
        const proto = Workspace.Workspace?.prototype;
        if (proto && typeof proto._isOverviewWindow === 'function') {
            const isOverview = proto._isOverviewWindow;
            this._patch(proto, '_isOverviewWindow', function (window) {
                return !ours(window) && isOverview.call(this, window);
            });
        }
    }

    _patch(obj, name, replacement) {
        this._patches.push([obj, name, obj[name]]);
        obj[name] = replacement;
    }
}
