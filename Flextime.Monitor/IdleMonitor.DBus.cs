// Generated by "dotnet dbus codegen --service org.gnome.Mutter.IdleMonitor"
// on a Fedora 39 Workstation system.

using System.Runtime.CompilerServices;
using Tmds.DBus;

#pragma warning disable CS8625 // Cannot convert null literal to non-nullable reference type.

[assembly: InternalsVisibleTo(Tmds.DBus.Connection.DynamicAssemblyName)]
namespace Inhill.Flextime.Monitor
{
    [DBusInterface("org.gtk.Notifications")]
    interface INotifications : IDBusObject
    {
        Task AddNotificationAsync(string AppId, string Id, IDictionary<string, object> Notification);
        Task RemoveNotificationAsync(string AppId, string Id);
    }

    [DBusInterface("org.Gtk.MountOperationHandler")]
    interface IMountOperationHandler : IDBusObject
    {
        Task<(uint response, IDictionary<string, object> responseDetails)> AskPasswordAsync(string ObjectId, string Message, string IconName, string DefaultUser, string DefaultDomain, uint Flags);
        Task<(uint response, IDictionary<string, object> responseDetails)> AskQuestionAsync(string ObjectId, string Message, string IconName, string[] Choices);
        Task<(uint response, IDictionary<string, object> responseDetails)> ShowProcessesAsync(string ObjectId, string Message, string IconName, int[] ApplicationPids, string[] Choices);
        Task CloseAsync();
    }

    [DBusInterface("org.gnome.Sysprof3.Profiler")]
    interface IProfiler : IDBusObject
    {
        Task StartAsync(IDictionary<string, object> Options, CloseSafeHandle Fd);
        Task StopAsync();
        Task<T> GetAsync<T>(string prop);
        Task<ProfilerProperties> GetAllAsync();
        Task SetAsync(string prop, object val);
        Task<IDisposable> WatchPropertiesAsync(Action<PropertyChanges> handler);
    }

    [Dictionary]
    class ProfilerProperties
    {
        private IDictionary<string, object> _Capabilities = default(IDictionary<string, object>);
        public IDictionary<string, object> Capabilities
        {
            get
            {
                return _Capabilities;
            }

            set
            {
                _Capabilities = (value);
            }
        }
    }

    static class ProfilerExtensions
    {
        public static Task<IDictionary<string, object>> GetCapabilitiesAsync(this IProfiler o) => o.GetAsync<IDictionary<string, object>>("Capabilities");
    }

    [DBusInterface("org.gnome.Shell")]
    interface IShell : IDBusObject
    {
        Task<(bool success, string result)> EvalAsync(string Script);
        Task FocusSearchAsync();
        Task ShowOSDAsync(IDictionary<string, object> Params);
        Task ShowMonitorLabelsAsync(IDictionary<string, object> Params);
        Task HideMonitorLabelsAsync();
        Task FocusAppAsync(string Id);
        Task ShowApplicationsAsync();
        Task<uint> GrabAcceleratorAsync(string Accelerator, uint ModeFlags, uint GrabFlags);
        Task<uint[]> GrabAcceleratorsAsync((string, uint, uint)[] Accelerators);
        Task<bool> UngrabAcceleratorAsync(uint Action);
        Task<bool> UngrabAcceleratorsAsync(uint[] Action);
        Task ScreenTransitionAsync();
        Task<IDisposable> WatchAcceleratorActivatedAsync(Action<(uint action, IDictionary<string, object> parameters)> handler, Action<Exception> onError = null);
        Task<T> GetAsync<T>(string prop);
        Task<ShellProperties> GetAllAsync();
        Task SetAsync(string prop, object val);
        Task<IDisposable> WatchPropertiesAsync(Action<PropertyChanges> handler);
    }

    [Dictionary]
    class ShellProperties
    {
        private string _Mode = default(string);
        public string Mode
        {
            get
            {
                return _Mode;
            }

            set
            {
                _Mode = (value);
            }
        }

        private bool _OverviewActive = default(bool);
        public bool OverviewActive
        {
            get
            {
                return _OverviewActive;
            }

            set
            {
                _OverviewActive = (value);
            }
        }

        private string _ShellVersion = default(string);
        public string ShellVersion
        {
            get
            {
                return _ShellVersion;
            }

            set
            {
                _ShellVersion = (value);
            }
        }
    }

