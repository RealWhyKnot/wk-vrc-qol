// LoomDiscovery.cs
//
// Walk an avatar root and collect every WkLoomThread (in M2: + Group +
// DerivedParam + Controller + RuleSheet). Result is a pure value object
// the Planner / Validator consume; Discovery does no validation itself
// beyond surfacing a missing-descriptor error, since the Validator's job
// is to centralize that.
//
// GetComponentsInChildren(true) includes inactive GameObjects so a Thread
// on a child that ships disabled-by-default still participates in the
// build. Authoring-component activation state is independent of the
// Thread's runtime on/off value.

using System.Collections.Generic;
using UnityEngine;
using VRC.SDK3.Avatars.Components;

namespace WhyKnot.AvatarQol.Loom.Pipeline {

    internal sealed class LoomDiscoveryResult {
        public GameObject AvatarRoot;
        public VRCAvatarDescriptor Descriptor;
        public List<WkLoomThread> Threads { get; } = new List<WkLoomThread>();
    }

    internal static class LoomDiscovery {

        public static LoomDiscoveryResult Discover(GameObject avatarRoot) {
            var result = new LoomDiscoveryResult { AvatarRoot = avatarRoot };
            if (avatarRoot == null) return result;

            result.Descriptor = avatarRoot.GetComponent<VRCAvatarDescriptor>();

            var threads = avatarRoot.GetComponentsInChildren<WkLoomThread>(true);
            foreach (var t in threads) {
                if (t != null) result.Threads.Add(t);
            }

            return result;
        }
    }
}
