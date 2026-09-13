using System.Runtime.InteropServices;
using System.Security;
using System.Text;

namespace OverShell.App.Notifications;

/// <summary>
/// Windows toast notifications through the Windows Runtime, called directly over COM —
/// no CsWinRT projection, which would put a 30 MB projection assembly back into an
/// output that was trimmed to 2 MB (DESIGN.md §4). .NET 5+ has no built-in WinRT
/// marshalling (no <c>HString</c>, no <c>IInspectable</c> interface type), so HSTRINGs
/// are made and freed by hand and each interface is declared as IUnknown-based with the
/// three IInspectable slots spelled out first, in IDL order.
/// <para>
/// An unpackaged app needs an AppUserModelID the shell knows:
/// <c>HKCU\Software\Classes\AppUserModelId\&lt;id&gt;</c> with a <c>DisplayName</c> is
/// enough since Windows 10 1709 — no Start Menu shortcut. Clicking the toast opens its
/// <c>launch</c> URL through the protocol handler (<c>overshell://focus/…</c>), so no COM
/// activator is registered either: the second instance hands the URL to the first
/// (§12.11).
/// </para>
/// </summary>
internal static class NativeToast
{
    public const string AppUserModelId = "OverShell.Terminal";

    private const string ManagerClass = "Windows.UI.Notifications.ToastNotificationManager";
    private const string NotificationClass = "Windows.UI.Notifications.ToastNotification";
    private const string XmlDocumentClass = "Windows.Data.Xml.Dom.XmlDocument";

    private static readonly Lock Gate = new();
    private static bool _registered;

