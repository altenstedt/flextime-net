using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Flextime.Daemon;

/// <summary>
/// Reads configd's dynamic store on macOS — the same store `scutil`
/// shows — through the SystemConfiguration framework.  Each network
/// service with an IPv4 configuration has a dictionary under
/// <c>State:/Network/Service/&lt;id&gt;/IPv4</c>, and configd records there
/// the router it uses and that router's hardware address.  Reading it
/// needs no permission, unlike the neighbour cache.
/// </summary>
/// <remarks>
/// https://developer.apple.com/documentation/systemconfiguration/scdynamicstore
/// </remarks>
[SupportedOSPlatform("macos")]
internal static unsafe partial class SystemConfiguration
{
    private const string CoreFoundation = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";
    private const string SystemConfigurationFramework = "/System/Library/Frameworks/SystemConfiguration.framework/SystemConfiguration";

    private const uint Utf8Encoding = 0x08000100; // kCFStringEncodingUTF8

    public const string ServiceIPv4Pattern = "State:/Network/Service/[^/]+/IPv4";

    /// <summary>
    /// The string values of every service's IPv4 dictionary, keyed by
    /// name; arrays and other values are left out.  Null when the store
    /// cannot be opened.
    /// </summary>
    public static IReadOnlyList<IReadOnlyDictionary<string, string>>? ReadServiceIPv4()
    {
        var name = CreateString("flextimed");
        var pattern = CreateString(ServiceIPv4Pattern);
        var store = IntPtr.Zero;
        var keys = IntPtr.Zero;

        try
        {
            store = SCDynamicStoreCreate(IntPtr.Zero, name, IntPtr.Zero, IntPtr.Zero);

            if (store == IntPtr.Zero)
            {
                return null;
            }

            keys = SCDynamicStoreCopyKeyList(store, pattern);

            if (keys == IntPtr.Zero)
            {
                return [];
            }

            var services = new List<IReadOnlyDictionary<string, string>>();

            var count = CFArrayGetCount(keys);

            for (nint i = 0; i < count; i++)
            {
                // Owned by the array; not released here.
                var key = CFArrayGetValueAtIndex(keys, i);

                var value = SCDynamicStoreCopyValue(store, key);

                if (value == IntPtr.Zero)
                {
                    // The service went away between the two calls.
                    continue;
                }

                try
                {
                    if (CFGetTypeID(value) == CFDictionaryGetTypeID())
                    {
                        services.Add(ReadStrings(value));
                    }
                }
                finally
                {
                    CFRelease(value);
                }
            }

            return services;
        }
        finally
        {
            Release(keys);
            Release(store);
            Release(pattern);
            Release(name);
        }
    }

    private static Dictionary<string, string> ReadStrings(IntPtr dictionary)
    {
        var result = new Dictionary<string, string>();

        var count = CFDictionaryGetCount(dictionary);

        if (count <= 0)
        {
            return result;
        }

        var keys = new IntPtr[count];
        var values = new IntPtr[count];

        fixed (IntPtr* keysPointer = keys)
        fixed (IntPtr* valuesPointer = values)
        {
            CFDictionaryGetKeysAndValues(dictionary, keysPointer, valuesPointer);
        }

        var stringType = CFStringGetTypeID();

        for (var i = 0; i < count; i++)
        {
            if (CFGetTypeID(keys[i]) == stringType && CFGetTypeID(values[i]) == stringType)
            {
                result[ToString(keys[i])] = ToString(values[i]);
            }
        }

        return result;
    }

    private static string ToString(IntPtr text)
    {
        var length = CFStringGetLength(text);

        if (length == 0)
        {
            return string.Empty;
        }

        var buffer = new char[length];

        fixed (char* pointer = buffer)
        {
            CFStringGetCharacters(text, new CFRange(0, length), pointer);
        }

        return new string(buffer);
    }

    private static IntPtr CreateString(string text) => CFStringCreateWithCString(IntPtr.Zero, text, Utf8Encoding);

    private static void Release(IntPtr reference)
    {
        if (reference != IntPtr.Zero)
        {
            CFRelease(reference);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct CFRange(nint location, nint length)
    {
        public readonly nint Location = location;
        public readonly nint Length = length;
    }

    [LibraryImport(SystemConfigurationFramework)]
    private static partial IntPtr SCDynamicStoreCreate(IntPtr allocator, IntPtr name, IntPtr callout, IntPtr context);

    [LibraryImport(SystemConfigurationFramework)]
    private static partial IntPtr SCDynamicStoreCopyKeyList(IntPtr store, IntPtr pattern);

    [LibraryImport(SystemConfigurationFramework)]
    private static partial IntPtr SCDynamicStoreCopyValue(IntPtr store, IntPtr key);

    [LibraryImport(CoreFoundation, StringMarshalling = StringMarshalling.Utf8)]
    private static partial IntPtr CFStringCreateWithCString(IntPtr allocator, string text, uint encoding);

    [LibraryImport(CoreFoundation)]
    private static partial nint CFStringGetLength(IntPtr text);

    [LibraryImport(CoreFoundation)]
    private static partial void CFStringGetCharacters(IntPtr text, CFRange range, char* buffer);

    [LibraryImport(CoreFoundation)]
    private static partial nuint CFGetTypeID(IntPtr reference);

    [LibraryImport(CoreFoundation)]
    private static partial nuint CFStringGetTypeID();

    [LibraryImport(CoreFoundation)]
    private static partial nuint CFDictionaryGetTypeID();

    [LibraryImport(CoreFoundation)]
    private static partial nint CFDictionaryGetCount(IntPtr dictionary);

    [LibraryImport(CoreFoundation)]
    private static partial void CFDictionaryGetKeysAndValues(IntPtr dictionary, IntPtr* keys, IntPtr* values);

    [LibraryImport(CoreFoundation)]
    private static partial nint CFArrayGetCount(IntPtr array);

    [LibraryImport(CoreFoundation)]
    private static partial IntPtr CFArrayGetValueAtIndex(IntPtr array, nint index);

    [LibraryImport(CoreFoundation)]
    private static partial void CFRelease(IntPtr reference);
}
