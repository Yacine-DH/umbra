using System.Text.Json;
using System.Text.Json.Nodes;

namespace Umbra;

// Edit mode and focus gating for a widget window (#183): drag, resize, zoom, nudge, Esc, keyboard focus.
// Every one of them needs physical input the page cannot fake: a mouse press GTK delivered to this view, a button or
// modifier key the display server reports as down, or a key GTK delivered while this window had the focus. A message
// from the page alone never moves, resizes, zooms or focuses anything.
sealed partial class WidgetWindow
{
    long _pressedAt = long.MinValue / 2;      // last physical mouse press on this view
    long _leftPressAt = long.MinValue / 2;    // last physical left press outside edit mode (the prompts' click gate)
    long _ctrlScrollAt = long.MinValue / 2;   // last physical wheel turn on this view with Ctrl held
    long _escapeAt = long.MinValue / 2;       // last physical Esc press while this window had the focus
    readonly Dictionary<string, long> _arrowAt = new(); // last physical press of each arrow key
    bool _shiftAtKey;
    long _lastZoomAt = long.MinValue / 2;
    bool _moving, _resizing;

    /// <summary>Moving or changing the widget by hand needs the global lock off (edit mode).</summary>
    bool CanArrange => WidgetGeometry.CanArrange(_app.Settings.General);

    /// <summary>GTK controllers in the capture phase: they see real input before the page does and never consume it.</summary>
    void InitInput()
    {
        var press = Gtk.GestureClick.New();
        press.SetButton(0);
        press.SetPropagationPhase(Gtk.PropagationPhase.Capture);
        press.OnPressed += (g, _) =>
        {
            _pressedAt = Environment.TickCount64;
            // The click gate of the host prompts (notifications, later camera and pictures): a left press outside edit mode.
            if (g.GetCurrentButton() == 1 && !_app.EditMode) _leftPressAt = _pressedAt;
        };
        _view.AddController(press);

        var scroll = Gtk.EventControllerScroll.New(Gtk.EventControllerScrollFlags.Vertical);
        scroll.SetPropagationPhase(Gtk.PropagationPhase.Capture);
        scroll.OnScroll += (c, _) =>
        {
            if ((c.GetCurrentEventState() & Gdk.ModifierType.ControlMask) != 0) _ctrlScrollAt = Environment.TickCount64;
            return false;
        };
        _view.AddController(scroll);

        var keys = Gtk.EventControllerKey.New();
        keys.SetPropagationPhase(Gtk.PropagationPhase.Capture);
        keys.OnKeyPressed += (_, e) =>
        {
            var now = Environment.TickCount64;
            _shiftAtKey = (e.State & Gdk.ModifierType.ShiftMask) != 0;
            switch (e.Keyval)
            {
                case 0xff1b: // Escape: ends edit mode, like the pill's Done (the widget only has the focus after a real press)
                    _escapeAt = now;
                    if (_app.EditMode) { _app.SetEditMode(false); return true; }
                    break;
                case 0xff51: _arrowAt["left"] = now; break;
                case 0xff52: _arrowAt["up"] = now; break;
                case 0xff53: _arrowAt["right"] = now; break;
                case 0xff54: _arrowAt["down"] = now; break;
            }
            return false;
        };
        _view.AddController(keys);

        // The focus is lent, not given: once the user moves on to another window, this one cannot take it back by itself.
        _window.OnNotify += (_, e) =>
        {
            if (e.Pspec.GetName() == "is-active" && !_window.IsActive && !_closed) _app.Pin.SetFocusable(_window, false);
        };
    }

    /// <summary>Any physical mouse press on this widget in the last 2 s (the media controls' gate, as on Windows).</summary>
    bool RecentUserPress() => Environment.TickCount64 - _pressedAt is >= 0 and <= 2000;

    /// <summary>A physical left click on this widget in the last 2 s, outside edit mode: the only thing that may open a host prompt.</summary>
    bool RecentLeftClick() => !_app.EditMode && Environment.TickCount64 - _leftPressAt is >= 0 and <= 2000;

    /// <summary>A mouse button is physically down right now and GTK delivered its press to this view in the last few seconds.</summary>
    bool PressedOnThisWindow() =>
        Environment.TickCount64 - _pressedAt <= 5000 && _app.Pin.Pointer() is { ButtonDown: true };

