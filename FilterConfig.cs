using System.Globalization;

namespace GamepadFilter;

internal static class FilterConfig
{
    private static readonly Dictionary<string, ushort> AxisCodes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["ABS_X"] = 0x00, ["ABS_Y"] = 0x01, ["ABS_Z"] = 0x02,
        ["ABS_RX"] = 0x03, ["ABS_RY"] = 0x04, ["ABS_RZ"] = 0x05,
        ["ABS_THROTTLE"] = 0x06, ["ABS_RUDDER"] = 0x07, ["ABS_WHEEL"] = 0x08,
        ["ABS_GAS"] = 0x09, ["ABS_BRAKE"] = 0x0a,
        ["ABS_HAT0X"] = 0x10, ["ABS_HAT0Y"] = 0x11,
        ["ABS_HAT1X"] = 0x12, ["ABS_HAT1Y"] = 0x13,
        ["ABS_HAT2X"] = 0x14, ["ABS_HAT2Y"] = 0x15,
        ["ABS_HAT3X"] = 0x16, ["ABS_HAT3Y"] = 0x17,
        ["ABS_PRESSURE"] = 0x18, ["ABS_DISTANCE"] = 0x19,
        ["ABS_TILT_X"] = 0x1a, ["ABS_TILT_Y"] = 0x1b,
        ["ABS_TOOL_WIDTH"] = 0x1c, ["ABS_VOLUME"] = 0x20,
        ["ABS_PROFILE"] = 0x21, ["ABS_SND_PROFILE"] = 0x22, ["ABS_MISC"] = 0x28,
        ["ABS_MT_SLOT"] = 0x2f,
        ["ABS_MT_TOUCH_MAJOR"] = 0x30, ["ABS_MT_TOUCH_MINOR"] = 0x31,
        ["ABS_MT_WIDTH_MAJOR"] = 0x32, ["ABS_MT_WIDTH_MINOR"] = 0x33,
        ["ABS_MT_ORIENTATION"] = 0x34, ["ABS_MT_POSITION_X"] = 0x35,
        ["ABS_MT_POSITION_Y"] = 0x36, ["ABS_MT_TOOL_TYPE"] = 0x37,
        ["ABS_MT_BLOB_ID"] = 0x38, ["ABS_MT_TRACKING_ID"] = 0x39,
        ["ABS_MT_PRESSURE"] = 0x3a, ["ABS_MT_DISTANCE"] = 0x3b,
        ["ABS_MT_TOOL_X"] = 0x3c, ["ABS_MT_TOOL_Y"] = 0x3d,
    };

    private static readonly Dictionary<ushort, string> AxisNames =
        AxisCodes.ToDictionary(pair => pair.Value, pair => pair.Key);

    public static string DeviceKey(string name) => name.Replace(' ', '_');

    public static string AxisName(ushort code) => AxisNames.GetValueOrDefault(code) ?? $"ABS_0x{code:x2}";

    private static bool TryGetAxisCode(string name, out ushort code)
    {
        return AxisCodes.TryGetValue(name, out code)
            || (name.StartsWith("ABS_0x", StringComparison.OrdinalIgnoreCase)
                && ushort.TryParse(name.AsSpan(6), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out code)
                && code <= LinuxInput.AbsMax);
    }

    public static Dictionary<string, DeviceProfile> Read(string path)
    {
        var profiles = new Dictionary<string, DeviceProfile>(StringComparer.Ordinal);
        DeviceProfile? profile = null;
        AxisOptions? axis = null;
        int number = 0;
        DeviceProfile SelectProfile(string key)
        {
            if (!profiles.TryGetValue(key, out DeviceProfile? selected))
                profiles.Add(key, selected = new DeviceProfile(key));
            return selected;
        }

        foreach (string source in File.ReadLines(path))
        {
            number++;
            int headerEnd = source.AsSpan().TrimStart().StartsWith("[") ? source.LastIndexOf(']') : -1;
            int comment = source.IndexOfAny(['#', ';'], headerEnd + 1);
            string line = (comment < 0 ? source : source[..comment]).Trim();
            if (line.Length == 0)
                continue;

            string location = $"{path}:{number}";
            if (line.StartsWith('[') && line.EndsWith(']'))
            {
                string section = line[1..^1].Trim();
                int separator = section.LastIndexOf('.');
                string axisName = separator < 0 ? section : section[(separator + 1)..].Trim();
                // A standalone axis uses the preceding device header. Dotted headings select it directly.
                if (axisName.StartsWith("ABS_", StringComparison.OrdinalIgnoreCase))
                {
                    if (separator >= 0)
                    {
                        string key = DeviceKey(section[..separator].Trim());
                        if (key.Length == 0)
                            throw new FormatException($"{location}: empty device name.");
                        profile = SelectProfile(key);
                    }
                    if (profile is null)
                        throw new FormatException($"{location}: specify a device before [{axisName}].");
                    if (!TryGetAxisCode(axisName, out ushort code))
                        throw new FormatException($"{location}: unsupported axis {axisName}.");

                    axis = new AxisOptions($"{location} [{profile.Key}.{axisName}]");
                    if (!profile.Axes.TryAdd(code, axis))
                        throw new FormatException($"{location}: duplicate axis section for {profile.Key}.{axisName}.");
                }
                else
                {
                    if (section.Length == 0)
                        throw new FormatException($"{location}: empty section name.");
                    profile = SelectProfile(DeviceKey(section));
                    axis = null;
                }
                continue;
            }

            int equals = line.IndexOf('=');
            if (profile is null || equals < 1)
                throw new FormatException($"{location}: expected key=value in a device or axis section.");
            string property = line[..equals].Trim();
            string value = line[(equals + 1)..].Trim();
            if (axis is null)
                profile.Set(property, value, location);
            else
                axis.Set(property, value, location);
        }

        return profiles;
    }
}