    static class ShellExtensions
    {
        public static Task<string> GetModeAsync(this IShell o) => o.GetAsync<string>("Mode");
        public static Task<bool> GetOverviewActiveAsync(this IShell o) => o.GetAsync<bool>("OverviewActive");
        public static Task<string> GetShellVersionAsync(this IShell o) => o.GetAsync<string>("ShellVersion");
        public static Task SetOverviewActiveAsync(this IShell o, bool val) => o.SetAsync("OverviewActive", val);
    }

    [DBusInterface("org.gnome.Shell.Extensions")]
    interface IExtensions : IDBusObject
    {
        Task<IDictionary<string, IDictionary<string, object>>> ListExtensionsAsync();
        Task<IDictionary<string, object>> GetExtensionInfoAsync(string Uuid);
        Task<string[]> GetExtensionErrorsAsync(string Uuid);
        Task<string> InstallRemoteExtensionAsync(string Uuid);
        Task<bool> UninstallExtensionAsync(string Uuid);
        Task ReloadExtensionAsync(string Uuid);
        Task<bool> EnableExtensionAsync(string Uuid);
        Task<bool> DisableExtensionAsync(string Uuid);
        Task LaunchExtensionPrefsAsync(string Uuid);
        Task OpenExtensionPrefsAsync(string Uuid, string ParentWindow, IDictionary<string, object> Options);
        Task CheckForUpdatesAsync();
        Task<IDisposable> WatchExtensionStateChangedAsync(Action<(string uuid, IDictionary<string, object> state)> handler, Action<Exception> onError = null);
        Task<IDisposable> WatchExtensionStatusChangedAsync(Action<(string uuid, int state, string error)> handler, Action<Exception> onError = null);
        Task<T> GetAsync<T>(string prop);
        Task<ExtensionsProperties> GetAllAsync();
        Task SetAsync(string prop, object val);
        Task<IDisposable> WatchPropertiesAsync(Action<PropertyChanges> handler);
    }

    [Dictionary]
    class ExtensionsProperties
    {
        private string _ShellVersion = default(string);
        public string ShellVersion
        {
            get
            {
                return _ShellVersion;
            }

            set
            {
                _ShellVersion = (value);
            }
        }

        private bool _UserExtensionsEnabled = default(bool);
        public bool UserExtensionsEnabled
        {
            get
            {
                return _UserExtensionsEnabled;
            }

            set
            {
                _UserExtensionsEnabled = (value);
            }
        }
    }

    static class ExtensionsExtensions
    {
        public static Task<string> GetShellVersionAsync(this IExtensions o) => o.GetAsync<string>("ShellVersion");
        public static Task<bool> GetUserExtensionsEnabledAsync(this IExtensions o) => o.GetAsync<bool>("UserExtensionsEnabled");
        public static Task SetUserExtensionsEnabledAsync(this IExtensions o, bool val) => o.SetAsync("UserExtensionsEnabled", val);
    }

    [DBusInterface("org.gnome.Shell.AudioDeviceSelection")]
    interface IAudioDeviceSelection : IDBusObject
    {
        Task OpenAsync(string[] Devices);
        Task CloseAsync();
        Task<IDisposable> WatchDeviceSelectedAsync(Action<string> handler, Action<Exception> onError = null);
    }

    [DBusInterface("org.gnome.Shell.Wacom.PadOsd")]
    interface IPadOsd : IDBusObject
    {
        Task ShowAsync(ObjectPath DeviceNode, bool EditionMode);
    }

    [DBusInterface("org.gnome.Shell.Introspect")]
    interface IIntrospect : IDBusObject
    {
        Task<IDictionary<string, IDictionary<string, object>>> GetRunningApplicationsAsync();
        Task<IDictionary<ulong, IDictionary<string, object>>> GetWindowsAsync();
        Task<IDisposable> WatchRunningApplicationsChangedAsync(Action handler, Action<Exception> onError = null);
        Task<IDisposable> WatchWindowsChangedAsync(Action handler, Action<Exception> onError = null);
        Task<T> GetAsync<T>(string prop);
        Task<IntrospectProperties> GetAllAsync();
        Task SetAsync(string prop, object val);
        Task<IDisposable> WatchPropertiesAsync(Action<PropertyChanges> handler);
    }

    [Dictionary]
    class IntrospectProperties
    {
        private bool _AnimationsEnabled = default(bool);
        public bool AnimationsEnabled
        {
            get
            {
                return _AnimationsEnabled;
            }

            set
            {
                _AnimationsEnabled = (value);
            }
        }

