using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace GamepadFilter;

// GTK exports the menu over StatusNotifierItem through Ayatana, including on KDE Wayland.
// Function pointers and explicit GCHandles keep the callbacks compatible with NativeAOT.
internal sealed unsafe partial class TrayIcon : IDisposable
{
    private const string Gtk = "libgtk-3.so.0";
    private const string Indicator = "libayatana-appindicator3.so.1";
    private const string Glib = "libglib-2.0.so.0";
    private const string GObject = "libgobject-2.0.so.0";
    private readonly AutoResetEvent reload;
    private readonly CancellationTokenSource stop;
    private readonly ConcurrentQueue<string> errors = new();
    private readonly GCHandle handle;
    private readonly nint indicator;
    private readonly nint menu;
    private readonly uint timer;
    private bool running;
    private nint errorDialog;

    private TrayIcon(AutoResetEvent reload, CancellationTokenSource stop)
    {
        this.reload = reload;
        this.stop = stop;
        handle = GCHandle.Alloc(this);
        menu = gtk_menu_new();
        AddItem("Reload Config", &Reload);
        AddItem("Quit", &Quit);
        gtk_widget_show_all(menu);
        indicator = app_indicator_new("gamepad-filter", "input-gaming", 0);
        app_indicator_set_title(indicator, "Gamepad Filter");
        app_indicator_set_menu(indicator, menu);
        app_indicator_set_status(indicator, 1); // APP_INDICATOR_STATUS_ACTIVE
        timer = g_timeout_add(100, &Tick, GCHandle.ToIntPtr(handle));
    }

    public static TrayIcon? TryCreate(AutoResetEvent reload, CancellationTokenSource stop)
    {
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DISPLAY"))
            && string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WAYLAND_DISPLAY")))
            return null;
        try
        {
            if (gtk_init_check(0, 0) == 0)
                return null;
            // Resolve the optional indicator library before allocating callback state.
            nint library = NativeLibrary.Load(Indicator);
            NativeLibrary.Free(library);
            return new TrayIcon(reload, stop);
        }
        catch (Exception error) when (error is DllNotFoundException or EntryPointNotFoundException)
        {
            Console.Error.WriteLine($"gamepad-filter: tray unavailable: {error.Message}");
            return null;
        }
    }

    public void ReportError(string message)
    {
        Console.Error.WriteLine($"gamepad-filter: {message}");
        errors.Enqueue(message);
    }

    public void Run()
    {
        running = true;
        gtk_main();
        running = false;
    }

    public void Dispose()
    {
        g_source_remove(timer);
        app_indicator_set_status(indicator, 0);
        gtk_widget_destroy(menu);
        g_object_unref(indicator);
        handle.Free();
    }

    private void AddItem(string label, delegate* unmanaged[Cdecl]<nint, nint, void> action)
    {
        nint item = gtk_menu_item_new_with_label(label);
        gtk_menu_shell_append(menu, item);
        g_signal_connect_data(item, "activate", (nint)action, GCHandle.ToIntPtr(handle), 0, 0);
    }

    private static TrayIcon FromData(nint data) => (TrayIcon)GCHandle.FromIntPtr(data).Target!;

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void Reload(nint widget, nint data) => FromData(data).reload.Set();

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void Quit(nint widget, nint data) => FromData(data).stop.Cancel();

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int Tick(nint data)
    {
        TrayIcon tray = FromData(data);
        if (tray.stop.IsCancellationRequested)
        {
            if (tray.errorDialog != 0)
            {
                gtk_widget_destroy(tray.errorDialog);
                tray.errorDialog = 0;
            }
            if (tray.running)
                gtk_main_quit();
            return 1;
        }

        if (tray.errorDialog == 0 && tray.errors.TryDequeue(out string? message))
        {
            // Escape percent signs because GTK treats its last argument as a printf format.
            nint dialog = gtk_message_dialog_new(0, 0, 3, 2, message.Replace("%", "%%"));
            tray.errorDialog = dialog;
            gtk_window_set_title(dialog, "Gamepad Filter");
            g_signal_connect_data(dialog, "response",
                (nint)(delegate* unmanaged[Cdecl]<nint, int, nint, void>)&DismissError,
                data, 0, 0);
            gtk_widget_show_all(dialog);
        }
        return 1;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void DismissError(nint dialog, int response, nint data)
    {
        FromData(data).errorDialog = 0;
        gtk_widget_destroy(dialog);
    }

    [LibraryImport(Gtk)] private static partial int gtk_init_check(nint argc, nint argv);
    [LibraryImport(Gtk)] private static partial nint gtk_menu_new();
    [LibraryImport(Gtk, StringMarshalling = StringMarshalling.Utf8)] private static partial nint gtk_menu_item_new_with_label(string label);
    [LibraryImport(Gtk)] private static partial void gtk_menu_shell_append(nint menu, nint item);
    [LibraryImport(Gtk)] private static partial void gtk_widget_show_all(nint widget);
    [LibraryImport(Gtk)] private static partial void gtk_widget_destroy(nint widget);
    [LibraryImport(Gtk)] private static partial void gtk_main();
    [LibraryImport(Gtk)] private static partial void gtk_main_quit();
    [LibraryImport(Gtk, StringMarshalling = StringMarshalling.Utf8)] private static partial nint gtk_message_dialog_new(nint parent, int flags, int type, int buttons, string message);
    [LibraryImport(Gtk, StringMarshalling = StringMarshalling.Utf8)] private static partial void gtk_window_set_title(nint window, string title);
    [LibraryImport(Indicator, StringMarshalling = StringMarshalling.Utf8)] private static partial nint app_indicator_new(string id, string icon, int category);
    [LibraryImport(Indicator, StringMarshalling = StringMarshalling.Utf8)] private static partial void app_indicator_set_title(nint indicator, string title);
    [LibraryImport(Indicator)] private static partial void app_indicator_set_menu(nint indicator, nint menu);
    [LibraryImport(Indicator)] private static partial void app_indicator_set_status(nint indicator, int status);
    [LibraryImport(GObject)] private static partial void g_object_unref(nint instance);
    [LibraryImport(GObject, StringMarshalling = StringMarshalling.Utf8)] private static partial nuint g_signal_connect_data(nint instance, string signal, nint callback, nint data, nint destroy, int flags);
    [LibraryImport(Glib)] private static partial uint g_timeout_add(uint interval, delegate* unmanaged[Cdecl]<nint, int> callback, nint data);
    [LibraryImport(Glib)] private static partial int g_source_remove(uint tag);
}