    /// <summary>Handles the edit-mode and focus messages. Returns false for a type it does not know.</summary>
    bool HandleEditMessage(string type, JsonNode msg)
    {
        if (type is "drag" or "resize" or "zoom" or "nudge" or "exitEdit" or "pointer" or "focus")
            Log.Verbose($"[{Instance}] page asked: {type} (press {Environment.TickCount64 - _pressedAt} ms ago, pointer {_app.Pin.Pointer()})");
        switch (type)
        {
            case "drag":
                if (CanArrange && !_resizing && !_moving && PressedOnThisWindow()) BeginMove();
                else if (!CanArrange) Log.Verbose($"[{Instance}] drag ignored: widgets are locked");
                else Log.Verbose($"[{Instance}] drag ignored: no mouse press on the widget");
                return true;

            case "resize":
                // The edit-mode edge and corner handles. Only a real press on this window starts a resize.
                if (_resizing || _moving || !CanArrange) return true;
                if (!PressedOnThisWindow()) { Log.Write($"[{Instance}] resize ignored: no mouse press on the widget"); return true; }
                SelectForEdit();
                BeginResize(msg["edge"]?.GetValue<string>());
                return true;

            case "zoom":
            {
                // Ctrl+wheel in edit mode: one 10 % step per message, at most one every 50 ms. The wheel turn with Ctrl must
                // have reached this view as real input moments ago; the page cannot produce that.
                if (_resizing || !CanArrange) return true;
                var now = Environment.TickCount64;
                if (now - _ctrlScrollAt > 400) { Log.Write($"[{Instance}] zoom ignored: no Ctrl+wheel on the widget"); return true; }
                if (msg["steps"] is not JsonNode stepsNode || stepsNode.GetValueKind() != JsonValueKind.Number) return true;
                var steps = Math.Sign(stepsNode.GetValue<double>());
                if (steps == 0 || now - _lastZoomAt < 50) return true;
                _lastZoomAt = now;
                SetScaleByHand(Ws.Scale + 0.1 * steps);
                return true;
            }

            case "nudge":
                Nudge(msg["dir"]?.GetValue<string>());
                return true;

            case "exitEdit":
                // The page saw Esc. Only honoured when this window has the focus and Esc really was pressed on it just now.
                if (_app.EditMode && _window.IsActive && Environment.TickCount64 - _escapeAt <= 500) _app.SetEditMode(false);
                return true;

            case "pointer":
                if (PressedOnThisWindow()) SelectForEdit();
                return true;

            case "focus":
                if (PressedOnThisWindow()) SelectForEdit();
                TakeKeyboardFocus();
                return true;
        }
        return false;
    }

    /// <summary>
    /// Widgets never take the keyboard on their own, so text fields cannot receive typing. Right after the user pressed the
    /// mouse on this widget the page may ask for it; any other request is ignored, so a widget cannot pull the focus away
    /// while the user types elsewhere.
    /// </summary>
    void TakeKeyboardFocus()
    {
        if (Environment.TickCount64 - _pressedAt > 1000)
        {
            Log.Verbose($"[{Instance}] focus ignored: no mouse press on the widget");
            return;
        }
        _app.Pin.SetFocusable(_window, true);
        _app.Pin.Focus(_window);
        _view.GrabFocus();
    }

    void SelectForEdit()
    {
        if (CanArrange) _app.SelectForEdit(Instance);
    }

    /// <summary>Redraws the stronger outline when this widget becomes (or stops being) the selected one.</summary>
    public void ShowEditSelected(bool selected)
    {
        if (_ready) Post(new JsonObject { ["type"] = "editSelected", ["selected"] = selected });
    }

    // ---------- move ----------

    /// <summary>
    /// Follows the pointer until the button is released: the window keeps its offset to the pointer, snapping like Windows
    /// (grid, edges, even gaps; Shift per the snap mode). The pointer is read from the display server on a short timer,
    /// because the page keeps the event stream of the press.
    /// </summary>
    void BeginMove()
    {
        if (_app.Pin.Pointer() is not { } start) return;
        SelectForEdit();
        _moving = true;
        var origin = new Point(Left, Top);
        UiThread.Every(8, () =>
        {
            if (_closed) return false;
            var now = _app.Pin.Pointer();
            if (now is not { ButtonDown: true } p || !CanArrange) { EndMove(); return false; }
            var rect = new Rectangle(origin.X + p.At.X - start.At.X, origin.Y + p.At.Y - start.At.Y, Width, Height);
            var g = _app.Settings.General;
            if (Snap.SnapsWhileDragging(g, p.Shift))
            {
                var monitors = _app.Monitors();
                var others = _app.OpenWindows.Where(w => w != this && w.Visible).Select(w => w.Bounds).ToList();
                var snapped = Snap.Compute(rect, g.SnapGrid, g.SnapEdges, Math.Clamp(g.SnapGap, 0, Snap.MaxGap),
                    monitors.Select(m => m.WorkArea).ToList(), others, 10);
                rect = snapped.Rect;
                _app.ShowGuides(snapped.Guides);
            }
            else _app.ShowGuides([]);
            if (rect.X != Left || rect.Y != Top)
            {
                Left = rect.X; Top = rect.Y;
                _app.Pin.MoveLive(_window, Left, Top);
            }
            return true;
        });
    }

    void EndMove()
    {
        _moving = false;
        _app.ShowGuides([]);
        if (!Alive) return;
        WidgetPlacement.Remember(Ws, Bounds, _app.Monitors());
        _app.Pin.Move(_window, Left, Top); // a backend that kept the monitor during the drag moves it to the new one now
        _app.SaveSettings();
        _app.RefreshEditPills();
        Log.Verbose($"[{Instance}] moved to {Left},{Top}");
    }

    // ---------- resize ----------

