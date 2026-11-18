using System.Text;
using Microsoft.Win32.SafeHandles;
using static GamepadFilter.LinuxInput;

namespace GamepadFilter;

internal static class GamepadRelay
{
    private static readonly (ushort Type, int Maximum, int SetCodeRequest)[] InputTypes =
    [
        (EvKey, 0x2ff, 101), (EvRel, 0x0f, 102), (EvAbs, AbsMax, 103),
        (EvMsc, 0x07, 104), (EvSw, 0x11, 109),
    ];

    public static void Run(
        SafeFileHandle source, string name, Dictionary<ushort, AxisOptions> options, CancellationToken stop)
    {
        byte[] types = Capabilities(source, EvSyn, 0x1f);
        var codes = new Dictionary<ushort, ushort[]>();
        foreach (var (type, maximum, _) in InputTypes)
            if (IsSet(types, type))
                codes.Add(type, Codes(Capabilities(source, type, maximum)).ToArray());

        ushort[] axes = codes.GetValueOrDefault(EvAbs) ?? [];
        var filters = new AxisFilter?[AbsMax + 1];
        foreach (var (code, axis) in options)
        {
            // Generated empty sections keep the axis's native range and calibration metadata.
            if (axis.IsEmpty)
                continue;
            if (!axes.Contains(code))
                throw new FormatException($"{axis.Section}: {name} does not expose this axis.");
            filters[code] = axis.Bind(Axis(source, code));
        }

        string virtualName = VirtualName(name);
        using var output = Open("/dev/uinput", writable: true);
        Setup(output, Id(source), virtualName);
        CopyProperties(source, output);

        foreach (var (type, _, request) in InputTypes)
        {
            if (!codes.TryGetValue(type, out ushort[]? supported))
                continue;

            EnableType(output, type);
            foreach (ushort code in supported)
            {
                if (type != EvAbs)
                {
                    EnableCode(output, request, code);
                    continue;
                }

                AbsInfo info = Axis(source, code);
                if (filters[code] is AxisFilter filter)
                {
                    info.Minimum = filter.OutputMin;
                    info.Maximum = filter.OutputMax;
                    // Clear virtual fuzz and flat so the relay controls the output dead zone.
                    info.Fuzz = 0;
                    info.Flat = 0;
                    info.Value = filter.Apply(info.Value);
                }
                SetupAxis(output, code, info);
            }
        }

        // Finish validating and configuring the virtual device before taking over the original.
        stop.ThrowIfCancellationRequested();
        Grab(source);
        Create(output);
        SyncState(source, output, codes, axes, filters);
        Console.WriteLine($"Filtering {name} through {virtualName} ({SysName(output)}). Press Ctrl+C to stop.");

        Span<InputEvent> buffer = stackalloc InputEvent[64];
        bool dropped = false;
        while (!stop.IsCancellationRequested)
        {
            if (!WaitForInput(source))
                continue;

            int count = Read(source, buffer);
            for (int index = 0; index < count; index++)
            {
                InputEvent input = buffer[index];
                if (input is { Type: EvSyn, Code: SynDropped })
                {
                    // After an evdev queue overrun, discard the incomplete frame and rebuild state.
                    dropped = true;
                    continue;
                }
                if (dropped)
                {
                    if (input is { Type: EvSyn, Code: SynReport })
                    {
                        SyncState(source, output, codes, axes, filters);
                        dropped = false;
                    }
                    continue;
                }
                if (input.Type != EvSyn && !codes.ContainsKey(input.Type))
                    continue;

                if (input.Type == EvAbs && filters[input.Code] is AxisFilter filter)
                    input.Value = filter.Apply(input.Value);
                Write(output, input);
            }
        }
    }

    private static void SyncState(
        SafeFileHandle source, SafeFileHandle output, Dictionary<ushort, ushort[]> codes,
        ushort[] axes, AxisFilter?[] filters)
    {
        foreach (ushort axis in axes)
        {
            int raw = Axis(source, axis).Value;
            int value = filters[axis]?.Apply(raw) ?? raw;
            Write(output, new InputEvent { Type = EvAbs, Code = axis, Value = value });
        }

        foreach (var (type, maximum, _) in InputTypes)
        {
            if (type is not (EvKey or EvSw) || !codes.TryGetValue(type, out ushort[]? supported))
                continue;
            byte[] state = State(source, type, maximum);
            foreach (ushort code in supported)
                Write(output, new InputEvent { Type = type, Code = code, Value = IsSet(state, code) ? 1 : 0 });
        }
        Write(output, new InputEvent { Type = EvSyn, Code = SynReport });
    }

    private static string VirtualName(string name)
    {
        const string suffix = InputDeviceNode.FilteredSuffix;
        var shortened = new StringBuilder();
        int bytes = suffix.Length;
        foreach (Rune rune in name.EnumerateRunes())
        {
            bytes += rune.Utf8SequenceLength;
            if (bytes >= UinputNameSize)
                break;
            shortened.Append(rune.ToString());
        }
        return shortened.Append(suffix).ToString();
    }
}
