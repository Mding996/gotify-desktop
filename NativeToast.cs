using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;
using System.Xml.Linq;
using Microsoft.Win32;

namespace GotifyDesktop;

// Direct Windows Runtime ABI interop keeps the application self-contained without extra NuGet packages.
// Protocol activation plus the Start-menu shortcut's stub CLSID supports retained Action Center toasts.
internal sealed class NativeToast
{
    internal const string AppId = "GotifyDesktop.Native";
    private static readonly Guid ActivatorId = new("b5bd560f-f4ad-4367-bfb0-749d120de35b");
    private readonly NotificationCopyActions _copyActions;
    internal Action? CopyActionUnavailable { get; set; }
    internal NativeToast(NotificationCopyActions? copyActions = null) => _copyActions = copyActions ?? new(NotificationCopyActions.DefaultDirectory);
    internal static void SetProcessIdentity() => Marshal.ThrowExceptionForHR(SetCurrentProcessExplicitAppUserModelID(AppId));
    internal void Register()
    {
        string exe = Environment.ProcessPath!;
        SetProcessIdentity();
        using (var protocol = Registry.CurrentUser.CreateSubKey(@"Software\Classes\gotify-desktop"))
        {
            protocol.SetValue("", "URL:Gotify Desktop message"); protocol.SetValue("URL Protocol", "");
            using var command = protocol.CreateSubKey(@"shell\open\command"); command.SetValue("", $"\"{exe}\" --uri \"%1\"");
        }
        string shortcut = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.StartMenu), "Programs", "Gotify Desktop.lnk");
        Directory.CreateDirectory(Path.GetDirectoryName(shortcut)!);
        object link = new ShellLink();
        try
        {
            var shell = (IShellLinkW)link;
            shell.SetPath(exe); shell.SetWorkingDirectory(Path.GetDirectoryName(exe)!); shell.SetDescription("Gotify Desktop 消息中心"); shell.SetIconLocation(exe, 0);
            var store = (IPropertyStore)link;
            var id = PropVariant.FromString(AppId);
            var clsid = PropVariant.FromGuid(ActivatorId);
            try
            {
                var key = new PropertyKey(new Guid("9f4c2855-9f79-4b39-a8d0-e1d42de1d5f3"), 5);
                store.SetValue(ref key, ref id);
                key = new PropertyKey(key.Format, 26); store.SetValue(ref key, ref clsid); store.Commit();
                ((IPersistFile)link).Save(shortcut, true);
            }
            finally { PropVariantClear(ref id); PropVariantClear(ref clsid); }
        }
        finally { Marshal.FinalReleaseComObject(link); }
    }
    internal static string Payload(GotifyMessage message, string? copyActionUri = null)
    {
        static string Clean(string value, int count) => string.Concat(value.EnumerateRunes().Where(r => r.Value is 9 or 10 or 13 || (r.Value >= 32 && r.Value != 65534 && r.Value != 65535)).Take(count).Select(r => r.ToString()));
        var binding = new XElement("binding", new XAttribute("template", "ToastGeneric"), new XElement("text", Clean(string.IsNullOrWhiteSpace(message.Title) ? "Gotify" : message.Title, 150)), new XElement("text", Clean(message.Message, 1000)), new XElement("text", Clean(message.AppName, 100)));
        if (Path.IsPathFullyQualified(message.AppIconPath) && File.Exists(message.AppIconPath)) binding.Add(new XElement("image", new XAttribute("placement", "appLogoOverride"), new XAttribute("src", new Uri(message.AppIconPath).AbsoluteUri)));
        var toast = new XElement("toast", new XAttribute("activationType", "protocol"), new XAttribute("launch", $"gotify-desktop://message?id={message.Id}"), new XElement("visual", binding));
        if (message.HasVerificationCode && NotificationCopyInvocation.TryKey(copyActionUri, out _))
            toast.Add(new XElement("actions", new XElement("action", new XAttribute("content", "复制验证码"), new XAttribute("activationType", "protocol"), new XAttribute("arguments", copyActionUri!))));
        else if (MessageActions.OriginalUrl(message) is Uri url) toast.Add(new XElement("actions", new XElement("action", new XAttribute("content", "打开原页面"), new XAttribute("activationType", "protocol"), new XAttribute("arguments", url.AbsoluteUri))));
        return toast.ToString(SaveOptions.DisableFormatting);
    }
    internal void Show(GotifyMessage message)
    {
        string? copyActionUri = null;
        try { copyActionUri = _copyActions.Create(message); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.Cryptography.CryptographicException)
        { CopyActionUnavailable?.Invoke(); }
        bool initialized = InitializeRuntime();
        IntPtr toast = IntPtr.Zero, manager = IntPtr.Zero, notifier = IntPtr.Zero;
        try
        {
            toast = CreateToast(Payload(message, copyActionUri));
            manager = Factory("Windows.UI.Notifications.ToastNotificationManager", new Guid("50ac103f-d235-4598-bbef-98fe4d1a3ad4"));
            using var id = new HString(AppId);
            Marshal.ThrowExceptionForHR(Method<FactoryCall>(manager, 7)(manager, id.Handle, out notifier));
            Marshal.ThrowExceptionForHR(Method<ObjectCall>(notifier, 6)(notifier, toast));
        }
        finally { Release(notifier); Release(manager); Release(toast); if (initialized) RoUninitialize(); }
    }
    internal static IDisposable ShowTemporary(string xml)
    {
        bool initialized = InitializeRuntime();
        IntPtr toast = IntPtr.Zero, manager = IntPtr.Zero, notifier = IntPtr.Zero;
        try
        {
            toast = CreateToast(xml); manager = Factory("Windows.UI.Notifications.ToastNotificationManager", new Guid("50ac103f-d235-4598-bbef-98fe4d1a3ad4"));
            using var id = new HString(AppId);
            Marshal.ThrowExceptionForHR(Method<FactoryCall>(manager, 7)(manager, id.Handle, out notifier));
            Marshal.ThrowExceptionForHR(Method<ObjectCall>(notifier, 6)(notifier, toast));
            return new TemporaryToast(notifier, toast, initialized);
        }
        catch { Release(notifier); Release(toast); if (initialized) RoUninitialize(); throw; }
        finally { Release(manager); }
    }
    private sealed class TemporaryToast(IntPtr notifier, IntPtr toast, bool initialized) : IDisposable
    {
        public void Dispose() { try { Method<ObjectCall>(notifier, 7)(notifier, toast); } finally { Release(notifier); Release(toast); if (initialized) RoUninitialize(); } }
    }
    internal static void ValidatePlatform(string xml) { bool initialized = InitializeRuntime(); try { var toast = CreateToast(xml); Release(toast); } finally { if (initialized) RoUninitialize(); } }
    private static bool InitializeRuntime() { int hr = RoInitialize(0); if (hr < 0 && hr != unchecked((int)0x80010106)) Marshal.ThrowExceptionForHR(hr); return hr >= 0; }
    private static IntPtr CreateToast(string xml)
    {
        IntPtr doc = IntPtr.Zero, io = IntPtr.Zero, factory = IntPtr.Zero, toast = IntPtr.Zero;
        try
        {
            using var classId = new HString("Windows.Data.Xml.Dom.XmlDocument");
            Marshal.ThrowExceptionForHR(RoActivateInstance(classId.Handle, out doc));
            var iid = new Guid("6cd0e74e-ee65-4489-9ebf-ca43e87ba637");
            Marshal.ThrowExceptionForHR(Marshal.QueryInterface(doc, ref iid, out io));
            using var content = new HString(xml);
            Marshal.ThrowExceptionForHR(Method<ObjectCall>(io, 6)(io, content.Handle));
            factory = Factory("Windows.UI.Notifications.ToastNotification", new Guid("04124b20-82c6-4229-b109-fd9ed4662b53"));
            Marshal.ThrowExceptionForHR(Method<FactoryCall>(factory, 6)(factory, doc, out toast));
            return toast;
        }
        catch { Release(toast); throw; }
        finally { Release(factory); Release(io); Release(doc); }
    }
    private static IntPtr Factory(string className, Guid iid)
    {
        using var name = new HString(className);
        Marshal.ThrowExceptionForHR(RoGetActivationFactory(name.Handle, ref iid, out var result)); return result;
    }
    private static T Method<T>(IntPtr instance, int slot) where T : Delegate => Marshal.GetDelegateForFunctionPointer<T>(Marshal.ReadIntPtr(Marshal.ReadIntPtr(instance), slot * IntPtr.Size));
    private static void Release(IntPtr value) { if (value != IntPtr.Zero) Marshal.Release(value); }
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int FactoryCall(IntPtr self, IntPtr arg, out IntPtr result);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int ObjectCall(IntPtr self, IntPtr arg);
    private sealed class HString : IDisposable
    {
        internal IntPtr Handle;
        internal HString(string value) { Marshal.ThrowExceptionForHR(WindowsCreateString(value, value.Length, out Handle)); }
        public void Dispose() => WindowsDeleteString(Handle);
    }
    [DllImport("combase.dll")] private static extern int RoInitialize(uint type);
    [DllImport("combase.dll")] private static extern void RoUninitialize();
    [DllImport("combase.dll")] private static extern int RoActivateInstance(IntPtr name, out IntPtr instance);
    [DllImport("combase.dll")] private static extern int RoGetActivationFactory(IntPtr name, ref Guid iid, out IntPtr factory);
    [DllImport("combase.dll", CharSet = CharSet.Unicode)] private static extern int WindowsCreateString(string value, int length, out IntPtr result);
    [DllImport("combase.dll")] private static extern int WindowsDeleteString(IntPtr value);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] private static extern int SetCurrentProcessExplicitAppUserModelID(string appId);
    [DllImport("ole32.dll")] private static extern int PropVariantClear(ref PropVariant value);
    [StructLayout(LayoutKind.Sequential)] private struct PropertyKey { public Guid Format; public uint Id; public PropertyKey(Guid format, uint id) { Format = format; Id = id; } }
    [StructLayout(LayoutKind.Explicit, Size = 24)] private struct PropVariant
    {
        [FieldOffset(0)] public ushort Type;
        [FieldOffset(8)] public IntPtr Pointer;
        public static PropVariant FromString(string value) => new() { Type = 31, Pointer = Marshal.StringToCoTaskMemUni(value) };
        public static PropVariant FromGuid(Guid value) { var result = new PropVariant { Type = 72, Pointer = Marshal.AllocCoTaskMem(16) }; Marshal.StructureToPtr(value, result.Pointer, false); return result; }
    }
    [ComImport, Guid("00021401-0000-0000-C000-000000000046")] private class ShellLink { }
    [ComImport, Guid("886d8eeb-8cf2-4446-8d02-cdba1dbdcf99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)] private interface IPropertyStore
    {
        void GetCount(out uint count); void GetAt(uint index, out PropertyKey key); void GetValue(ref PropertyKey key, out PropVariant value); void SetValue(ref PropertyKey key, ref PropVariant value); void Commit();
    }
    [ComImport, Guid("000214F9-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)] private interface IShellLinkW
    {
        void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder file, int count, IntPtr data, uint flags);
        void GetIDList(out IntPtr id); void SetIDList(IntPtr id);
        void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder text, int count); void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string text);
        void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder text, int count); void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string text);
        void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder text, int count); void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string text);
        void GetHotkey(out short key); void SetHotkey(short key); void GetShowCmd(out int command); void SetShowCmd(int command);
        void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder text, int count, out int index); void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string path, int index);
        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string path, uint reserved); void Resolve(IntPtr owner, uint flags); void SetPath([MarshalAs(UnmanagedType.LPWStr)] string path);
    }
}