internal sealed class DeviceProfile(string key)
{
    private bool hasEnabled;

    public string Key { get; } = key;
    public bool Enabled { get; private set; } = true;
    public Dictionary<ushort, AxisOptions> Axes { get; } = new();

    public void Set(string key, string value, string location)
    {
        if (!key.Equals("Enabled", StringComparison.OrdinalIgnoreCase))
            throw new FormatException($"{location}: unknown device option '{key}'.");
        if (hasEnabled)
            throw new FormatException($"{location}: duplicate option 'Enabled'.");
        if (!bool.TryParse(value, out bool enabled))
            throw new FormatException($"{location}: Enabled must be true or false.");
        Enabled = enabled;
        hasEnabled = true;
    }
}

internal sealed class AxisOptions(string section)
{
    private readonly Dictionary<string, string> values = new(StringComparer.OrdinalIgnoreCase);

    public string Section { get; } = section;
    public bool IsEmpty => values.Count == 0;

    public void Set(string key, string value, string location)
    {
        string canonical = key.ToUpperInvariant() switch
        {
            "ZEROOFFSET" => "ZeroOffset",
            "DEADZONE" => "DeadZone",
            "RAWMINIMUM" => "RawMinimum",
            "RAWMAXIMUM" => "RawMaximum",
            "CLAMPMODE" => "ClampMode",
            "STRETCH" => "Stretch",
            "THRESHOLD" or "TOGGLETHRESHOLD" => "Threshold",
            _ => throw new FormatException($"{location}: unknown axis option '{key}'."),
        };
        if (!values.TryAdd(canonical, value))
            throw new FormatException($"{location}: duplicate option '{canonical}'.");
    }

