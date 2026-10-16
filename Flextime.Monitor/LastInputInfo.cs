using System.Runtime.InteropServices;

namespace Flextime.Monitor;

public static class LastInputInfo {
    /// <summary>
    /// Milliseconds since the last input, or null when Windows cannot
    /// tell.
    /// </summary>
    public static uint? GetIdleTimeSinceLastInputInMilliSeconds()
    {
        var lastInputInfo = new LASTINPUTINFO();
        lastInputInfo.cbSize = (uint)Marshal.SizeOf(lastInputInfo);

        if (!GetLastInputInfo(ref lastInputInfo))
        {
            return null;
        }

        return Elapsed(Environment.TickCount, lastInputInfo.dwTime);
    }

    /// <summary>
    /// Milliseconds from <paramref name="lastInput"/> to
    /// <paramref name="tickCount"/>.  Both are the same 32-bit tick
    /// counter, which wraps after 49.7 days (and turns negative as an int
    /// after 24.9); unsigned wrap-around subtraction is right across both.
    /// </summary>
    public static uint Elapsed(int tickCount, uint lastInput) => unchecked((uint)tickCount - lastInput);

    [DllImport("User32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetLastInputInfo(ref LASTINPUTINFO plii);

    [StructLayout(LayoutKind.Sequential)]
    internal struct LASTINPUTINFO
    {
        public uint cbSize;
        public uint dwTime;
    }
}
