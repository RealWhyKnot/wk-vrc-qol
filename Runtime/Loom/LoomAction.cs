// LoomAction.cs
//
// Abstract base for everything a Thread can apply when it goes on. Held
// in a [SerializeReference]-decorated list on WkLoomThread, so Unity's
// managed-reference serializer preserves the concrete subclass type across
// scene/prefab save/load.
//
// Why a class hierarchy instead of a discriminated-union struct: the
// authoring inspector binds SerializedProperty paths into the action's
// fields. Per-action-type SerializedProperty paths are stable as long as
// each subclass has its own field set. A struct with a "kind" enum plus
// every possible field would either bloat the serialized data or force
// the inspector to gate every field on the enum -- both worse than the
// class hierarchy at the cost of one [SerializeReference] attribute.
//
// All concrete subclasses MUST be [System.Serializable] AND have a public
// parameterless constructor; Unity's managed-reference serializer requires
// both to round-trip the instance. Subclasses live in their own files so
// future action types can land without touching this one.

namespace WhyKnot.AvatarQol.Loom {

    [System.Serializable]
    public abstract class LoomAction {
        // Reserved for fields that genuinely apply to every action kind.
        // Per-platform toggles (desktopActive / androidActive) and the
        // local-only / remote-only flags will land here in M2 once the
        // first action type that needs them ships.
    }
}
