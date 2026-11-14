// A stand-in for the user's text editor in the focus tests: one GTK 4 window with a text field whose text is written to
// a file on every change.   gjs fake-editor.js "<title>" <file>
imports.gi.versions.Gtk = '4.0';
const {Gtk, GLib, Gio} = imports.gi;
const app = new Gtk.Application({application_id: 'org.umbra.TestEditor' + ARGV[0].replace(/[^a-z]/g, ''), flags: Gio.ApplicationFlags.NON_UNIQUE});
app.connect('activate', () => {
    const w = new Gtk.ApplicationWindow({application: app, title: ARGV[0], default_width: 500, default_height: 300});
    const entry = new Gtk.Entry();
    entry.connect('changed', () => GLib.file_set_contents(ARGV[1], entry.get_text()));
    w.set_child(entry);
    w.present();
});
app.run([]);
