using System.Runtime.InteropServices;
using GamepadFilter;

const string usage = "Usage: gamepad-filter [config.conf] [--device /dev/input/eventN]";
if (args.Length == 1 && args[0] is "--help" or "-h")
{
    Console.WriteLine(usage);
    Console.WriteLine("Defaults to gamepad.conf in the working directory.");
    return 0;
}

string? deviceOverride = null;
int pathArguments = args.Length;
if (args.Length >= 2 && args[^2] == "--device")
{
    deviceOverride = args[^1];
    pathArguments -= 2;
}
if (pathArguments > 1 || (pathArguments == 1 && args[0].StartsWith('-')))
{
    Console.Error.WriteLine(usage);
    return 2;
}

if (!OperatingSystem.IsLinux() || RuntimeInformation.ProcessArchitecture is not (Architecture.X64 or Architecture.Arm64))
{
    Console.Error.WriteLine("gamepad-filter requires Linux x64 or arm64.");
    return 1;
}

using var stop = new CancellationTokenSource();
void Stop(PosixSignalContext context)
{
    context.Cancel = true;
    stop.Cancel();
}

using var interrupt = PosixSignalRegistration.Create(PosixSignal.SIGINT, Stop);
using var terminate = PosixSignalRegistration.Create(PosixSignal.SIGTERM, Stop);
try
{
    string configPath = Path.GetFullPath(pathArguments == 1 ? args[0] : "gamepad.conf");
    InputDeviceNode[] attached = InputDevices.Find();
    if (!File.Exists(configPath))
    {
        try
        {
            int count = ConfigTemplate.Write(configPath, InputDevices.PhysicalGamepads(attached));
            Console.WriteLine($"Created {configPath} with {count} gamepad profile(s). Edit it, then run again.");
        }
        catch (IOException) when (File.Exists(configPath))
        {
            Console.WriteLine($"{configPath} already exists. Run again to use it.");
        }
        return 0;
    }

    var profiles = FilterConfig.Read(configPath);
    if (deviceOverride is not null)
    {
        string path = File.ResolveLinkTarget(deviceOverride, returnFinalTarget: true)?.FullName
            ?? Path.GetFullPath(deviceOverride);
        InputDeviceNode? device = attached.FirstOrDefault(candidate => candidate.Path == path);
        if (device is null)
            throw new IOException($"No input event device found at {deviceOverride}.");
        DeviceProfile profile = device.Match(profiles)
            ?? throw new FormatException($"No configuration matches {deviceOverride} ({device.Name}).");
        if (!profile.Enabled)
        {
            Console.WriteLine($"{device.Name} is disabled in {configPath}.");
            return 0;
        }
        return RunFilter(device, profile, stop.Token);
    }

    var physicalKeys = attached.Where(device => !device.IsVirtual).Select(device => device.Key).ToHashSet();
    var filters = new List<Task<int>>();
    foreach (InputDeviceNode device in attached)
    {
        // Skip our own virtual output and prefer a physical device when names collide.
        if (device.IsVirtual && (physicalKeys.Contains(device.Key)
            || device.Name.EndsWith(InputDeviceNode.FilteredSuffix, StringComparison.Ordinal)))
            continue;

        if (device.Match(profiles) is { Enabled: true } profile)
        {
            // Each relay blocks in poll, so give it a dedicated worker and share the shutdown signal.
            filters.Add(Task.Factory.StartNew(() => RunFilter(device, profile, stop.Token),
                CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default));
        }
    }

    if (filters.Count == 0)
    {
        Console.WriteLine($"No attached controllers with enabled profiles in {configPath}.");
        return 0;
    }
    int[] results = await Task.WhenAll(filters);
    return results.Max();
}
catch (Exception error) when (error is IOException or UnauthorizedAccessException or FormatException)
{
    Console.Error.WriteLine($"gamepad-filter: {error.Message}");
    return 1;
}
catch (DllNotFoundException)
{
    Console.Error.WriteLine("gamepad-filter: first-run device discovery requires libudev.so.1.");
    return 1;
}

static int RunFilter(InputDeviceNode device, DeviceProfile profile, CancellationToken stop)
{
    try
    {
        using var source = LinuxInput.Open(device.Path);
        if (LinuxInput.Name(source) != device.Name)
            throw new IOException("The input device changed during discovery.");
        GamepadRelay.Run(source, device.Name, profile.Axes, stop);
        return 0;
    }
    catch (OperationCanceledException) when (stop.IsCancellationRequested)
    {
        return 0;
    }
    catch (Exception error) when (error is IOException or UnauthorizedAccessException or FormatException)
    {
        Console.Error.WriteLine($"gamepad-filter: {device.Name} ({device.Path}): {error.Message}");
        return 1;
    }
}
