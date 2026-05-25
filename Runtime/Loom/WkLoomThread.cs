// WkLoomThread.cs
//
// One Thread: a single menu-driven switchable item on an avatar. At build
// time the pipeline lowers each Thread to (1) one entry in the avatar's
// VRCExpressionParameters, (2) one menu item under VRCExpressionsMenu,
// (3) one layer in the FX AnimatorController with Off/On states.
//
// Authored as an IEditorOnly MonoBehaviour. The component is stripped at
// upload by the SDK; only the generated animator/param/menu artifacts
// ship in the uploaded avatar.
//
// Field-design notes ("why this is not VRCFury's Toggle field-for-field"):
//
//   persistAcrossSessions defaults to true. 81 of 86 Toggles on the Ume
//   avatar set VRCFury's `saved` flag, so we flip the default rather than
//   ask the user to opt in every time.
//
//   No useGlobalParam bool. Presence of a non-empty explicitParamName is
//   the gate. VRCFury's two-field useGlobalParam + globalParam shape has
//   produced the "param silently resets on regenerate" failure mode that
//   the vrcfury-qol AutoGlobalParameterTool exists to paper over; making
//   the name field self-gating eliminates the desync.
//
//   No enable<X> partner bool for icon, exclusiveTag, etc. A null icon
//   means no icon; an empty group reference means no group. Mirroring
//   VRCFury's enable<X> + <X> pairs would just import its footguns
//   (see "Shoes" in the design brief: tag filled, enable bool off,
//   silently broken).
//
// Cross-Thread rules (Reactions, Groups) ship in M2. M1 only covers the
// authoring shape needed to prove the build pipeline end-to-end: name,
// param shape, default state, and an actions list.

using System.Collections.Generic;
using UnityEngine;
using VRC.SDKBase;

namespace WhyKnot.AvatarQol.Loom {

    [AddComponentMenu("WhyKnot/Loom/Thread")]
    [DisallowMultipleComponent]
    public sealed class WkLoomThread : MonoBehaviour, IEditorOnly {

        [Tooltip("Menu path for this Thread. Forward slashes create submenu folders. " +
                 "Also used as the FX AnimatorController layer name and, when " +
                 "explicitParamName is empty, the synced parameter name.")]
        public string menuPath = "";

        [Tooltip("Shape of the synced parameter. Bool toggles between off and on. " +
                 "Float and Int are reserved for the slider and n-state radio in M2.")]
        public ThreadKind kind = ThreadKind.Bool;

        [Tooltip("Initial value when no saved state exists. For Bool, true means the " +
                 "Thread starts on.")]
        public bool defaultOn = false;

        [Tooltip("Default for Float / Int Threads. Ignored for Bool.")]
        public float defaultValue = 0f;

        [Tooltip("If true, the avatar's saved-parameter slot remembers the Thread's " +
                 "value across sessions. Defaults to true because virtually every " +
                 "real toggle wants this.")]
        public bool persistAcrossSessions = true;

        [Tooltip("If true, the parameter is network-synced so remote viewers see the " +
                 "same state as the wearer. Sliders typically leave this off to keep " +
                 "the synced-parameter budget free.")]
        public bool networkSynced = true;

        [Tooltip("Optional explicit parameter name. When blank, the parameter name is " +
                 "derived from menuPath. Setting a name keeps it stable across rebuilds " +
                 "so OSC mappings and external apps don't drift.")]
        public string explicitParamName = "";

        [Tooltip("Optional menu icon. Null means no icon -- there is no separate enable " +
                 "flag, the presence of an icon is the gate.")]
        public Texture2D icon;

        [Tooltip("Actions applied while the Thread is on. At M1 only ObjectToggleAction " +
                 "is implemented; the remaining action types arrive in M2.")]
        [SerializeReference]
        public List<LoomAction> actions = new List<LoomAction>();

        /// <summary>
        /// The parameter name this Thread will compile to. Returns
        /// <see cref="explicitParamName"/> when set, otherwise
        /// <see cref="menuPath"/>. Returns an empty string when both are
        /// empty (the validator flags this as an error at build time).
        /// </summary>
        public string ResolvedParameterName =>
            !string.IsNullOrEmpty(explicitParamName) ? explicitParamName : menuPath;
    }
}
