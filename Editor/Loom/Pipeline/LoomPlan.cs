// LoomPlan.cs
//
// Pure data describing everything the build pipeline will write into the
// avatar's FX AnimatorController, VRCExpressionParameters, and
// VRCExpressionsMenu. The Planner builds a LoomPlan from a Discovery
// result; the Emitter consumes the Plan and performs the side-effecting
// mutations.
//
// Keeping the Plan side-effect-free is what lets the validator and the
// planner be tested with NUnit -- no Unity build hook, no clone-restore
// dance. Once the Plan is correct, the Emitter is a mechanical mapping
// from Plan entries to UnityEditor.Animations API calls.
//
// AnimationCurve binding paths are stored as raw strings keyed against
// the avatar root, matching what AnimationUtility.SetEditorCurve consumes.
// The Emitter turns each PlannedClipBinding into the matching
// EditorCurveBinding + AnimationCurve pair.

using System.Collections.Generic;
using UnityEngine;

namespace WhyKnot.AvatarQol.Loom.Pipeline {

    internal sealed class LoomPlan {
        public List<PlannedParameter> Parameters { get; } = new List<PlannedParameter>();
        public List<PlannedLayer>     Layers     { get; } = new List<PlannedLayer>();
        public List<PlannedMenuItem>  MenuItems  { get; } = new List<PlannedMenuItem>();
    }

    internal sealed class PlannedParameter {
        public string Name;
        public PlannedParameterType Type;
        public float DefaultValue;
        public bool NetworkSynced;
        public bool PersistAcrossSessions;
    }

    internal enum PlannedParameterType { Bool, Float, Int }

    internal sealed class PlannedLayer {
        public string Name;
        public List<PlannedState>      States      { get; } = new List<PlannedState>();
        public List<PlannedTransition> Transitions { get; } = new List<PlannedTransition>();
        public string DefaultStateName;
    }

    internal sealed class PlannedState {
        public string Name;
        public List<PlannedClipBinding> Bindings { get; } = new List<PlannedClipBinding>();
    }

    /// <summary>
    /// One curve to write into the state's AnimationClip. <see cref="RelativePath"/>
    /// is the path from the avatar root to the target GameObject;
    /// <see cref="BindingType"/> + <see cref="PropertyName"/> name the
    /// property; <see cref="ConstantValue"/> is held flat across the clip
    /// (single-keyframe step curve).
    /// </summary>
    internal sealed class PlannedClipBinding {
        public string RelativePath;
        public System.Type BindingType;
        public string PropertyName;
        public float ConstantValue;
    }

    internal sealed class PlannedTransition {
        public string FromState;
        public string ToState;
        public string ConditionParameter;
        public PlannedTransitionMode Mode;
    }

    /// <summary>
    /// Match the small subset of AnimatorConditionMode we actually emit at
    /// M1. Greater / Less / NotEqual arrive when sliders and int Threads
    /// ship in M2.
    /// </summary>
    internal enum PlannedTransitionMode { If, IfNot }

    internal sealed class PlannedMenuItem {
        /// <summary>
        /// Forward-slash separated menu path including the leaf item.
        /// "Outfit/Hat" creates submenu "Outfit" with a Toggle "Hat".
        /// </summary>
        public string Path;
        public PlannedMenuItemType Type;
        public string ParameterName;
        public float Value;
        public Texture2D Icon;
    }

    internal enum PlannedMenuItemType { Toggle }
}
