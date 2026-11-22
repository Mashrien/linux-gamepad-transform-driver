#!/usr/bin/env python3
"""Check the exported tray menu and activate its real DBus menu actions."""
import importlib.util
from pathlib import Path
import subprocess
import sys
import time
from contextlib import closing

from gi.repository import Gio, GLib
from evdev import ecodes as e

spec = importlib.util.spec_from_file_location("integration", Path(__file__).with_name("integration.py"))
integration = importlib.util.module_from_spec(spec)
spec.loader.exec_module(integration)
bus = Gio.bus_get_sync(Gio.BusType.SESSION, None)


def call(service, path, interface, method, parameters=None):
    return bus.call_sync(service, path, interface, method, parameters, None,
                         Gio.DBusCallFlags.NONE, 3000, None).unpack()


def eventually(function):
    deadline = time.monotonic() + 5
    while time.monotonic() < deadline:
        value = function()
        if value:
            return value
        time.sleep(0.03)
    raise AssertionError("Timed out waiting for tray state")


def tray_for(pid):
    items = call("org.kde.StatusNotifierWatcher", "/StatusNotifierWatcher",
                 "org.freedesktop.DBus.Properties", "Get",
                 GLib.Variant("(ss)", ("org.kde.StatusNotifierWatcher", "RegisteredStatusNotifierItems")))[0]
    for item in items:
        service, path = item.split("/", 1)
        owner_pid = call("org.freedesktop.DBus", "/org/freedesktop/DBus",
                         "org.freedesktop.DBus", "GetConnectionUnixProcessID",
                         GLib.Variant("(s)", (service,)))[0]
        if owner_pid == pid:
            return service, "/" + path
    return None


class TrayTests(integration.IntegrationTests):
    def test_tray_reload_and_quit(self):
        self.path.write_text(f"[{integration.NAME.replace(' ', '_')}.ABS_X]\nZeroOffset=2604\n")
        child = subprocess.Popen([integration.BINARY, self.path, "--device", self.source.device.path],
                                 stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True)
        self.addCleanup(self.stop, child)
        service, path = eventually(lambda: tray_for(child.pid))
        menu = call(service, path, "org.freedesktop.DBus.Properties", "Get",
                    GLib.Variant("(ss)", ("org.kde.StatusNotifierItem", "Menu")))[0]

        def menu_layout():
            return call(service, menu, "com.canonical.dbusmenu", "GetLayout",
                        GLib.Variant("(iias)", (0, -1, [])))[1]

        def items():
            return menu_layout()[2]

        entries = eventually(lambda: items())
        self.assertEqual([entry[1].get("label") for entry in entries], ["Reload Config", "Quit"])
        actions = {entry[1]["label"]: entry[0] for entry in entries}

        def click(label):
            call(service, menu, "com.canonical.dbusmenu", "Event",
                 GLib.Variant("(isvu)", (actions[label], "clicked", GLib.Variant("i", 0), 0)))

        with closing(integration.find_virtual_device()) as output:
            self.event(output, e.EV_ABS, e.ABS_X, 2904, 300)

        self.path.write_text(f"[{integration.NAME.replace(' ', '_')}.ABS_X]\nZeroOffset=1200\n")
        click("Reload Config")
        # Reload replaces the virtual controller after validating the new calibration.
        def updated():
            try:
                output = integration.find_virtual_device()
            except AssertionError:
                return None
            if output.absinfo(e.ABS_X).value == 1704:
                return output
            output.close()
            return None

        with closing(eventually(updated)) as output:
            self.event(output, e.EV_ABS, e.ABS_X, 1500, 300)
            original_path = output.path
            self.path.write_text(f"[{integration.NAME.replace(' ', '_')}.ABS_X]\nDeadZone=1.01\n")
            click("Reload Config")
            # A reload validation error keeps the working output and calibration active.
            time.sleep(0.4)
            self.event(output, e.EV_ABS, e.ABS_X, 1600, 400)
            self.assertEqual(output.path, original_path)
            click("Quit")
            stdout, stderr = child.communicate(timeout=3)
            self.assertEqual(child.returncode, 1, stdout)
            self.assertIn("DeadZone", stderr)
            self.assertNotIn("CRITICAL", stderr)
            self.assertNotIn("assertion", stderr)
        self.source.device.grab()
        self.source.device.ungrab()


if __name__ == "__main__":
    import unittest
    unittest.main(defaultTest="TrayTests.test_tray_reload_and_quit")
