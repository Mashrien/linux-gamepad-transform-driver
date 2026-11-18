using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace GamepadFilter;

// ABI definitions from linux/input.h, linux/uinput.h and asm-generic/ioctl.h.
// On the supported 64-bit Linux targets, timeval has two 64-bit fields.
[StructLayout(LayoutKind.Sequential)]
internal struct InputEvent
{
    public long Seconds;
    public long Microseconds;
    public ushort Type;
    public ushort Code;
    public int Value;
}

[StructLayout(LayoutKind.Sequential)]
internal struct AbsInfo
{
    public int Value;
    public int Minimum;
    public int Maximum;
    public int Fuzz;
    public int Flat;
    public int Resolution;
}

[StructLayout(LayoutKind.Sequential)]
internal struct InputId
{
    public ushort Bus;
    public ushort Vendor;
    public ushort Product;
    public ushort Version;
}

internal static unsafe partial class LinuxInput
{
    public const ushort EvSyn = 0, EvKey = 1, EvRel = 2, EvAbs = 3, EvMsc = 4, EvSw = 5;
    public const ushort SynReport = 0, SynDropped = 3;
    public const int AbsMax = 0x3f;
    public const int UinputNameSize = 80;
    private const uint IoWrite = 1, IoRead = 2;
    private const char Evdev = 'E', Uinput = 'U';
    private const int Interrupted = 4, Again = 11;
    private const int Nonblocking = 0x800, CloseOnExec = 0x80000;

