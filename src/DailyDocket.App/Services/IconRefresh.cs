using System.Runtime.InteropServices;

namespace DailyDocket.App.Services;

/// <summary>Tells Windows to read app icons again, after an update changed DailyDocket's.</summary>
/// <remarks>
/// An update replaces the exe in place, under the same path, and the taskbar and Start menu keep
/// the icon they cached for that path: after 26.1.4's new icon, the title bar showed it and the
/// taskbar still showed the old one (November 2026). SHCNE_ASSOCCHANGED is the notice Windows
/// itself sends when icons change; it makes Explorer drop its cached icons.
/// </remarks>
internal static class IconRefresh
{
    private const int ShcneAssocChanged = 0x08000000;
    private const uint ShcnfIdList = 0x0000;

    public static void Request() => SHChangeNotify(ShcneAssocChanged, ShcnfIdList, IntPtr.Zero, IntPtr.Zero);

    // DllImport, not LibraryImport: the generated marshalling needs unsafe code turned on for the
    // whole project, for one call that passes nothing to marshal.
#pragma warning disable SYSLIB1054
    [DllImport("shell32.dll")]
    private static extern void SHChangeNotify(int eventId, uint flags, IntPtr item1, IntPtr item2);
#pragma warning restore SYSLIB1054
}
