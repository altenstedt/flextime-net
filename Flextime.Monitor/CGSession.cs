using System.Runtime.InteropServices;

namespace Flextime.Monitor;

internal static partial class CGSession
{
    [LibraryImport("/System/Library/Frameworks/ApplicationServices.framework/Frameworks/CoreGraphics.framework/CoreGraphics", EntryPoint = "CGSessionCopyCurrentDictionary")]
    internal static partial IntPtr CGSessionCopyCurrentDictionary();
    
    [LibraryImport("/System/Library/Frameworks/Foundation.framework/Foundation", EntryPoint = "CFDictionaryGetValue")]
    internal static partial IntPtr CFDictionaryGetValue(IntPtr windowListRef, IntPtr key);
    
    [LibraryImport("/System/Library/Frameworks/Foundation.framework/Foundation", EntryPoint = "CFStringCreateWithCharacters")]
    internal static partial IntPtr CFStringCreateWithCharacters(IntPtr allocator, IntPtr str, nint count);
}

