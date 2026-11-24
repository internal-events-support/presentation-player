using System.Runtime.InteropServices;

namespace PresentationPlayer;

/// <summary>A connected display. The id stays the same across restarts and re-plugging into the same socket.</summary>
public sealed record DisplayInfo(string Id, string Name, int X, int Y, int Width, int Height, bool Primary, bool App)
{
    internal Rectangle Bounds => new(X, Y, Width, Height);
}

/// <summary>Lists displays with the names Windows shows for them, which WinForms' Screen doesn't have.</summary>
internal static class Displays
{
    public static IReadOnlyList<DisplayInfo> List()
    {
        var names = Names();
        var list = new List<DisplayInfo>();
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr monitor, IntPtr _, ref Rect _, IntPtr _) =>
        {
            var info = new MonitorInfoEx { cbSize = Marshal.SizeOf<MonitorInfoEx>() };
            if (!GetMonitorInfoW(monitor, ref info)) return true;
            var (id, name) = names.TryGetValue(info.szDevice, out var found) ? found : (info.szDevice, "");
            var r = info.rcMonitor;
            list.Add(new DisplayInfo(id, name, r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top, (info.dwFlags & 1) != 0, false));
            return true;
        }, IntPtr.Zero);

        // Tell unnamed displays, and several of the same model, apart by number: left to right, like they're arranged.
        list = list.OrderBy(d => d.X).ThenBy(d => d.Y).Select((d, i) => d.Name.Length > 0 ? d : d with { Name = $"Display {i + 1}" }).ToList();
        return list.Select(d =>
        {
            var same = list.Where(o => o.Name == d.Name).ToList();
            return same.Count > 1 ? d with { Name = $"{d.Name} ({same.IndexOf(d) + 1})" } : d;
        }).ToList();
    }

    /// <summary>The top left corner of the display a window is on.</summary>
    public static Point? PositionOf(IntPtr window)
    {
        var info = new MonitorInfoEx { cbSize = Marshal.SizeOf<MonitorInfoEx>() };
        return GetMonitorInfoW(MonitorFromWindow(window, 2), ref info) ? new Point(info.rcMonitor.Left, info.rcMonitor.Top) : null;
    }

    // Device name ("\\.\DISPLAY1") → (device path, the monitor's own name).
    private static Dictionary<string, (string Id, string Name)> Names()
    {
        var names = new Dictionary<string, (string Id, string Name)>();
        const uint onlyActive = 2;
        for (var attempt = 0; attempt < 3; attempt++)
        {
            if (GetDisplayConfigBufferSizes(onlyActive, out var pathCount, out var modeCount) != 0) return names;
            var paths = new byte[pathCount * PathSize];
            var modes = new byte[modeCount * ModeSize];
            var result = QueryDisplayConfig(onlyActive, ref pathCount, paths, ref modeCount, modes, IntPtr.Zero);
            if (result == 122) continue; // the displays changed in between
            if (result != 0) return names;

            for (var i = 0; i < pathCount; i++)
            {
                var at = i * PathSize;
                var source = new SourceName { Header = new Header { Type = 1, Size = (uint)Marshal.SizeOf<SourceName>(), AdapterId = BitConverter.ToInt64(paths, at), Id = BitConverter.ToUInt32(paths, at + 8) } };
                var target = new TargetName { Header = new Header { Type = 2, Size = (uint)Marshal.SizeOf<TargetName>(), AdapterId = BitConverter.ToInt64(paths, at + 20), Id = BitConverter.ToUInt32(paths, at + 28) } };
                if (DisplayConfigGetDeviceInfo(ref source) != 0 || DisplayConfigGetDeviceInfo(ref target) != 0) continue;
                var name = target.FriendlyName.Trim();
                // Duplicated displays share one picture.
                names[source.DeviceName] = names.TryGetValue(source.DeviceName, out var first)
                    ? (first.Id, string.Join(" + ", new[] { first.Name, name }.Where(n => n.Length > 0)))
                    : (target.DevicePath.Length > 0 ? target.DevicePath : source.DeviceName, name);
            }
            return names;
        }
        return names;
    }

    private const int PathSize = 72, ModeSize = 64; // DISPLAYCONFIG_PATH_INFO, DISPLAYCONFIG_MODE_INFO

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private struct Header
    {
        public uint Type, Size;
        public long AdapterId;
        public uint Id;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 4, CharSet = CharSet.Unicode)]
    private struct SourceName
    {
        public Header Header;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 4, CharSet = CharSet.Unicode)]
    private struct TargetName
    {
        public Header Header;
        public uint Flags, OutputTechnology;
        public ushort ManufactureId, ProductCodeId;
        public uint ConnectorInstance;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string FriendlyName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DevicePath;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left, Top, Right, Bottom;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MonitorInfoEx
    {
        public int cbSize;
        public Rect rcMonitor, rcWork;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string szDevice;
    }

    private delegate bool MonitorProc(IntPtr monitor, IntPtr dc, ref Rect rect, IntPtr data);

    [DllImport("user32.dll")] private static extern bool EnumDisplayMonitors(IntPtr dc, IntPtr clip, MonitorProc proc, IntPtr data);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool GetMonitorInfoW(IntPtr monitor, ref MonitorInfoEx info);
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr window, uint flags);
    [DllImport("user32.dll")] private static extern int GetDisplayConfigBufferSizes(uint flags, out uint paths, out uint modes);
    [DllImport("user32.dll")] private static extern int QueryDisplayConfig(uint flags, ref uint pathCount, byte[] paths, ref uint modeCount, byte[] modes, IntPtr topology);
    [DllImport("user32.dll")] private static extern int DisplayConfigGetDeviceInfo(ref SourceName request);
    [DllImport("user32.dll")] private static extern int DisplayConfigGetDeviceInfo(ref TargetName request);
}
