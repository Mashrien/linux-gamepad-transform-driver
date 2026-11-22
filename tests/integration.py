#!/usr/bin/env python3
"""Exercise the published executable using a kernel uinput gamepad."""
import os
from contextlib import closing
from pathlib import Path
import select
import subprocess
import sys
import tempfile
import time
import unittest

from evdev import AbsInfo, InputDevice, UInput, ecodes as e

BINARY = Path(sys.argv.pop(1)).resolve()
NAME = f"Gamepad Filter Test {os.getpid()}"
CAPABILITIES = {
    e.EV_ABS: [
        (e.ABS_X, AbsInfo(2604, -32768, 32767, 0, 4096, 0)),
        (e.ABS_Y, AbsInfo(0, -32768, 32767, 0, 42, 0)),
        (e.ABS_Z, AbsInfo(0, 0, 255, 0, 0, 0)),
    ],
    e.EV_KEY: [
        e.BTN_SOUTH, e.BTN_EAST, e.BTN_NORTH, e.BTN_WEST,
        e.BTN_TL, e.BTN_TR, e.BTN_SELECT, e.BTN_START,
        e.BTN_MODE, e.BTN_THUMBL, e.BTN_THUMBR,
    ],
}


def find_virtual_device(source_name=NAME):
    deadline = time.monotonic() + 3
    last_error = None
    while time.monotonic() < deadline:
        for entry in Path("/sys/class/input").glob("event*"):
            try:
                if (entry / "device/name").read_text().strip() == f"{source_name} (filtered)":
                    return InputDevice(f"/dev/input/{entry.name}")
            except (FileNotFoundError, PermissionError) as error:
                # The event node can appear before udev grants seat access.
                last_error = error
        time.sleep(0.02)
    raise AssertionError("filtered gamepad did not become readable") from last_error