    /// <summary>Makes sure the shell knows our id. Idempotent; HKCU only.</summary>
    public static void EnsureRegistered(string displayName, string? iconPath)
    {
        lock (Gate)
        {
            if (_registered)
            {
                return;
            }

            using var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(@"Software\Classes\AppUserModelId\" + AppUserModelId);
            key.SetValue("DisplayName", displayName);
            if (iconPath is not null && System.IO.File.Exists(iconPath))
            {
                key.SetValue("IconUri", iconPath);
            }

            _registered = true;
        }
    }

    /// <summary>
    /// Shows a toast. <paramref name="launch"/> is opened on click (protocol activation);
    /// <paramref name="tag"/> and <paramref name="group"/> let a later toast replace this one.
    /// Returns null on success, else the failing step and HRESULT — the caller traces it.
    /// </summary>
    public static string? Show(string title, string message, string? attribution, string? launch, string? tag, string? group, bool silent)
    {
        var xml = new StringBuilder("<toast");
        if (launch is not null)
        {
            xml.Append(" activationType=\"protocol\" launch=\"").Append(Escape(launch)).Append('"');
        }

        xml.Append("><visual><binding template=\"ToastGeneric\">");
        xml.Append("<text>").Append(Escape(title)).Append("</text>");
        xml.Append("<text>").Append(Escape(message)).Append("</text>");
        if (!string.IsNullOrEmpty(attribution))
        {
            xml.Append("<text placement=\"attribution\">").Append(Escape(attribution)).Append("</text>");
        }

        xml.Append("</binding></visual>");
        if (silent)
        {
            xml.Append("<audio silent=\"true\"/>");
        }

        xml.Append("</toast>");

        try
        {
            // COM is already initialised on this thread by the runtime (WPF: STA; a pool
            // thread: MTA); RoInitialize then reports S_FALSE or "changed mode", both fine.
            var init = RoInitialize(RoInitType.MultiThreaded);
            if (init < 0 && init != RpcChangedMode)
            {
                return $"RoInitialize 0x{init:X8}";
            }

            var document = (IXmlDocumentIO)ActivateInstance(XmlDocumentClass);
            using (var hstring = new HString(xml.ToString()))
            {
                document.LoadXml(hstring.Handle);
            }

            var factory = (IToastNotificationFactory)GetActivationFactory(NotificationClass, typeof(IToastNotificationFactory).GUID);
            var notification = factory.CreateToastNotification((IXmlDocument)document);

            if (tag is not null && notification is IToastNotification2 tagged)
            {
                using (var hstring = new HString(Clip(tag, 64)))
                {
                    tagged.put_Tag(hstring.Handle);
                }

                if (group is not null)
                {
                    using var hstring = new HString(Clip(group, 64));
                    tagged.put_Group(hstring.Handle);
                }
            }

            var manager = (IToastNotificationManagerStatics)GetActivationFactory(ManagerClass, typeof(IToastNotificationManagerStatics).GUID);
            IToastNotifier notifier;
            using (var hstring = new HString(AppUserModelId))
            {
                notifier = manager.CreateToastNotifierWithId(hstring.Handle);
            }

            notifier.Show(notification);
            return null;
        }
        catch (COMException e)
        {
            return $"toast failed 0x{e.HResult:X8}: {e.Message}";
        }
        catch (Exception e) when (e is InvalidCastException or EntryPointNotFoundException or DllNotFoundException or SecurityException or MarshalDirectiveException)
        {
            return $"toast failed: {e.GetType().Name}: {e.Message}";
        }
    }

    private static string Escape(string s) => SecurityElement.Escape(s) ?? string.Empty;

    private static string Clip(string s, int max) => s.Length <= max ? s : s[..max];

    // ---------------------------------------------------------------- WinRT plumbing

    private const int RpcChangedMode = unchecked((int)0x80010106);

    private enum RoInitType
    {
        SingleThreaded = 0,
        MultiThreaded = 1,
    }

    [DllImport("combase.dll")]
    private static extern int RoInitialize(RoInitType initType);

    [DllImport("combase.dll")]
    private static extern int RoActivateInstance(IntPtr activatableClassId, out IntPtr instance);

    [DllImport("combase.dll")]
    private static extern int RoGetActivationFactory(IntPtr activatableClassId, ref Guid iid, out IntPtr factory);

    [DllImport("combase.dll", CharSet = CharSet.Unicode)]
    private static extern int WindowsCreateString([MarshalAs(UnmanagedType.LPWStr)] string source, int length, out IntPtr hstring);

    [DllImport("combase.dll")]
    private static extern int WindowsDeleteString(IntPtr hstring);

    /// <summary>An HSTRING for the duration of one call.</summary>
    private readonly struct HString : IDisposable
    {
        public HString(string value)
        {
            Marshal.ThrowExceptionForHR(WindowsCreateString(value, value.Length, out var handle));
            Handle = handle;
        }

        public IntPtr Handle { get; }

        public void Dispose() => WindowsDeleteString(Handle);
    }

    private static object ActivateInstance(string classId)
    {
        using var name = new HString(classId);
        Marshal.ThrowExceptionForHR(RoActivateInstance(name.Handle, out var pointer));
        try
        {
            return Marshal.GetObjectForIUnknown(pointer);
        }
        finally
        {
            Marshal.Release(pointer);
        }
    }

    private static object GetActivationFactory(string classId, Guid iid)
    {
        using var name = new HString(classId);
        Marshal.ThrowExceptionForHR(RoGetActivationFactory(name.Handle, ref iid, out var pointer));
        try
        {
            return Marshal.GetObjectForIUnknown(pointer);
        }
        finally
        {
            Marshal.Release(pointer);
        }
    }

    // Every WinRT interface starts with IInspectable's three methods after IUnknown's; they
    // are declared so the slots line up and are never called. Then the IDL order, exactly.

    [ComImport]
    [Guid("F7F3A506-1E87-42D6-BCFB-B8C809FA5494")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IXmlDocument
    {
        [PreserveSig] int GetIids(out int count, out IntPtr iids);
        [PreserveSig] int GetRuntimeClassName(out IntPtr name);
        [PreserveSig] int GetTrustLevel(out int level);
    }

    [ComImport]
    [Guid("6CD0E74E-EE65-4489-9EBF-CA43E87BA637")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IXmlDocumentIO
    {
        [PreserveSig] int GetIids(out int count, out IntPtr iids);
        [PreserveSig] int GetRuntimeClassName(out IntPtr name);
        [PreserveSig] int GetTrustLevel(out int level);

        void LoadXml(IntPtr xml);
    }

    [ComImport]
    [Guid("997E2675-059E-4E60-8B06-1760917C8B80")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IToastNotification
    {
        [PreserveSig] int GetIids(out int count, out IntPtr iids);
        [PreserveSig] int GetRuntimeClassName(out IntPtr name);
        [PreserveSig] int GetTrustLevel(out int level);
    }

    [ComImport]
    [Guid("04124B20-82C6-4229-B109-FD9ED4662B53")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IToastNotificationFactory
    {
        [PreserveSig] int GetIids(out int count, out IntPtr iids);
        [PreserveSig] int GetRuntimeClassName(out IntPtr name);
        [PreserveSig] int GetTrustLevel(out int level);

        IToastNotification CreateToastNotification(IXmlDocument content);
    }

    [ComImport]
    [Guid("9DFB9FD1-143A-490E-90BF-B9FBA7132DE7")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IToastNotification2
    {
        [PreserveSig] int GetIids(out int count, out IntPtr iids);
        [PreserveSig] int GetRuntimeClassName(out IntPtr name);
        [PreserveSig] int GetTrustLevel(out int level);

        void put_Tag(IntPtr value);
        IntPtr get_Tag();
        void put_Group(IntPtr value);
        IntPtr get_Group();
        void put_SuppressPopup([MarshalAs(UnmanagedType.U1)] bool value);
        [return: MarshalAs(UnmanagedType.U1)] bool get_SuppressPopup();
    }

    [ComImport]
    [Guid("50AC103F-D235-4598-BBEF-98FE4D1A3AD4")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IToastNotificationManagerStatics
    {
        [PreserveSig] int GetIids(out int count, out IntPtr iids);
        [PreserveSig] int GetRuntimeClassName(out IntPtr name);
        [PreserveSig] int GetTrustLevel(out int level);

        IToastNotifier CreateToastNotifier();
        IToastNotifier CreateToastNotifierWithId(IntPtr applicationId);
    }

    [ComImport]
    [Guid("75927B93-03F3-41EC-91D3-6E5BAC1B38E7")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IToastNotifier
    {
        [PreserveSig] int GetIids(out int count, out IntPtr iids);
        [PreserveSig] int GetRuntimeClassName(out IntPtr name);
        [PreserveSig] int GetTrustLevel(out int level);

        void Show(IToastNotification notification);
        void Hide(IToastNotification notification);
        int get_Setting();
    }
}
