namespace Umbra;

/// <summary>
/// The host-drawn prompt for a widget command. It is a normal top-level window that no widget can draw into or move;
/// the widget's own text (its description) is shown in a separate, labelled box. Allow stays disabled for a moment so
/// a keystroke meant for another window can't approve it, and Deny is the default.
/// </summary>
sealed class ApprovalDialog : Form
{
    readonly Button _allow = new() { Text = "Allow", Enabled = false, DialogResult = DialogResult.Yes, Width = 110, Height = 32 };
    readonly Button _deny = new() { Text = "Deny", DialogResult = DialogResult.No, Width = 110, Height = 32 };
    /// <summary>A prompt with no answer before <see cref="TimeoutMs"/> closes with this result (still a refusal everywhere).</summary>
    const DialogResult TimedOut = DialogResult.Abort;

    ApprovalDialog(ApprovalRequest r)
    {
        SuspendLayout();
        Text = "Umbra: allow this widget to run a program?";
        Icon = App.AppIcon;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        TopMost = true;
        ShowInTaskbar = true;
        AutoScaleDimensions = new SizeF(96f, 96f); // sizes below are in 96-dpi units; ArmButtons' ResumeLayout applies the scaling
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(560, 345);

        var heading = new Label
        {
            Text = $"The widget \"{Clean(r.WidgetName, 40)}\" wants to run this on your PC:",
            Left = 16, Top = 14, Width = 528, Height = 22, UseMnemonic = false, Font = new Font(Font, FontStyle.Bold),
        };
        var line = new TextBox
        {
            Text = r.CommandLine, ReadOnly = true, Multiline = true, ScrollBars = ScrollBars.Vertical,
            Left = 16, Top = 42, Width = 528, Height = 70, Font = new Font(FontFamily.GenericMonospace, 9.5f),
        };
        var where = new Label { Text = $"Program: {r.ExePath}", Left = 16, Top = 120, Width = 528, Height = 34, AutoEllipsis = true, UseMnemonic = false };
        var note = new Label
        {
            Text = (r.HasParams ? "Parts written ${like this} are filled in by the widget each time (as single arguments). " : "")
                   + "It runs as you, with no window, and is stopped after a time limit. You can revoke this any time in Settings > My widgets.",
            Left = 16, Top = 156, Width = 528, Height = 50,
        };
        var said = new GroupBox { Text = "What the widget says it is for (not verified)", Left = 16, Top = 210, Width = 528, Height = 60 };
        said.Controls.Add(new Label { Text = string.IsNullOrWhiteSpace(r.Description) ? "(no description)" : r.Description, Left = 10, Top = 20, Width = 508, Height = 34, AutoEllipsis = true, UseMnemonic = false });

        var warn = new Label
        {
            Text = "This program can run anything: whoever controls this widget controls what it does.",
            Left = 16, Top = 272, Width = 528, Height = 20, ForeColor = Color.Firebrick, Font = new Font(Font, FontStyle.Bold),
            Visible = r.IsInterpreter, UseMnemonic = false,
        };
        _allow.Left = 544 - 110 - 10 - 110; _allow.Top = 296;
        _deny.Left = 544 - 110; _deny.Top = 296;
        Controls.AddRange(new System.Windows.Forms.Control[] { heading, line, where, note, said, warn, _allow, _deny });
        ArmButtons();
    }

    /// <summary>The one-time camera prompt for <c>permissions.camera</c> (timelapses).</summary>
    internal ApprovalDialog(string widgetName)
    {
        SuspendLayout();
        Text = "Umbra: allow this widget to use your camera?";
        Icon = App.AppIcon;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        TopMost = true;
        ShowInTaskbar = true;
        AutoScaleDimensions = new SizeF(96f, 96f); // sizes below are in 96-dpi units; ArmButtons' ResumeLayout applies the scaling
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(560, 216);
        var heading = new Label
        {
            Text = $"The widget \"{Clean(widgetName, 40)}\" wants to record timelapses with your webcam.",
            Left = 16, Top = 14, Width = 528, Height = 40, UseMnemonic = false, Font = new Font(Font, FontStyle.Bold),
        };
        var note = new Label
        {
            Text = "Umbra takes the photos itself and saves the video on this PC; the widget never sees the camera or the photos, "
                   + "and nothing is sent anywhere. Recording only starts when you click the widget, a red dot shows on the widget "
                   + "and in the tray while the camera is on, and it stops after 12 hours. You can revoke this in Settings > My widgets.",
            Left = 16, Top = 58, Width = 528, Height = 100,
        };
        _allow.Left = 544 - 110 - 10 - 110; _allow.Top = 168;
        _deny.Left = 544 - 110; _deny.Top = 168;
        Controls.AddRange(new System.Windows.Forms.Control[] { heading, note, _allow, _deny });
        ArmButtons();
    }

