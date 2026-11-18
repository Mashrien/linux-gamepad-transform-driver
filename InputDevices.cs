using System.Runtime.InteropServices;

namespace GamepadFilter;

internal sealed record InputDeviceNode(string Path, string Name, string SysPath, string Identity)
{
    public const string FilteredSuffix = " (filtered)";
    public string Key => FilterConfig.DeviceKey(Name);
    public string IdentityKey => $"{Key}@{Uri.EscapeDataString(Identity)}";
    public string EventKey => $"{IdentityKey}@{System.IO.Path.GetFileName(Path)}";
    // uinput device nodes live directly under virtual/input. A Bluetooth pad may appear under virtual/misc/uhid.
    public bool IsVirtual => SysPath.StartsWith("/sys/devices/virtual/input/", StringComparison.Ordinal);

    // Prefer the most specific profile so same-name controllers can have separate calibration.
    public DeviceProfile? Match(Dictionary<string, DeviceProfile> profiles)
        => profiles.GetValueOrDefault(EventKey)
            ?? profiles.GetValueOrDefault(IdentityKey)
            ?? profiles.GetValueOrDefault(Key);
}

internal static partial class InputDevices
{
    private const string Udev = "libudev.so.1";

    [LibraryImport(Udev)]
    private static partial nint udev_new();

    [LibraryImport(Udev)]
    private static partial nint udev_unref(nint context);

    [LibraryImport(Udev, StringMarshalling = StringMarshalling.Utf8)]
    private static partial nint udev_device_new_from_syspath(nint context, string path);

    [LibraryImport(Udev, StringMarshalling = StringMarshalling.Utf8)]
    private static partial nint udev_device_get_property_value(nint device, string key);

    [LibraryImport(Udev)]
    private static partial nint udev_device_unref(nint device);

    public static InputDeviceNode[] Find()
    {
        var devices = new List<InputDeviceNode>();
        foreach (string entry in Directory.EnumerateDirectories("/sys/class/input", "event*"))
        {
            try
            {
                string sysPath = new DirectoryInfo(entry).ResolveLinkTarget(returnFinalTarget: true)?.FullName ?? entry;
                string parent = Path.Combine(entry, "device");
                string name = ReadAttribute(parent, "name");
                if (name.Length == 0)
                    continue;

                string identity = ReadAttribute(parent, "uniq");
                if (identity.Length == 0)
                    identity = ReadAttribute(parent, "phys");
                if (identity.Length == 0)
                {
                    // The inputN and eventN names are assigned at runtime. Prefer their parent's path.
                    string inputDirectory = Path.GetDirectoryName(sysPath)!;
                    identity = Path.GetDirectoryName(inputDirectory)!;
                }
                devices.Add(new InputDeviceNode(Path.Combine("/dev/input", Path.GetFileName(entry)),
                    name, sysPath, identity));
            }
            catch (IOException)
            {
                // A device can disappear while sysfs is being enumerated.
            }
        }

        return devices
            .OrderBy(device => device.Key, StringComparer.Ordinal)
            .ThenBy(device => device.Identity, StringComparer.Ordinal)
            .ThenBy(device => device.Path, StringComparer.Ordinal)
            .ToArray();
    }

    public static InputDeviceNode[] PhysicalGamepads(IEnumerable<InputDeviceNode> devices)
    {
        nint context = udev_new();
        if (context == 0)
            throw new IOException("Could not initialize udev for gamepad discovery.");
        try
        {
            // Let udev distinguish controllers from other inputs that also expose absolute axes.
            return devices.Where(device => !device.IsVirtual && IsJoystick(context, device.SysPath)).ToArray();
        }
        finally
        {
            udev_unref(context);
        }
    }

    private static bool IsJoystick(nint context, string path)
    {
        nint device = udev_device_new_from_syspath(context, path);
        if (device == 0)
            return false;
        try
        {
            nint property = udev_device_get_property_value(device, "ID_INPUT_JOYSTICK");
            return Marshal.PtrToStringUTF8(property) == "1";
        }
        finally
        {
            udev_device_unref(device);
        }
    }

    private static string ReadAttribute(string directory, string name)
    {
        try
        {
            return File.ReadAllText(Path.Combine(directory, name)).TrimEnd('\n', '\r');
        }
        catch (FileNotFoundException) when (name is "uniq" or "phys")
        {
            return "";
        }
    }
}
