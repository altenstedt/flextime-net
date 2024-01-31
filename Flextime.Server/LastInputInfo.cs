using System.Runtime.InteropServices;

namespace Inhill.Flextime.Server;

internal static class LastInputInfo {
    public static long GetIdleTimeSinceLastInputInMilliSeconds()
    {
        var lastInputInfo = new LASTINPUTINFO();
        lastInputInfo.cbSize = (uint)Marshal.SizeOf(lastInputInfo);

        GetLastInputInfo(ref lastInputInfo);

        return Environment.TickCount - lastInputInfo.dwTime;
    }

    [DllImport("User32.dll")]
    private static extern bool GetLastInputInfo(ref LASTINPUTINFO plii);

    [StructLayout(LayoutKind.Sequential)]
    internal struct LASTINPUTINFO
    {
        public uint cbSize;
        public uint dwTime;
    }
}
 