using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;

namespace LightWeaver.Player.Native;

/// <summary>
/// Raw libmpv client API bindings (client.h). String parameters marshal as UTF-8.
/// Strings *returned* by mpv come back as <see cref="nint"/>: heap strings
/// (mpv_get_property_string) must be freed with <see cref="mpv_free"/>;
/// mpv_error_string returns static memory and must NOT be freed.
/// </summary>
internal static partial class LibMpv
{
    private const string Lib = "libmpv-2";

    static LibMpv()
    {
        // Bundled dll next to the exe wins; returning Zero falls back to the default
        // search order (PATH etc.).
        NativeLibrary.SetDllImportResolver(Assembly.GetExecutingAssembly(), static (name, _, _) =>
        {
            if (name != Lib)
                return nint.Zero;
            var bundled = Path.Combine(AppContext.BaseDirectory, "libmpv-2.dll");
            return NativeLibrary.TryLoad(bundled, out var handle) ? handle : nint.Zero;
        });
    }

    [LibraryImport(Lib)]
    internal static partial nint mpv_create();

    [LibraryImport(Lib)]
    internal static partial int mpv_initialize(nint ctx);

    [LibraryImport(Lib)]
    internal static partial void mpv_terminate_destroy(nint ctx);

    [LibraryImport(Lib)]
    internal static partial void mpv_wakeup(nint ctx);

    /// <returns>mpv_event* (never null); cast to <see cref="MpvEvent"/>. Valid until the next call.</returns>
    [LibraryImport(Lib)]
    internal static partial nint mpv_wait_event(nint ctx, double timeout);

    [LibraryImport(Lib)]
    internal static partial void mpv_free(nint data);

    /// <returns>Static string — do NOT mpv_free.</returns>
    [LibraryImport(Lib)]
    internal static partial nint mpv_error_string(int error);

    [LibraryImport(Lib, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial int mpv_set_option_string(nint ctx, string name, string data);

    [LibraryImport(Lib, StringMarshalling = StringMarshalling.Utf8)]
    internal static unsafe partial int mpv_set_option(nint ctx, string name, MpvFormat format, void* data);

    [LibraryImport(Lib, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial int mpv_set_property_string(nint ctx, string name, string data);

    /// <returns>Heap char* — read with PtrToStringUTF8, then mpv_free. Zero if unavailable.</returns>
    [LibraryImport(Lib, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial nint mpv_get_property_string(nint ctx, string name);

    [LibraryImport(Lib, StringMarshalling = StringMarshalling.Utf8)]
    internal static unsafe partial int mpv_set_property(nint ctx, string name, MpvFormat format, void* data);

    [LibraryImport(Lib, StringMarshalling = StringMarshalling.Utf8)]
    internal static unsafe partial int mpv_get_property(nint ctx, string name, MpvFormat format, void* data);

    [LibraryImport(Lib, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial int mpv_observe_property(nint ctx, ulong replyUserdata, string name, MpvFormat format);

    [LibraryImport(Lib, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial int mpv_request_log_messages(nint ctx, string minLevel);

    [LibraryImport(Lib)]
    private static partial int mpv_command(nint ctx, nint argv);

    /// <summary>mpv_command with a NULL-terminated UTF-8 char** built from managed strings.</summary>
    internal static unsafe int Command(nint ctx, params string[] args)
    {
        var argv = new nint[args.Length + 1]; // trailing element stays Zero = NULL terminator
        try
        {
            for (var i = 0; i < args.Length; i++)
                argv[i] = Marshal.StringToCoTaskMemUTF8(args[i]);
            fixed (nint* p = argv)
            {
                return mpv_command(ctx, (nint)p);
            }
        }
        finally
        {
            for (var i = 0; i < args.Length; i++)
                if (argv[i] != nint.Zero)
                    Marshal.FreeCoTaskMem(argv[i]);
        }
    }

    internal static string ErrorString(int error)
        => Marshal.PtrToStringUTF8(mpv_error_string(error)) ?? $"mpv error {error}";
}