    [StructLayout(LayoutKind.Sequential)]
    private struct PollFd
    {
        public int Fd;
        public short Events;
        public short ReturnedEvents;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct UinputSetup
    {
        public InputId Id;
        public fixed byte Name[UinputNameSize];
        public uint EffectsMax;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct UinputAbsSetup
    {
        public ushort Code;
        // The kernel ABI aligns input_absinfo to four bytes after the axis code.
        public AbsInfo Info;
    }

    [LibraryImport("libc", EntryPoint = "open", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
    private static partial int OpenNative(string path, int flags);

    [LibraryImport("libc", EntryPoint = "ioctl", SetLastError = true)]
    private static partial int IoctlNative(int fd, nuint request, nuint argument);

    [LibraryImport("libc", EntryPoint = "poll", SetLastError = true)]
    private static partial int PollNative(PollFd* buffer, nuint count, int timeout);

    [LibraryImport("libc", EntryPoint = "read", SetLastError = true)]
    private static partial nint ReadNative(int fd, InputEvent* buffer, nuint length);

    [LibraryImport("libc", EntryPoint = "write", SetLastError = true)]
    private static partial nint WriteNative(int fd, InputEvent* buffer, nuint length);

    public static SafeFileHandle Open(string path, bool writable = false)
    {
        int fd = OpenNative(path, Nonblocking | CloseOnExec | (writable ? 1 : 0));
        if (fd < 0)
            throw Error($"open {path}");
        // Closing evdev releases its grab. Closing uinput removes the virtual device.
        return new SafeFileHandle((nint)fd, ownsHandle: true);
    }

    public static string Name(SafeFileHandle source) => ReadName(source, Evdev, 0x06);
    public static string SysName(SafeFileHandle destination) => ReadName(destination, Uinput, 44);
    public static InputId Id(SafeFileHandle source) => Get<InputId>(source, 0x02);
    public static AbsInfo Axis(SafeFileHandle source, ushort code) => Get<AbsInfo>(source, 0x40 + code);

    public static void Grab(SafeFileHandle source) => Ioctl(source, IoWrite, Evdev, 0x90, sizeof(int), 1);
    public static void Create(SafeFileHandle output) => Ioctl(output, 0, Uinput, 1, 0, 0);

    public static void Setup(SafeFileHandle output, InputId id, string name)
    {
        var setup = new UinputSetup { Id = id };
        Encoding.UTF8.GetBytes(name.AsSpan(), new Span<byte>(setup.Name, UinputNameSize - 1));
        Ioctl(output, IoWrite, Uinput, 3, sizeof(UinputSetup), (nuint)(&setup));
    }

    public static void SetupAxis(SafeFileHandle output, ushort code, AbsInfo info)
    {
        var setup = new UinputAbsSetup { Code = code, Info = info };
        Ioctl(output, IoWrite, Uinput, 4, sizeof(UinputAbsSetup), (nuint)(&setup));
    }

    public static byte[] Capabilities(SafeFileHandle source, ushort eventType, int maximum)
        => GetBits(source, 0x20 + eventType, maximum);

    public static byte[] State(SafeFileHandle source, ushort type, int maximum)
        => GetBits(source, type == EvKey ? 0x18 : 0x1b, maximum);

    public static void CopyProperties(SafeFileHandle source, SafeFileHandle output)
    {
        foreach (ushort code in Codes(GetBits(source, 0x09, 0x1f)))
            Ioctl(output, IoWrite, Uinput, 110, sizeof(int), code);
    }

    public static void EnableType(SafeFileHandle output, ushort type)
        => Ioctl(output, IoWrite, Uinput, 100, sizeof(int), type);

    public static void EnableCode(SafeFileHandle output, int request, ushort code)
        => Ioctl(output, IoWrite, Uinput, request, sizeof(int), code);

    public static bool IsSet(byte[] bits, int code) => (bits[code / 8] & (1 << (code % 8))) != 0;

    public static IEnumerable<ushort> Codes(byte[] bits)
    {
        for (ushort code = 0; code < bits.Length * 8; code++)
            if (IsSet(bits, code))
                yield return code;
    }

    public static bool WaitForInput(SafeFileHandle source)
    {
        const short pollIn = 0x01, pollFailure = 0x08 | 0x10 | 0x20;
        var poll = new PollFd { Fd = Descriptor(source), Events = pollIn };
        // Use a timeout so the relay can check for shutdown even when the pad is idle.
        int ready = PollNative(&poll, 1, 250);
        if (ready < 0 && Marshal.GetLastPInvokeError() != Interrupted)
            throw Error("poll input");
        if ((poll.ReturnedEvents & pollFailure) != 0)
            throw new IOException("The input device disconnected or became unreadable.");
        return ready > 0 && (poll.ReturnedEvents & pollIn) != 0;
    }

    public static int Read(SafeFileHandle source, Span<InputEvent> events)
    {
        nint bytes;
        fixed (InputEvent* buffer = events)
            bytes = ReadNative(Descriptor(source), buffer, (nuint)(events.Length * sizeof(InputEvent)));
        if (bytes > 0)
            return (int)(bytes / sizeof(InputEvent));
        if (bytes < 0 && Marshal.GetLastPInvokeError() is Again or Interrupted)
            return 0;
        if (bytes == 0)
            throw new IOException("The input device disconnected.");
        throw Error("read input");
    }

    public static void Write(SafeFileHandle output, InputEvent input)
    {
        nint bytes;
        do
        {
            bytes = WriteNative(Descriptor(output), &input, (nuint)sizeof(InputEvent));
        } while (bytes < 0 && Marshal.GetLastPInvokeError() == Interrupted);

        if (bytes != sizeof(InputEvent))
            throw bytes < 0 ? Error("write /dev/uinput") : new IOException("Short write to /dev/uinput.");
    }

    private static string ReadName(SafeFileHandle file, char type, int request)
    {
        Span<byte> buffer = stackalloc byte[256];
        buffer.Clear();
        fixed (byte* pointer = buffer)
            Ioctl(file, IoRead, type, request, buffer.Length, (nuint)pointer);
        int terminator = buffer.IndexOf((byte)0);
        return Encoding.UTF8.GetString(terminator < 0 ? buffer : buffer[..terminator]);
    }

    private static T Get<T>(SafeFileHandle file, int request) where T : unmanaged
    {
        T result = default;
        Ioctl(file, IoRead, Evdev, request, sizeof(T), (nuint)(&result));
        return result;
    }

    private static byte[] GetBits(SafeFileHandle file, int request, int maximum)
    {
        var bits = new byte[maximum / 8 + 1];
        fixed (byte* pointer = bits)
            Ioctl(file, IoRead, Evdev, request, bits.Length, (nuint)pointer);
        return bits;
    }

    private static void Ioctl(SafeFileHandle file, uint direction, char type, int request, int size, nuint argument)
    {
        // Recreate the Linux _IOC macro so requests containing a struct size stay in sync with its layout.
        uint command = (direction << 30) | ((uint)size << 16) | ((uint)type << 8) | (uint)request;
        while (IoctlNative(Descriptor(file), command, argument) < 0)
        {
            if (Marshal.GetLastPInvokeError() != Interrupted)
                throw Error($"ioctl {type}/0x{request:x2}");
        }
    }

    private static int Descriptor(SafeFileHandle file) => (int)file.DangerousGetHandle();

    private static IOException Error(string operation)
    {
        int code = Marshal.GetLastPInvokeError();
        return new IOException($"{operation}: {Marshal.GetPInvokeErrorMessage(code)} (errno {code})");
    }
}