    /// <summary>The notification prompt for <c>umbra.requestNotifications</c>: Allow, Not now (default), Don't ask again.</summary>
    internal ApprovalDialog(string widgetName, bool notifications)
    {
        _ = notifications; // only selects this overload
        SuspendLayout();
        Text = "Umbra: allow notifications from this widget?";
        Icon = App.AppIcon;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        TopMost = true;
        ShowInTaskbar = true;
        AutoScaleDimensions = new SizeF(96f, 96f); // sizes below are in 96-dpi units; ArmButtons' ResumeLayout applies the scaling
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(560, 176);
        var heading = new Label
        {
            Text = $"The widget \"{Clean(widgetName, 40)}\" wants to show you notifications.",
            Left = 16, Top = 14, Width = 528, Height = 40, UseMnemonic = false, Font = new Font(Font, FontStyle.Bold),
        };
        var note = new Label
        {
            Text = "They appear as Windows notifications with the widget's name, at most 5 every 10 minutes. "
                   + "You can change this any time in Settings > My widgets.",
            Left = 16, Top = 58, Width = 528, Height = 60,
        };
        _deny.Text = "Not now";
        var never = new Button { Text = "Don't ask again", DialogResult = DialogResult.Ignore, Left = 16, Top = 128, Width = 140, Height = 32 };
        _allow.Left = 544 - 110 - 10 - 110; _allow.Top = 128;
        _deny.Left = 544 - 110; _deny.Top = 128;
        Controls.AddRange(new System.Windows.Forms.Control[] { heading, note, never, _allow, _deny });
        ArmButtons();
    }

    /// <summary>Dark surfaces and a dark title bar to match the app; behaviour is untouched.</summary>
    void ApplyDark()
    {
        BackColor = Color.Black; ForeColor = Color.White;
        foreach (var c in Controls.OfType<System.Windows.Forms.Control>().SelectMany(c => c.Controls.OfType<System.Windows.Forms.Control>().Prepend(c)))
        {
            switch (c)
            {
                case Button b:
                    b.FlatStyle = FlatStyle.Flat; b.UseVisualStyleBackColor = false;
                    b.BackColor = Color.FromArgb(24, 24, 24); b.ForeColor = Color.White; b.FlatAppearance.BorderColor = Color.FromArgb(58, 58, 58);
                    b.Paint += (_, e) =>
                    {
                        if (!b.Focused) return;
                        using var pen = new Pen(Color.FromArgb(96, 165, 250), 2);
                        e.Graphics.DrawRectangle(pen, 1, 1, b.Width - 3, b.Height - 3);
                    };
                    b.GotFocus += (_, _) => b.Invalidate();
                    b.LostFocus += (_, _) => b.Invalidate();
                    break;
                case TextBox t: t.BackColor = Color.FromArgb(24, 24, 24); t.ForeColor = Color.White; break;
                case GroupBox g: g.ForeColor = Color.White; break;
                case Label l: l.ForeColor = l.ForeColor == Color.Firebrick ? Color.FromArgb(255, 120, 110) : Color.White; break;
            }
        }
        HandleCreated += (_, _) => Native.SetDwm(Handle, Native.DWMWA_USE_IMMERSIVE_DARK_MODE, 1);
    }

    void ArmButtons()
    {
        ResumeLayout(false); // without this pair around the constructor, fonts scale with the display but the layout doesn't
        PerformLayout();
        ApplyDark();
        AcceptButton = _deny;
        CancelButton = _deny;
        ActiveControl = _deny;

        // Allow works only when 900 ms have passed since the dialog appeared AND the cursor, having been outside the
        // button at some point after that, is on it now. A click already in flight (or a click-jack aimed at where the
        // button will appear) therefore can't approve.
        var poll = new System.Windows.Forms.Timer { Interval = 40 };
        var shownAt = 0L;
        var sawOutside = false;
        poll.Tick += (_, _) =>
        {
            var inside = _allow.RectangleToScreen(_allow.ClientRectangle).Contains(Cursor.Position);
            if (!inside) sawOutside = true;
            _allow.Enabled = sawOutside && inside && System.Environment.TickCount64 - shownAt >= 900;
            if (System.Environment.TickCount64 - shownAt >= TimeoutMs) DialogResult = TimedOut; // a late click can't approve
        };
        Shown += (_, _) => { shownAt = System.Environment.TickCount64; poll.Start(); };
        FormClosed += (_, _) => poll.Dispose();
    }

    /// <summary>How long the prompt waits for an answer before it counts as a refusal and closes.</summary>
    public const int TimeoutMs = 120_000;

    /// <summary>Widget-controlled text for the heading: control characters removed, at most <paramref name="max"/> characters.</summary>
    internal static string Clean(string s, int max)
    {
        var t = new string(s.Where(c => !char.IsControl(c)).ToArray()).Trim();
        return t.Length > max ? t[..max] + "..." : t;
    }

    /// <summary>Shows the camera prompt and returns true only when the user clicked Allow.</summary>
    public static bool AskCamera(string widgetName)
    {
        using var dlg = new ApprovalDialog(widgetName);
        return dlg.ShowDialog() == DialogResult.Yes;
    }

    /// <summary>Shows the notification prompt: the user's choice, or null when it closed without an answer (timeout).</summary>
    public static NotifyChoice? AskNotifications(string widgetName)
    {
        using var dlg = new ApprovalDialog(widgetName, notifications: true);
        return dlg.ShowDialog() switch
        {
            DialogResult.Yes => NotifyChoice.Allow,
            DialogResult.Ignore => NotifyChoice.Never,
            TimedOut => null,
            _ => NotifyChoice.NotNow, // the Not now button, Esc or the close box
        };
    }

    /// <summary>Shows the prompt and returns true only when the user clicked Allow.</summary>
    public static bool Ask(ApprovalRequest request)
    {
        using var dlg = new ApprovalDialog(request);
        return dlg.ShowDialog() == DialogResult.Yes;
    }
}
