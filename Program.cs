using System.Runtime.InteropServices;
using GamepadFilter;

const string usage = "Usage: gamepad-filter [config.conf] [--device /dev/input/eventN] [--no-tray]";
if (args.Length == 1 && args[0] is "--help" or "-h")
{
    Console.WriteLine(usage);
    Console.WriteLine("Defaults to gamepad.conf in the working directory.");
    return 0;
}

bool noTray = args.Contains("--no-tray");
args = args.Where(argument => argument != "--no-tray").ToArray();
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

    using var reload = new AutoResetEvent(false);
    using TrayIcon? tray = noTray ? null : TrayIcon.TryCreate(reload, stop);
    void ReportError(string message)
    {
        if (tray is null)
            Console.Error.WriteLine($"gamepad-filter: {message}");
        else
            tray.ReportError(message);
    }

    var host = new FilterHost(configPath, deviceOverride, ReportError);
    if (tray is null)
        return host.Run(keepOpen: false, reload, stop.Token);

    Task<int> worker = Task.Run(() => host.Run(keepOpen: true, reload, stop.Token));
    try
    {
        tray.Run();
    }
    finally
    {
        stop.Cancel();
    }
    return worker.GetAwaiter().GetResult();
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
