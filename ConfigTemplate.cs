using System.Globalization;
using System.Text;
using static GamepadFilter.LinuxInput;

namespace GamepadFilter;

internal static class ConfigTemplate
{
    public static int Write(string path, InputDeviceNode[] physicalGamepads)
    {
        var text = new StringBuilder()
            .AppendLine("; Gamepad Filter configuration")
            .AppendLine("; Uncomment axis options to apply them. Empty axis sections pass through as-is.")
            .AppendLine("; Set Enabled=false to leave a controller on its original input device.");

        var sharedNames = physicalGamepads.ToLookup(device => device.Key);
        var sharedIdentities = physicalGamepads.ToLookup(device => device.IdentityKey);
        foreach (InputDeviceNode device in physicalGamepads)
        {
            using var source = Open(device.Path);
            if (Name(source) != device.Name)
                throw new IOException($"{device.Path} changed during discovery.");

            string key = device.Key;
            // Keep the simple heading for unique names and disambiguate matching controller names.
            if (sharedNames[key].Count() > 1)
                key = sharedIdentities[device.IdentityKey].Count() > 1 ? device.EventKey : device.IdentityKey;
            text.AppendLine().AppendLine($"[{key}]").AppendLine("Enabled=true");
            if (!IsSet(Capabilities(source, EvSyn, 0x1f), EvAbs))
                continue;
            bool firstAxis = true;
            foreach (ushort code in Codes(Capabilities(source, EvAbs, AbsMax)))
            {
                AbsInfo axis = Axis(source, code);
                if (axis.Minimum >= axis.Maximum)
                    continue;

                text.AppendLine($"[{FilterConfig.AxisName(code)}]");
                if (firstAxis)
                {
                    // Show the available settings once per controller and keep the other sections editable.
                    WriteExamples(text, axis);
                    firstAxis = false;
                }
            }
        }

        if (physicalGamepads.Length == 0)
            text.AppendLine("; No physical gamepads were detected. Connect one and rerun with a missing config path.");

        // Publish the whole file at once and keep an existing config if two instances start together.
        string temporaryPath = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllText(temporaryPath, text.ToString());
            File.Move(temporaryPath, path, overwrite: false);
        }
        finally
        {
            File.Delete(temporaryPath);
        }
        return physicalGamepads.Length;
    }

    private static void WriteExamples(StringBuilder text, AbsInfo axis)
    {
        text.AppendLine(CultureInfo.InvariantCulture, $"; RawMinimum={axis.Minimum} ; raw input lower limit")
            .AppendLine(CultureInfo.InvariantCulture, $"; RawMaximum={axis.Maximum} ; raw input upper limit")
            .AppendLine(CultureInfo.InvariantCulture, $"; ZeroOffset={Math.Clamp(axis.Value, axis.Minimum, axis.Maximum)} ; sampled reading, set to the raw resting position")
            .AppendLine("; DeadZone=0.01 ; ignore movement within 1% of full scale around the resting position")
            .AppendLine("; ClampMode=true ; clamp input and output to their configured ranges")
            .AppendLine("; Stretch=true ; map the available travel on each side to full output")
            .AppendLine("; Threshold=0.66 ; snap to full output at 66% of full scale")
            .AppendLine("; ToggleThreshold=0.66 ; alias for Threshold, use either name");
    }
}
