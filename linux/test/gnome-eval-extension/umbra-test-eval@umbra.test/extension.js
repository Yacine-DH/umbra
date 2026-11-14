// SPDX-License-Identifier: GPL-2.0-or-later OR MIT
// TEST ONLY (linux/test/gnome.sh): unsafe mode in a throwaway headless GNOME Shell, so org.gnome.Shell.Eval answers.
import {Extension} from 'resource:///org/gnome/shell/extensions/extension.js';

export default class UnsafeForTests extends Extension {
    enable() { global.context.unsafe_mode = true; }
    disable() { global.context.unsafe_mode = false; }
}