    void BeginResize(string? edge)
    {
        if (_app.Pin.Pointer() is not { } start) return;
        _resizing = true;
        var from = new Size(Width, Height);
        Size min = WidgetGeometry.ScaledSize(Manifest.Width, Manifest.Height, LogicalDpi, WidgetSettings.MinScale),
             max = WidgetGeometry.ScaledSize(Manifest.Width, Manifest.Height, LogicalDpi, WidgetSettings.MaxScale);
        UiThread.Every(8, () =>
        {
            if (_closed) return false;
            if (_app.Pin.Pointer() is not { ButtonDown: true } p || !CanArrange) { EndResize(from); return false; }
            int dx = p.At.X - start.At.X, dy = p.At.Y - start.At.Y;
            var size = new Size(
                Math.Clamp(from.Width + (edge == "bottom" ? 0 : dx), min.Width, max.Width),
                Math.Clamp(from.Height + (edge == "right" ? 0 : dy), min.Height, max.Height));
            if (size.Width != Width || size.Height != Height)
            {
                // The page lays out at window / zoom and zooms with the smaller stretch.
                var zoom = SetWindowSize(size);
                if (_ready) Post(new JsonObject { ["type"] = "editScale", ["scale"] = zoom });
            }
            return true;
        });
    }

    void EndResize(Size before)
    {
        _resizing = false;
        if (!Alive) return;
        // Edit mode may have been turned off during the drag: put the saved size back.
        if (!CanArrange) { ApplySize(); return; }
        if (Width == before.Width && Height == before.Height) return;
        var ws = Ws;
        var k = Prefs.ClampSize(_app.Settings.General.Size); // the window shows own size x the global Size; store the own part
        ws.Width = (int)Math.Round(Width / k);
        ws.Height = (int)Math.Round(Height / k);
        ws.Scale = WidgetSettings.ClampScale(WidgetGeometry.ZoomFor(Manifest.Width, Manifest.Height, LogicalDpi, Width, Height) / k);
        FinishHandResize();
    }

    /// <summary>Zooms the widget by hand (Ctrl+wheel): the top-left corner stays where it is. A free size grows or shrinks with it.</summary>
    void SetScaleByHand(double scale)
    {
        var ws = Ws;
        if (ws.Width is int w && ws.Height is int h)
        {
            var factor = scale / Math.Max(0.01, ws.Scale);
            var size = WidgetGeometry.ClampSize(Manifest.Width, Manifest.Height, 96, (int)Math.Round(w * factor), (int)Math.Round(h * factor));
            ws.Width = size.Width; ws.Height = size.Height;
            ws.Scale = WidgetGeometry.ZoomFor(Manifest.Width, Manifest.Height, 96, size.Width, size.Height);
        }
        else ws.Scale = scale;
        FinishHandResize();
    }

    void FinishHandResize()
    {
        _app.NoteHandScale(Instance);
        Ws.UseDefaultScale = false; // a size picked by hand is the widget's own
        WidgetPlacement.Remember(Ws, Bounds, _app.Monitors());
        PushSettings();
        SaveSoon();
    }

    int _saveGeneration;

    /// <summary>A wheel notch or a held arrow key can fire many times a second: settings.json is written once it settles.</summary>
    void SaveSoon()
    {
        var mine = ++_saveGeneration;
        UiThread.Every(300, () =>
        {
            if (mine == _saveGeneration && Alive) { _app.SaveSettings(); _app.SettingsChangedElsewhere(); }
            return false;
        });
    }

    // ---------- nudge ----------

    /// <summary>
    /// Arrow-key nudge in edit mode: 1 px, Shift 10 px (one grid step when snapping is on). The page only reports which
    /// arrow it saw; the host moves the window only for the selected widget, while this window has the focus (which it
    /// only gets after a physical click) and that arrow key reached it as real input just now. The widget cannot leave its
    /// work area completely, and the new spot is saved like after a drag.
    /// </summary>
    void Nudge(string? dir)
    {
        var (sx, sy) = dir switch { "left" => (-1, 0), "right" => (1, 0), "up" => (0, -1), "down" => (0, 1), _ => (0, 0) };
        if ((sx == 0 && sy == 0) || _resizing || _moving || !CanArrange || !Alive || _app.EditSelection != Instance) return;
        if (!_window.IsActive || Environment.TickCount64 - _arrowAt.GetValueOrDefault(dir!, long.MinValue / 2) > 500) return;

        var big = _shiftAtKey;
        var grid = Snap.NudgeGrid(_app.Settings.General, big);
        var to = new Point(
            sx == 0 ? Left : WidgetGeometry.NudgeTo(Left, sx, big, grid),
            sy == 0 ? Top : WidgetGeometry.NudgeTo(Top, sy, big, grid));
        var areas = _app.Monitors().Select(m => m.WorkArea).ToList();
        if (!WidgetGeometry.NudgeAllowed(Bounds, new Rectangle(to, new Size(Width, Height)), areas, WidgetGeometry.NudgeKeepVisible)) return;

        Left = to.X; Top = to.Y;
        _app.Pin.Move(_window, Left, Top);
        WidgetPlacement.Remember(Ws, Bounds, _app.Monitors());
        SaveSoon();
    }
}