    public AxisFilter Bind(AbsInfo axis)
    {
        // Keep raw calibration limits separate from the virtual axis, which must be able to report zero.
        int outputMin = Math.Min(0, axis.Minimum);
        int outputMax = Math.Max(0, axis.Maximum);
        long fullScale = Math.Max(-(long)outputMin, outputMax);
        int rawMin = Counts("RawMinimum", axis.Minimum, fullScale, outputMin, outputMax);
        int rawMax = Counts("RawMaximum", axis.Maximum, fullScale, outputMin, outputMax);
        int offset = Counts("ZeroOffset", Math.Clamp(0, axis.Minimum, axis.Maximum), fullScale);
        int deadZone = Counts("DeadZone", 0, fullScale);
        int? threshold = values.ContainsKey("Threshold") ? Counts("Threshold", 0, fullScale) : null;
        bool clamp = Flag("ClampMode", true);
        bool stretch = Flag("Stretch", false);

        if (rawMin >= rawMax)
            throw new FormatException($"{Section}: RawMinimum must be less than RawMaximum.");
        if (offset < rawMin || offset > rawMax)
            throw new FormatException($"{Section}: ZeroOffset must be within the raw range.");
        if (deadZone < 0 || threshold < 0)
            throw new FormatException($"{Section}: DeadZone and Threshold must be nonnegative.");
        if (stretch && ((outputMin < 0 && (long)offset - rawMin <= deadZone)
                        || (outputMax > 0 && (long)rawMax - offset <= deadZone)))
            throw new FormatException($"{Section}: Stretch requires travel outside the dead zone on each output side.");

        return new AxisFilter(rawMin, rawMax, offset, deadZone, threshold, clamp, stretch, outputMin, outputMax);
    }

    private bool Flag(string key, bool fallback)
    {
        if (!values.TryGetValue(key, out string? text))
            return fallback;
        if (bool.TryParse(text, out bool value))
            return value;
        throw new FormatException($"{Section}: {key} must be true or false.");
    }

    private int Counts(string key, int fallback, long fullScale, int? negativeEnd = null, int? positiveEnd = null)
    {
        if (!values.TryGetValue(key, out string? text))
            return fallback;
        // The written form distinguishes a single count (1) from full scale (1.0).
        if (int.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out int counts))
            return counts;
        if (text.IndexOfAny(['.', 'e', 'E']) >= 0
            && decimal.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out decimal fraction)
            && fraction is >= -1 and <= 1)
        {
            long reference = fullScale;
            if (fraction < 0 && negativeEnd is int minimum)
                reference = -(long)minimum;
            else if (fraction >= 0 && positiveEnd is int maximum)
                reference = maximum;
            decimal rounded = decimal.Round(fraction * reference, 0, MidpointRounding.AwayFromZero);
            if (rounded is >= int.MinValue and <= int.MaxValue)
                return (int)rounded;
        }
        throw new FormatException($"{Section}: {key} must be a signed 32-bit count or a fraction from -1.0 to 1.0 that fits in that range.");
    }
}

internal sealed record AxisFilter(
    int RawMin, int RawMax, int Offset, int DeadZone, int? Threshold,
    bool Clamp, bool Stretch, int OutputMin, int OutputMax)
{
    public int Apply(int raw)
    {
        // Recenter in 64 bits because two valid 32-bit readings can differ by more than int.MaxValue.
        long centered = (Clamp ? Math.Clamp(raw, RawMin, RawMax) : (long)raw) - Offset;
        long magnitude = Math.Abs(centered);
        if (magnitude <= DeadZone)
            return 0;

        bool negative = centered < 0;
        int endpoint = negative ? OutputMin : OutputMax;
        if (Stretch)
        {
            // Calculate travel separately on each side because the resting position can be off-center.
            long travel = negative ? (long)Offset - RawMin : (long)RawMax - Offset;
            if (endpoint == 0)
                return 0;
            long outputMagnitude = Math.Abs((long)endpoint);
            magnitude = ((magnitude - DeadZone) * outputMagnitude + (travel - DeadZone) / 2) / (travel - DeadZone);
        }

        // Threshold is evaluated in output counts after any stretching.
        if (Threshold is int threshold && magnitude >= threshold)
            return endpoint;
        long result = negative ? -magnitude : magnitude;
        return Clamp
            ? (int)Math.Clamp(result, OutputMin, OutputMax)
            : (int)Math.Clamp(result, int.MinValue, int.MaxValue);
    }
}