        private (int, int) _ScreenSize = default((int, int));
        public (int, int) ScreenSize
        {
            get
            {
                return _ScreenSize;
            }

            set
            {
                _ScreenSize = (value);
            }
        }

        private uint _version = default(uint);
        public uint Version
        {
            get
            {
                return _version;
            }

            set
            {
                _version = (value);
            }
        }
    }

    static class IntrospectExtensions
    {
        public static Task<bool> GetAnimationsEnabledAsync(this IIntrospect o) => o.GetAsync<bool>("AnimationsEnabled");
        public static Task<(int, int)> GetScreenSizeAsync(this IIntrospect o) => o.GetAsync<(int, int)>("ScreenSize");
        public static Task<uint> GetVersionAsync(this IIntrospect o) => o.GetAsync<uint>("version");
    }

    [DBusInterface("org.gnome.Shell.Screenshot")]
    interface IScreenshot : IDBusObject
    {
        Task<(bool success, string filenameUsed)> ScreenshotAsync(bool IncludeCursor, bool Flash, string Filename);
        Task<(bool success, string filenameUsed)> ScreenshotWindowAsync(bool IncludeFrame, bool IncludeCursor, bool Flash, string Filename);
        Task<(bool success, string filenameUsed)> ScreenshotAreaAsync(int X, int Y, int Width, int Height, bool Flash, string Filename);
        Task<IDictionary<string, object>> PickColorAsync();
        Task FlashAreaAsync(int X, int Y, int Width, int Height);
        Task<(int x, int y, int width, int height)> SelectAreaAsync();
    }

    [DBusInterface("org.gnome.Mutter.ServiceChannel")]
    interface IServiceChannel : IDBusObject
    {
        Task<CloseSafeHandle> OpenWaylandServiceConnectionAsync(uint ServiceClientType);
    }

    [DBusInterface("org.gnome.Mutter.DisplayConfig")]
    interface IDisplayConfig : IDBusObject
    {
        Task<(uint serial, (uint, long, int, int, int, int, int, uint, uint[], IDictionary<string, object>)[] crtcs, (uint, long, int, uint[], string, uint[], uint[], IDictionary<string, object>)[] outputs, (uint, long, uint, uint, double, uint)[] modes, int maxScreenWidth, int maxScreenHeight)> GetResourcesAsync();
        Task ApplyConfigurationAsync(uint Serial, bool Persistent, (uint, int, int, int, uint, uint[], IDictionary<string, object>)[] Crtcs, (uint, IDictionary<string, object>)[] Outputs);
        Task<int> ChangeBacklightAsync(uint Serial, uint Output, int Value);
        Task<(ushort[] red, ushort[] green, ushort[] blue)> GetCrtcGammaAsync(uint Serial, uint Crtc);
        Task SetCrtcGammaAsync(uint Serial, uint Crtc, ushort[] Red, ushort[] Green, ushort[] Blue);
        Task<(uint serial, ((string, string, string, string), (string, int, int, double, double, double[], IDictionary<string, object>)[], IDictionary<string, object>)[] monitors, (int, int, double, uint, bool, (string, string, string, string)[], IDictionary<string, object>)[] logicalMonitors, IDictionary<string, object> properties)> GetCurrentStateAsync();
        Task ApplyMonitorsConfigAsync(uint Serial, uint Method, (int, int, double, uint, bool, (string, string, IDictionary<string, object>)[])[] LogicalMonitors, IDictionary<string, object> Properties);
        Task SetOutputCTMAsync(uint Serial, uint Output, (ulong, ulong, ulong, ulong, ulong, ulong, ulong, ulong, ulong) Ctm);
        Task<IDisposable> WatchMonitorsChangedAsync(Action handler, Action<Exception> onError = null);
        Task<T> GetAsync<T>(string prop);
        Task<DisplayConfigProperties> GetAllAsync();
        Task SetAsync(string prop, object val);
        Task<IDisposable> WatchPropertiesAsync(Action<PropertyChanges> handler);
    }

    [Dictionary]
    class DisplayConfigProperties
    {
        private int _PowerSaveMode = default(int);
        public int PowerSaveMode
        {
            get
            {
                return _PowerSaveMode;
            }

            set
            {
                _PowerSaveMode = (value);
            }
        }

