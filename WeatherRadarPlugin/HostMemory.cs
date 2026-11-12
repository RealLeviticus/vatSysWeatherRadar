using System;
using System.Diagnostics;
using System.Linq;
using System.Reflection;

namespace WeatherRadarPlugin;

/// <summary>
/// Keeps a memory leak in vatSys's own map painting from being made fatal by this overlay.
///
/// Every time vatSys repaints its map layer (ASDControlDX.PaintMap, build 0.4.9305) it
/// creates a Direct2D geometry and a geometry sink for each filled polygon. It disposes the
/// geometry but never the sink, and SharpDX does not release an undisposed object when it is
/// garbage collected unless told to. Each polygon therefore costs about 0.5 KB that is never
/// returned, on every repaint. A normal profile has a few hundred filled polygons and the
/// loss goes unnoticed. The radar overlay has around 20,000, so each repaint lost about
/// 10 MB, and repaints happen on every pan, zoom, animation frame and transparency change.
/// vatSys is a 32-bit program with at most 4 GB of address space; dragging the transparency
/// slider was enough to exhaust it in about two minutes.
///
/// Two defences:
///   1. SharpDX has a documented switch, Configuration.EnableReleaseOnFinalizer, that makes
///      it release undisposed objects when the garbage collector finalises them. With it on,
///      the same repaint loop holds steady instead of growing (measured: 451 MB after 40
///      repaints without it, about 120 MB with it). vatSys creates its Direct2D factory as
///      multi-threaded, so a release from the finaliser thread is safe.
///   2. If memory still climbs towards the limit for any reason, the overlay switches
///      itself off and says why, rather than taking the client down mid-session.
/// </summary>
internal static class HostMemory
{
    /// <summary>
    /// Private memory at which the overlay stops. A 32-bit process fails well before its
    /// 4 GB of address space is full, and vatSys was seen failing at about 3 GB private.
    /// </summary>
    internal const long LimitBytes = 1800L * 1024 * 1024;

    private static readonly object Gate = new();
    private static FieldInfo? _switch;
    private static bool _original;

    /// <summary>
    /// Turns SharpDX's release-on-finalizer switch on for the rest of the session. Returns
    /// null on success, or why it could not be done. Safe to call repeatedly.
    /// </summary>
    internal static string? ReleaseLeakedGraphicsObjects()
    {
        lock (Gate)
        {
            if (_switch is not null) return null;
            try
            {
                // Looked up in the assembly vatSys already has loaded, by reflection, so the
                // plugin does not depend on SharpDX at compile time.
                var sharpDx = AppDomain.CurrentDomain.GetAssemblies()
                    .FirstOrDefault(assembly => assembly.GetName().Name == "SharpDX");
                var field = sharpDx?.GetType("SharpDX.Configuration", throwOnError: false)
                    ?.GetField("EnableReleaseOnFinalizer", BindingFlags.Public | BindingFlags.Static);
                if (field is null || field.FieldType != typeof(bool))
                    return "SharpDX's EnableReleaseOnFinalizer switch was not found";

                _original = (bool)field.GetValue(null)!;
                field.SetValue(null, true);
                _switch = field;

                // At process exit the runtime finalises every remaining object in no
                // particular order, when Direct2D itself may already be gone. Put the switch
                // back first so shutdown behaves exactly as it did before.
                AppDomain.CurrentDomain.ProcessExit += (_, _) => Restore();
                return null;
            }
            catch (Exception ex)
            {
                return ex.Message;
            }
        }
    }

    private static void Restore()
    {
        try { _switch?.SetValue(null, _original); }
        catch { /* Nothing useful can be done while the process is exiting. */ }
    }

    /// <summary>The process's private memory in bytes, or 0 if it cannot be read.</summary>
    internal static long PrivateBytes()
    {
        try
        {
            using var process = Process.GetCurrentProcess();
            return process.PrivateMemorySize64;
        }
        catch
        {
            return 0;
        }
    }
}
