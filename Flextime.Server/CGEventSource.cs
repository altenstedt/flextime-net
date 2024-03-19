using System.Runtime.InteropServices;

namespace Inhill.Flextime.Server;

/// <summary>
/// Defines an opaque type that represents the source of a Quartz event on macOS
/// </summary>
/// <remarks>
/// https://developer.apple.com/documentation/coregraphics/cgeventsource
/// </remarks>
internal static partial class CGEventSource
{
    private const string CoreGraphics = "/System/Library/Frameworks/ApplicationServices.framework/Frameworks/CoreGraphics.framework/CoreGraphics";

    /// <summary>
    /// Constants that specify the possible source states of an event source.
    /// </summary>
    /// <remarks>
    /// https://developer.apple.com/documentation/coregraphics/cgeventsourcestateid
    /// </remarks>
    internal enum CGEventSourceStateID : int
    {
        /// <summary>
        /// Specifies that an event source should use the event state table that
        /// reflects the combined state of all event sources posting to the
        /// current user login session.
        /// </summary>
        CombinedSessionState = 0,

        /// <summary>
        /// Specifies that an event source should use the event state table that
        /// reflects the combined state of all hardware event sources posting from
        /// the HID system.
        /// </summary>
        HidSystemState = 1
    }

    /// <summary>
    /// Constants that specify the different types of input events.
    /// </summary>
    /// <remarks>
    /// https://developer.apple.com/documentation/coregraphics/cgeventtype
    /// </remarks>
    internal enum CGEventType : uint
    {
        // All events omitted for brevity.
        // https://developer.apple.com/documentation/coregraphics/cgeventtype

        // We do not want the tapDisabled* events.
        MouseAndKeyboard = 0xFF
    }

    /// <summary>
    /// Returns the elapsed time since the last event for a Quartz event source.
    /// </summary>
    /// <returns>
    /// The time in seconds since the previous input event of the specified type.
    /// </returns>
    /// <remarks>
    /// https://developer.apple.com/documentation/coregraphics/cgeventsource/1408790-secondssincelasteventtype
    /// </remarks>
    [LibraryImport(CoreGraphics, EntryPoint = "CGEventSourceSecondsSinceLastEventType")]
    internal static partial double SecondsSinceLastEventType(CGEventSourceStateID stateId, CGEventType eventType);
}