        private bool _PanelOrientationManaged = default(bool);
        public bool PanelOrientationManaged
        {
            get
            {
                return _PanelOrientationManaged;
            }

            set
            {
                _PanelOrientationManaged = (value);
            }
        }

        private bool _ApplyMonitorsConfigAllowed = default(bool);
        public bool ApplyMonitorsConfigAllowed
        {
            get
            {
                return _ApplyMonitorsConfigAllowed;
            }

            set
            {
                _ApplyMonitorsConfigAllowed = (value);
            }
        }

        private bool _NightLightSupported = default(bool);
        public bool NightLightSupported
        {
            get
            {
                return _NightLightSupported;
            }

            set
            {
                _NightLightSupported = (value);
            }
        }
    }

    static class DisplayConfigExtensions
    {
        public static Task<int> GetPowerSaveModeAsync(this IDisplayConfig o) => o.GetAsync<int>("PowerSaveMode");
        public static Task<bool> GetPanelOrientationManagedAsync(this IDisplayConfig o) => o.GetAsync<bool>("PanelOrientationManaged");
        public static Task<bool> GetApplyMonitorsConfigAllowedAsync(this IDisplayConfig o) => o.GetAsync<bool>("ApplyMonitorsConfigAllowed");
        public static Task<bool> GetNightLightSupportedAsync(this IDisplayConfig o) => o.GetAsync<bool>("NightLightSupported");
        public static Task SetPowerSaveModeAsync(this IDisplayConfig o, int val) => o.SetAsync("PowerSaveMode", val);
    }

    [DBusInterface("org.gnome.Mutter.InputCapture")]
    interface IInputCapture : IDBusObject
    {
        Task<ObjectPath> CreateSessionAsync(uint Capabilities);
        Task<T> GetAsync<T>(string prop);
        Task<InputCaptureProperties> GetAllAsync();
        Task SetAsync(string prop, object val);
        Task<IDisposable> WatchPropertiesAsync(Action<PropertyChanges> handler);
    }

    [Dictionary]
    class InputCaptureProperties
    {
        private uint _SupportedCapabilities = default(uint);
        public uint SupportedCapabilities
        {
            get
            {
                return _SupportedCapabilities;
            }

            set
            {
                _SupportedCapabilities = (value);
            }
        }
    }

    static class InputCaptureExtensions
    {
        public static Task<uint> GetSupportedCapabilitiesAsync(this IInputCapture o) => o.GetAsync<uint>("SupportedCapabilities");
    }

    [DBusInterface("org.gnome.Mutter.RemoteDesktop")]
    interface IRemoteDesktop : IDBusObject
    {
        Task<ObjectPath> CreateSessionAsync();
        Task<T> GetAsync<T>(string prop);
        Task<RemoteDesktopProperties> GetAllAsync();
        Task SetAsync(string prop, object val);
        Task<IDisposable> WatchPropertiesAsync(Action<PropertyChanges> handler);
    }

    [Dictionary]
    class RemoteDesktopProperties
    {
        private uint _SupportedDeviceTypes = default(uint);
        public uint SupportedDeviceTypes
        {
            get
            {
                return _SupportedDeviceTypes;
            }

            set
            {
                _SupportedDeviceTypes = (value);
            }
        }

        private int _Version = default(int);
        public int Version
        {
            get
            {
                return _Version;
            }

            set
            {
                _Version = (value);
            }
        }
    }

    static class RemoteDesktopExtensions
    {
        public static Task<uint> GetSupportedDeviceTypesAsync(this IRemoteDesktop o) => o.GetAsync<uint>("SupportedDeviceTypes");
        public static Task<int> GetVersionAsync(this IRemoteDesktop o) => o.GetAsync<int>("Version");
    }

    [DBusInterface("org.gnome.Mutter.ScreenCast")]
    interface IScreenCast : IDBusObject
    {
        Task<ObjectPath> CreateSessionAsync(IDictionary<string, object> Properties);
        Task<T> GetAsync<T>(string prop);
        Task<ScreenCastProperties> GetAllAsync();
        Task SetAsync(string prop, object val);
        Task<IDisposable> WatchPropertiesAsync(Action<PropertyChanges> handler);
    }

    [Dictionary]
    class ScreenCastProperties
    {
        private int _Version = default(int);
        public int Version
        {
            get
            {
                return _Version;
            }

            set
            {
                _Version = (value);
            }
        }
    }

