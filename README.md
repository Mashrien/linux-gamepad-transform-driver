# Gamepad Filter

A foreground Linux gamepad filter written in C# and published with .NET NativeAOT. It grabs each enabled controller's evdev device and creates a virtual controller named `<original name> (filtered)`. Buttons and unconfigured axes pass through. For a configured axis, the virtual device uses the filter's dead zone in place of the source axis's advertised flat and fuzz settings, as well as allowing redefinition / calibration of the axis's zero point. (eg; Device axis fails to return to 0, instead some deviated value.)

## Linux requirements

The release targets **Linux x86-64** and includes its .NET runtime. It needs two system libraries:

| Library | Debian and Ubuntu | Fedora |
| --- | --- | --- |
| glibc 2.34 or newer (provides `libc.so.6` and `libm.so.6`) | `libc6` | `glibc` |
| `libudev.so.1` (used to detect physical controllers during config generation) | `libudev1` | `systemd-libs` |

The kernel needs evdev and uinput enabled. The account running the filter needs read access to the controller's `/dev/input/event*` and write access to `/dev/uinput`. If uinput is available as a module and the node is missing, load it with `sudo modprobe uinput`.

An ARM64 system needs an ARM64 build. The release uses the glibc loader, so musl-based distributions such as Alpine need a separate build.

## Install and run

Download a package from [GitHub Releases](https://github.com/Mashrien/linux-gamepad-transform-driver/releases). Use the package manager to install the local file and resolve its dependencies from your configured repositories.

```sh
# Debian and Ubuntu
sudo apt install ./gamepad-filter_0.1.0-1_amd64.deb

# Fedora
sudo dnf install ./gamepad-filter-0.1.0-1.x86_64.rpm

# After installing either package
gamepad-filter
```

For the generic ZIP, install the libraries above, extract the archive, and run `./gamepad-filter` from the extracted directory. From a source checkout, the build below puts the executable at `./out/gamepad-filter`.

The first run creates `gamepad.conf` in the working directory and exits so you can edit it. It uses Linux's udev joystick classification to list physical controllers and adds a section for each absolute axis with a reported range. The first axis section for each controller shows a commented example of every axis option. Uncomment and edit the settings you want to use. Empty sections leave those axes unchanged.

On later runs, the filter reads the same file and launches every enabled profile whose controller is attached. Set `Enabled=false` in a controller's section to leave it on its physical input device. Start the filter before the game and choose the device with the `(filtered)` suffix if the game lists both. Press Ctrl+C to release the controllers.

Pass a filename to use a different config. The supplied `afterglow.conf` uses `ZeroOffset=2604` and a 328-count dead zone on `ABS_X`. A missing custom path gets a new template, too. Add `--device /dev/input/eventN` or a stable `/dev/input/by-id/…-event-joystick` path to run one controller from the shared config.

The generated INI uses a device header such as `[Afterglow_Gamepad_for_Xbox_360]` followed by axis headers such as `[ABS_X]`. Device names are case-sensitive, with spaces or underscores accepted for spaces. When attached devices share a name, generation adds identity suffixes so their settings can differ. Older dotted sections such as `[Afterglow_Gamepad_for_Xbox_360.ABS_X]` still work and default to enabled.

## Axis options

| Option | Effect |
| --- | --- |
| `RawMinimum`, `RawMaximum` | Raw calibration endpoints. Default to the controller's reported axis limits. |
| `ZeroOffset` | The raw resting position. Subtracted from each reading. Defaults to zero, bounded to the device's reported range. |
| `DeadZone` | Inclusive radius around the resting position that reports exactly zero. Defaults to zero. |
| `ClampMode` | Clamps raw readings to the calibrated input range and final output to the virtual axis range. Defaults to true. With false, output can exceed the advertised axis range and still fits in a signed 32-bit integer. |
| `Stretch` | Maps the travel outside the dead zone to full output on each side independently. Defaults to false. With false, readings outside the dead zone keep their offset-corrected magnitude. |
| `Threshold` | Optional magnitude at which the output snaps to the corresponding signed endpoint. Checked after stretch. Output becomes proportional again below the threshold. `ToggleThreshold` is an alias. |

Whole numbers are raw counts. Decimal values from −1.0 to +1.0 are fractions, rounded to the nearest count, with halfway values rounded away from zero. `ZeroOffset`, `DeadZone`, and `Threshold` use the larger magnitude of the device's advertised endpoints, so `DeadZone=0.01` on `ABS_X` means `round(0.01 × 32768) = 328`. Fractional raw endpoints use the device endpoint on their own side, so `RawMaximum=1.0` uses the reported positive maximum, `32767` on this pad.

This version is for gamepad input. Rumble forwarding and automatic reconnection are outside its scope. Restart the process after reconnecting the controller or editing its config.

## Build and test

With the .NET 10 SDK and Clang installed, publish a Linux x64 executable:

```sh
dotnet publish GamepadFilter.csproj -c Release -r linux-x64 -p:RuntimeFrameworkVersion=10.0.14 -o out
```

The version shown reproduces the delivered build on the installed SDK. On a newer SDK that bundles a later .NET 10 patch, you can omit the override. The published executable uses the Linux C library and needs no separately installed .NET runtime.

After publishing, build the three release packages and their SHA-256 checksums with `./packaging/build-release.sh 0.1.0 10.0.14`. This uses `dpkg-deb`, `rpmbuild`, `readelf`, `zip`, and `unzip`, and writes assets under `out/release`.

The integration tests use Python's `evdev` module and create temporary gamepads through `/dev/uinput`:

```sh
python3 tests/integration.py out/gamepad-filter -v
```

Set `GAMEPAD_TEST_PHYSICAL` to an attached physical controller's event node to also check its generated axis list and examples.
