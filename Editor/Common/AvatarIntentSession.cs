// AvatarIntentSession.cs
//
// Per-pipeline-run lifetime for the in-memory mutations an intent runner
// makes against an avatar. The session captures every original it touches
// so Dispose deterministically unwinds the run: source mesh references
// go back on every renderer, generated meshes are destroyed, components
// spawned during the run are removed in reverse order.
//
// Four categories of tracked state:
//   1. Renderer originals: Capture(renderer) snapshots sharedMesh +
//      every blendshape weight so a later RestoreOnDispose puts the
//      renderer back exactly the way it was before the run touched it.
//      Repeated Capture calls for the same renderer are no-ops -- first
//      capture wins so multiple ops on the same renderer share one
//      original.
//   2. Component originals: Capture(component) snapshots serialized fields
//      so temporary play/build changes can be restored after the run.
//   3. Generated Unity Objects (meshes, scriptable assets): Adopt(obj)
//      tracks the object and DestroyImmediate-s it on Dispose. Use for
//      cloned meshes the run created.
//   4. Spawned scene components: RememberSpawnedComponent(c) records a
//      component the run added to a GameObject. Dispose destroys them in
//      LIFO order so dependency chains (e.g., a PhysBoneCollider added
//      then a PhysBone that references it) unwind cleanly.
//
// Why HideFlags.DontSave on cloned meshes (callers should apply it):
//   HideFlags.DontSave = DontSaveInBuild | DontSaveInEditor | DontUnloadUnusedAsset
//   is the documented compound. Using DontSaveInEditor alone has been
//   reported to behave AS IF DontUnloadUnusedAsset were also set, but
//   the docs do not promise this -- relying on it would be a silent leak
//   path. DontSave is predictable; pair it with explicit DestroyImmediate
//   in Dispose.
//
// Why NOT Undo.RegisterCreatedObjectUndo on the clones: it would pin them
// in the undo stack and balloon memory. Lifetime is owned by this Session.

using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace WhyKnot.AvatarQol.Intent {

    internal sealed class AvatarIntentSession : System.IDisposable {

        private readonly Dictionary<SkinnedMeshRenderer, RendererState> _states =
            new Dictionary<SkinnedMeshRenderer, RendererState>();
        private readonly Dictionary<Component, ComponentState> _componentStates =
            new Dictionary<Component, ComponentState>();
        private readonly List<Object> _generated = new List<Object>();
        private readonly List<Component> _spawnedComponents = new List<Component>();

        public bool HasChanges => _states.Count > 0 || _componentStates.Count > 0 || _generated.Count > 0 || _spawnedComponents.Count > 0;
        public int CapturedRendererCount => _states.Count;
        public int CapturedComponentCount => _componentStates.Count;
        public int GeneratedObjectCount => _generated.Count;
        public int SpawnedComponentCount => _spawnedComponents.Count;

        public void Capture(SkinnedMeshRenderer renderer) {
            if (renderer == null || _states.ContainsKey(renderer)) return;
            _states[renderer] = new RendererState(renderer);
        }

        public void Capture(Component component) {
            if (component == null || _componentStates.ContainsKey(component)) return;
            _componentStates[component] = new ComponentState(component);
        }

        public void Adopt(Object generated) {
            if (generated != null) _generated.Add(generated);
        }

        public void RememberSpawnedComponent(Component component) {
            if (component != null) _spawnedComponents.Add(component);
        }

        public void Merge(AvatarIntentSession other) {
            if (other == null || ReferenceEquals(other, this)) return;
            foreach (var kv in other._states) {
                if (kv.Key != null && !_states.ContainsKey(kv.Key)) _states[kv.Key] = kv.Value;
            }
            foreach (var kv in other._componentStates) {
                if (kv.Key != null && !_componentStates.ContainsKey(kv.Key)) _componentStates[kv.Key] = kv.Value;
            }
            foreach (var g in other._generated) if (g != null) _generated.Add(g);
            foreach (var c in other._spawnedComponents) if (c != null) _spawnedComponents.Add(c);
            other._states.Clear();
            other._componentStates.Clear();
            other._generated.Clear();
            other._spawnedComponents.Clear();
        }

        public void Restore() => Dispose();

        public void Dispose() {
            // Spawned components come off first so a component still
            // pointing at a captured-then-restored renderer does not run
            // its OnDestroy against half-restored state.
            for (int i = _spawnedComponents.Count - 1; i >= 0; i--) {
                var c = _spawnedComponents[i];
                if (c != null) Object.DestroyImmediate(c);
            }
            _spawnedComponents.Clear();

            foreach (var state in _states.Values) state.Restore();
            _states.Clear();

            foreach (var state in _componentStates.Values) state.Restore();
            _componentStates.Clear();

            for (int i = _generated.Count - 1; i >= 0; i--) {
                var obj = _generated[i];
                if (obj != null) Object.DestroyImmediate(obj);
            }
            _generated.Clear();
        }

        private sealed class RendererState {
            private readonly SkinnedMeshRenderer _renderer;
            private readonly Mesh _mesh;
            private readonly Dictionary<string, float> _weightsByName;

            public RendererState(SkinnedMeshRenderer renderer) {
                _renderer = renderer;
                _mesh = renderer != null ? renderer.sharedMesh : null;
                _weightsByName = BlendShapeUtility.CaptureWeights(renderer, _mesh);
            }

            public void Restore() {
                if (_renderer == null) return;
                _renderer.sharedMesh = _mesh;
                BlendShapeUtility.RestoreWeights(_renderer, _mesh, _weightsByName);
            }
        }

        private sealed class ComponentState {
            private readonly Component _component;
            private readonly string _json;

            public ComponentState(Component component) {
                _component = component;
                _json = component != null ? EditorJsonUtility.ToJson(component) : "";
            }

            public void Restore() {
                if (_component == null || string.IsNullOrEmpty(_json)) return;
                EditorJsonUtility.FromJsonOverwrite(_json, _component);
                EditorUtility.SetDirty(_component);
            }
        }
    }
}
