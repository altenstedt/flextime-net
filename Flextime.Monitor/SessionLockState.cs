using System.Runtime.InteropServices;

namespace Flextime.Monitor;

/// <summary>
/// Whether the Windows session this process runs in is locked right now.
/// The session switch events only tell of a change, so a listener that
/// starts while locked reads the state here once.
/// </summary>
public static partial class SessionLockState
{
    // WTS_CURRENT_SERVER_HANDLE and WTS_CURRENT_SESSION: this computer,
    // the caller's own session, which any user may query.
    private static readonly IntPtr CurrentServer = IntPtr.Zero;
    private const int CurrentSession = -1;

    // WTS_INFO_CLASS.WTSSessionInfoEx, answered with a WTSINFOEXW.
    private const int SessionInfoEx = 25;

    // WTSINFOEXW is a DWORD Level and then a WTSINFOEX_LEVEL1_W, which
    // holds LARGE_INTEGERs and so starts 8-byte aligned: SessionId at 8,
    // SessionState at 12 and SessionFlags at 16, on 32 and 64 bits alike.
    private const int LevelOffset = 0;
    private const int SessionFlagsOffset = 16;

    // WTS_SESSIONSTATE_LOCK and WTS_SESSIONSTATE_UNLOCK.  Windows 7 had
    // them the wrong way round, but .NET no longer runs there.
    private const int FlagLocked = 0;
    private const int FlagUnlocked = 1;

    /// <summary>
    /// True when locked, false when not, and null when Windows cannot
    /// tell (WTS_SESSIONSTATE_UNKNOWN, or a failed query).
    /// </summary>
    public static bool? IsLocked()
    {
        IntPtr buffer;
        int bytes;

        try
        {
            if (!WTSQuerySessionInformation(CurrentServer, CurrentSession, SessionInfoEx, out buffer, out bytes))
            {
                return null;
            }
        }
        catch (Exception exception) when (exception is DllNotFoundException or EntryPointNotFoundException)
        {
            // A Windows without Remote Desktop Services' API, if there
            // is one: no worse than before the state was read.
            return null;
        }

        if (buffer == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            if (bytes < SessionFlagsOffset + sizeof(int))
            {
                return null;
            }

            return FromSessionInfo(Marshal.ReadInt32(buffer, LevelOffset), Marshal.ReadInt32(buffer, SessionFlagsOffset));
        }
        finally
        {
            WTSFreeMemory(buffer);
        }
    }

    /// <summary>
    /// The lock state in a WTSINFOEXW's level and session flags.  Only
    /// level 1 is defined; anything else is not understood.
    /// </summary>
    public static bool? FromSessionInfo(int level, int sessionFlags)
    {
        if (level != 1)
        {
            return null;
        }

        return sessionFlags switch
        {
            FlagLocked => true,
            FlagUnlocked => false,
            _ => null
        };
    }

    [LibraryImport("Wtsapi32.dll", EntryPoint = "WTSQuerySessionInformationW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool WTSQuerySessionInformation(IntPtr server, int sessionId, int infoClass, out IntPtr buffer, out int bytesReturned);

    [LibraryImport("Wtsapi32.dll", EntryPoint = "WTSFreeMemory")]
    private static partial void WTSFreeMemory(IntPtr memory);
}
