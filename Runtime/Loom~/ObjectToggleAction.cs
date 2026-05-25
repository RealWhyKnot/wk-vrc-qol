// ObjectToggleAction.cs
//
// Set a GameObject's active flag based on the Thread's state. The most
// common avatar-toggle primitive -- "wear this hat", "hide these shoes",
// "show the magic effect mesh". Everything else (BlendShapeAction,
// MaterialAction, ScaleAction, ...) ships in M2.
//
// Mode semantics:
//   TurnOn  -- target.activeSelf == thread.on
//   TurnOff -- target.activeSelf == !thread.on    (inverted)
//   Toggle  -- target.activeSelf flips on every transition; the build
//              pipeline lowers this to two states whose clips set the
//              opposite values, so the Animator does the flipping with
//              no per-frame work.
//
// The reference is a hard GameObject reference rather than a transform
// path. Path-based refs survive renames but break on hierarchy moves;
// hard refs break on renames AND moves but the inspector surfaces the
// missing target immediately, which matches VRCFury's behaviour and the
// validator can catch it at build time (one of the footguns the design
// brief calls out -- "(NO VALID ANIMATIONS)" layers on Ume today).

using UnityEngine;

namespace WhyKnot.AvatarQol.Loom {

    [System.Serializable]
    public sealed class ObjectToggleAction : LoomAction {
        public GameObject target;
        public ObjectToggleMode mode = ObjectToggleMode.TurnOn;
    }
}
