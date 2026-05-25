// AvatarQolMenus.cs
//
// Wires the wk-core 1.2.0 log viewer and Project Settings page into
// per-downstream menu paths. WkLogViewerWindow and WkSettingsProvider
// both ship in the bundled Editor/Internal/ tree but deliberately
// register no menu / settings attribute of their own -- if they did,
// each downstream's synced copy would race for the same menu path.
// Doing the wiring here gives this package its own
// Window/WhyKnot/Avatar QoL/Logs menu item and its own
// WhyKnot/Avatar QoL Project Settings page.

using UnityEditor;
using WhyKnot.AvatarQol.Internal.Logging;
using WhyKnot.AvatarQol.Internal.Settings;

namespace WhyKnot.AvatarQol {

    internal static class AvatarQolMenus {

        [MenuItem("Window/WhyKnot/Avatar QoL/Logs")]
        public static void OpenLogViewer() => WkLogViewerWindow.Open();

        [SettingsProvider]
        public static SettingsProvider CreateSettings() => WkSettingsProvider.Build("WhyKnot/Avatar QoL");
    }
}