    static class ScreenCastExtensions
    {
        public static Task<int> GetVersionAsync(this IScreenCast o) => o.GetAsync<int>("Version");
    }

    [DBusInterface("org.freedesktop.DBus.ObjectManager")]
    interface IObjectManager : IDBusObject
    {
        Task<IDictionary<ObjectPath, IDictionary<string, IDictionary<string, object>>>> GetManagedObjectsAsync();
        Task<IDisposable> WatchInterfacesAddedAsync(Action<(ObjectPath objectPath, IDictionary<string, IDictionary<string, object>> interfacesAndProperties)> handler, Action<Exception> onError = null);
        Task<IDisposable> WatchInterfacesRemovedAsync(Action<(ObjectPath objectPath, string[] interfaces)> handler, Action<Exception> onError = null);
    }

    [DBusInterface("org.gnome.Mutter.IdleMonitor")]
    interface IIdleMonitor : IDBusObject
    {
        Task<ulong> GetIdletimeAsync();
        Task<uint> AddIdleWatchAsync(ulong Interval);
        Task<uint> AddUserActiveWatchAsync();
        Task RemoveWatchAsync(uint Id);
        Task ResetIdletimeAsync();
        Task<IDisposable> WatchWatchFiredAsync(Action<uint> handler, Action<Exception> onError = null);
    }

    [DBusInterface("org.gnome.Mutter.InputMapping")]
    interface IInputMapping : IDBusObject
    {
        Task<(int rect, int, int, int)> GetDeviceMappingAsync(ObjectPath DeviceNode);
    }

    [DBusInterface("org.gnome.ScreenSaver")]
    interface IScreenSaver : IDBusObject
    {
        Task LockAsync();
        Task<bool> GetActiveAsync();
        Task SetActiveAsync(bool Value);
        Task<uint> GetActiveTimeAsync();
        Task<IDisposable> WatchActiveChangedAsync(Action<bool> handler, Action<Exception> onError = null);
        Task<IDisposable> WatchWakeUpScreenAsync(Action handler, Action<Exception> onError = null);
    }

    [DBusInterface("org.gnome.SessionManager.EndSessionDialog")]
    interface IEndSessionDialog : IDBusObject
    {
        Task OpenAsync(uint Arg0, uint Arg1, uint Arg2, ObjectPath[] Arg3);
        Task CloseAsync();
        Task<IDisposable> WatchConfirmedLogoutAsync(Action handler, Action<Exception> onError = null);
        Task<IDisposable> WatchConfirmedRebootAsync(Action handler, Action<Exception> onError = null);
        Task<IDisposable> WatchConfirmedShutdownAsync(Action handler, Action<Exception> onError = null);
        Task<IDisposable> WatchCanceledAsync(Action handler, Action<Exception> onError = null);
        Task<IDisposable> WatchClosedAsync(Action handler, Action<Exception> onError = null);
    }

    [DBusInterface("org.gnome.keyring.internal.Prompter")]
    interface IPrompter : IDBusObject
    {
        Task BeginPromptingAsync(ObjectPath Callback);
        Task PerformPromptAsync(ObjectPath Callback, string Type, IDictionary<string, object> Properties, string Exchange);
        Task StopPromptingAsync(ObjectPath Callback);
    }

    [DBusInterface("org.freedesktop.impl.portal.Access")]
    interface IAccess : IDBusObject
    {
        Task<(uint response, IDictionary<string, object> results)> AccessDialogAsync(ObjectPath Handle, string AppId, string ParentWindow, string Title, string Subtitle, string Body, IDictionary<string, object> Options);
    }

    [DBusInterface("org.freedesktop.Notifications")]
    interface INotifications0 : IDBusObject
    {
        Task<uint> NotifyAsync(string Arg0, uint Arg1, string Arg2, string Arg3, string Arg4, string[] Arg5, IDictionary<string, object> Arg6, int Arg7);
        Task CloseNotificationAsync(uint Arg0);
        Task<string[]> GetCapabilitiesAsync();
        Task<(string arg0, string arg1, string arg2, string arg3)> GetServerInformationAsync();
        Task<IDisposable> WatchNotificationClosedAsync(Action<(uint arg0, uint arg1)> handler, Action<Exception> onError = null);
        Task<IDisposable> WatchActionInvokedAsync(Action<(uint arg0, string arg1)> handler, Action<Exception> onError = null);
    }
}