namespace GamepadFilter;

// Owns the relay workers so both reload and quit wait for input grabs to be released.
internal sealed class FilterHost(string configPath, string? deviceOverride, Action<string> reportError)
{
    public int Run(bool keepOpen, AutoResetEvent reload, CancellationToken stop)
    {
        CancellationTokenSource? sessionStop = null;
        Task<int>[] workers = [];
        int result = 0;
        try
        {
            bool load = true;
            while (!stop.IsCancellationRequested)
            {
                if (load)
                {
                    try
                    {
                        var profiles = FilterConfig.Read(configPath);
                        var selected = SelectDevices(profiles);
                        // Validate every attached profile before stopping any working relay.
                        foreach (var (device, profile) in selected)
                        {
                            using var source = LinuxInput.Open(device.Path);
                            GamepadRelay.Validate(source, device.Name, profile.Axes);
                        }

                        sessionStop?.Cancel();
                        Task.WhenAll(workers).GetAwaiter().GetResult();
                        sessionStop?.Dispose();
                        sessionStop = CancellationTokenSource.CreateLinkedTokenSource(stop);
                        CancellationToken sessionToken = sessionStop.Token;
                        workers = selected.Select(pair => Task.Factory.StartNew(
                            () => RunFilter(pair.Device, pair.Profile, sessionToken),
                            CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default)).ToArray();
                        result = 0;
                        Console.WriteLine($"Loaded {configPath} for {workers.Length} controller(s).");
                    }
                    catch (Exception error) when (error is IOException or UnauthorizedAccessException or FormatException)
                    {
                        reportError(error.Message);
                        result = 1;
                        if (!keepOpen)
                            break;
                    }
                    load = false;
                }

                if (!keepOpen && workers.All(worker => worker.IsCompleted))
                    break;
                load = WaitHandle.WaitAny([stop.WaitHandle, reload], 100) == 1;
            }
        }
        finally
        {
            sessionStop?.Cancel();
            int[] workerResults = Task.WhenAll(workers).GetAwaiter().GetResult();
            sessionStop?.Dispose();
            result = Math.Max(result, workerResults.DefaultIfEmpty(0).Max());
        }
        return result;
    }

    private (InputDeviceNode Device, DeviceProfile Profile)[] SelectDevices(Dictionary<string, DeviceProfile> profiles)
    {
        InputDeviceNode[] attached = InputDevices.Find();
        if (deviceOverride is not null)
        {
            string path = File.ResolveLinkTarget(deviceOverride, returnFinalTarget: true)?.FullName
                ?? Path.GetFullPath(deviceOverride);
            InputDeviceNode device = attached.FirstOrDefault(candidate => candidate.Path == path)
                ?? throw new IOException($"No input event device found at {deviceOverride}.");
            DeviceProfile profile = device.Match(profiles)
                ?? throw new FormatException($"No configuration matches {deviceOverride} ({device.Name}).");
            return profile.Enabled ? [(device, profile)] : [];
        }

        var physicalKeys = attached.Where(device => !device.IsVirtual).Select(device => device.Key).ToHashSet();
        return attached
            .Where(device => !(device.IsVirtual && (physicalKeys.Contains(device.Key)
                || device.Name.EndsWith(InputDeviceNode.FilteredSuffix, StringComparison.Ordinal))))
            .Select(device => (Device: device, Profile: device.Match(profiles)))
            .Where(pair => pair.Profile is { Enabled: true })
            .Select(pair => (pair.Device, pair.Profile!)).ToArray();
    }

    private int RunFilter(InputDeviceNode device, DeviceProfile profile, CancellationToken stop)
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
            reportError($"{device.Name} ({device.Path}): {error.Message}");
            return 1;
        }
    }
}