class IntegrationTests(unittest.TestCase):
    def setUp(self):
        self.source = self.enterContext(UInput(CAPABILITIES, name=NAME))
        self.assertIsNotNone(self.source.device, "test requires read access to the uinput gamepad")
        self.source.write(e.EV_ABS, e.ABS_X, 2604)
        self.source.syn()
        self.path = Path(self.enterContext(tempfile.TemporaryDirectory())) / "gamepad.conf"

    def launch(self, config, explicit_device=True):
        self.path.write_text(config.replace("DEVICE", NAME.replace(" ", "_")))
        self.assertTrue(BINARY.is_file(), f"filter has not been built: {BINARY}")
        command = [BINARY, "--no-tray", self.path]
        if explicit_device:
            command.extend(("--device", self.source.device.path))
        return subprocess.Popen(command, stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True)

    def stop(self, child):
        if child.poll() is None:
            child.terminate()
        return child.communicate(timeout=3)

    def start_filter(self, config, explicit_device=True):
        child = self.launch(config, explicit_device)
        self.addCleanup(self.stop, child)
        output = self.enterContext(closing(find_virtual_device()))
        return child, output

    def event(self, output, event_type, code, raw_value, expected, source=None):
        source = source or self.source
        source.write(event_type, code, raw_value)
        source.syn()
        deadline = time.monotonic() + 2
        while time.monotonic() < deadline:
            ready, _, _ = select.select([output], [], [], max(0, deadline - time.monotonic()))
            if ready:
                for event in output.read():
                    if (event.type, event.code) == (event_type, code):
                        self.assertEqual(event.value, expected)
                        return
        self.fail(f"no output for event {event_type}/{code} with raw value {raw_value}")

    def test_offset_fractional_deadzone_and_passthrough(self):
        child, output = self.start_filter(
            "[DEVICE.ABS_X]\nZeroOffset=2604\nDeadZone=0.01 # 328 counts\n",
            explicit_device=False,
        )
        self.assertEqual((output.absinfo(e.ABS_X).value, output.absinfo(e.ABS_X).flat), (0, 0))
        with closing(InputDevice(self.source.device.path)) as original:
            self.event(output, e.EV_ABS, e.ABS_X, 2933, 329)
            self.event(output, e.EV_ABS, e.ABS_X, 2932, 0)
            self.event(output, e.EV_ABS, e.ABS_X, 2275, -329)
            self.event(output, e.EV_ABS, e.ABS_X, 2276, 0)
            self.event(output, e.EV_ABS, e.ABS_Y, 1000, 1000)
            self.event(output, e.EV_KEY, e.BTN_SOUTH, 1, 1)
            self.assertFalse(select.select([original], [], [], 0)[0])
            self.stop(child)
            self.assertEqual(child.returncode, 0)
            self.event(original, e.EV_KEY, e.BTN_SOUTH, 0, 0)

    def test_stretch_clamp_and_signed_threshold(self):
        _, output = self.start_filter(
            "[DEVICE]\n[ABS_X]\nZeroOffset=2604\nDeadZone=256\n"
            "RawMinimum=-10000\nRawMaximum=10000\nClampMode=true\nStretch=true\nThreshold=0.66\n"
        )
        self.event(output, e.EV_ABS, e.ABS_X, 6430, 16384)
        self.event(output, e.EV_ABS, e.ABS_X, 9900, 32767)
        self.event(output, e.EV_ABS, e.ABS_X, 2604, 0)
        self.event(output, e.EV_ABS, e.ABS_X, -3826, -16384)
        self.event(output, e.EV_ABS, e.ABS_X, -11000, -32768)

    def test_clamp_and_threshold_use_integer_counts_without_stretch(self):
        _, output = self.start_filter(
            "[DEVICE.ABS_X]\nZeroOffset=2604\nRawMinimum=-12000\nRawMaximum=12000\nClampMode=true\n"
            "[DEVICE.ABS_Z]\nToggleThreshold=0.5\n"
        )
        self.event(output, e.EV_ABS, e.ABS_X, 15000, 9396)
        self.event(output, e.EV_ABS, e.ABS_X, -15000, -14604)
        self.event(output, e.EV_ABS, e.ABS_Z, 127, 127)
        self.event(output, e.EV_ABS, e.ABS_Z, 128, 255)
        self.event(output, e.EV_ABS, e.ABS_Z, 126, 126)

    def test_negative_decimal_offset_and_integer_deadzone(self):
        _, output = self.start_filter("[DEVICE.ABS_X]\nZeroOffset=-0.25\nDeadZone=1\n")
        self.event(output, e.EV_ABS, e.ABS_X, -8190, 2)
        self.event(output, e.EV_ABS, e.ABS_X, -8191, 0)
        self.event(output, e.EV_ABS, e.ABS_X, -8194, -2)

    def test_invalid_fraction_is_rejected(self):
        child = self.launch("[DEVICE.ABS_X]\nDeadZone=1.01\n")
        self.addCleanup(self.stop, child)
        stdout, stderr = child.communicate(timeout=3)
        self.assertNotEqual(child.returncode, 0, stdout)
        self.assertIn("DeadZone", stderr)
        self.source.device.grab()
        self.source.device.ungrab()

    def test_unclamped_values_still_fit_signed_32_bit(self):
        _, output = self.start_filter("[DEVICE.ABS_X]\nZeroOffset=2604\nClampMode=false\n")
        self.event(output, e.EV_ABS, e.ABS_X, -32768, -35372)
        self.event(output, e.EV_ABS, e.ABS_X, -2147483648, -2147483648)

    def test_missing_default_config_creates_template_and_exits(self):
        result = subprocess.run([BINARY, "--no-tray"], cwd=self.path.parent, capture_output=True, text=True, timeout=3)
        self.assertEqual(result.returncode, 0, result.stderr)
        template = self.path.read_text()
        self.assertNotIn(NAME.replace(" ", "_"), template)

        # The optional hardware fixture checks the physical discovery and template contents.
        if path := os.getenv("GAMEPAD_TEST_PHYSICAL"):
            with closing(InputDevice(path)) as device:
                title = f"[{device.name.replace(' ', '_')}]\n"
                profile = template.split(title, 1)[1].split("\n\n", 1)[0]
                self.assertTrue(profile.startswith("Enabled=true\n"))
                axes = [
                    e.ABS[code] for code, info in device.capabilities()[e.EV_ABS]
                    if info.min < info.max
                ]
                self.assertEqual([line[1:-1] for line in profile.splitlines() if line.startswith("[")], axes)
                first_axis = profile.split(f"[{axes[0]}]\n", 1)[1].split("\n[", 1)[0]
                for field in ("RawMinimum", "RawMaximum", "ZeroOffset", "DeadZone",
                              "ClampMode", "Stretch", "Threshold", "ToggleThreshold"):
                    self.assertRegex(first_axis, rf"(?m)^; {field}=")
                self.assertEqual(profile.count("; ZeroOffset="), 1)
                device.grab()
                device.ungrab()

    def test_absent_explicit_config_is_created_at_that_path(self):
        path = self.path.with_name("controllers.conf")
        result = subprocess.run([BINARY, "--no-tray", path], cwd=path.parent, capture_output=True, text=True, timeout=3)
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertTrue(path.is_file())
        self.assertFalse(self.path.exists())

    def test_one_config_relays_two_enabled_devices_at_once(self):
        second_name = f"{NAME} Second"
        second = self.enterContext(UInput(CAPABILITIES, name=second_name))
        config = (
            "[DEVICE]\nEnabled=true\n[ABS_X]\nZeroOffset=2604\n[ABS_Y]\n"
            f"[{second_name.replace(' ', '_')}]\nEnabled=true\n[ABS_X]\nZeroOffset=1200\n"
        )
        _, first_output = self.start_filter(config, explicit_device=False)
        second_output = self.enterContext(closing(find_virtual_device(second_name)))
        self.assertEqual(first_output.absinfo(e.ABS_Y).flat, 42)
        self.event(first_output, e.EV_ABS, e.ABS_X, 2904, 300)
        self.event(second_output, e.EV_ABS, e.ABS_X, 1400, 200, source=second)

    def test_same_name_devices_can_have_separate_profiles(self):
        second = self.enterContext(UInput(CAPABILITIES, name=NAME, phys="other-port"))
        config = (
            "[DEVICE@py-evdev-uinput]\nEnabled=true\n[ABS_X]\nZeroOffset=2604\n"
            "[DEVICE@other-port]\nEnabled=false\n"
        )
        _, output = self.start_filter(config, explicit_device=False)
        self.event(output, e.EV_ABS, e.ABS_X, 2904, 300)
        second.device.grab()
        second.device.ungrab()

    def test_disabled_profile_skips_device_and_leaves_config_intact(self):
        config = f"[{NAME.replace(' ', '_')}]\nEnabled=false\n[ABS_X]\nZeroOffset=2604\n"
        self.path.write_text(config)
        result = subprocess.run([BINARY, "--no-tray"], cwd=self.path.parent, capture_output=True, text=True, timeout=3)
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertEqual(self.path.read_text(), config)
        self.source.device.grab()
        self.source.device.ungrab()


if __name__ == "__main__":
    unittest.main()
