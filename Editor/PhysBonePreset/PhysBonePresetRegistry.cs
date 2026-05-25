// PhysBonePresetRegistry.cs
//
// Cached lookup of every IPhysBonePreset implementation in the loaded
// assemblies. Used by PhysBonePresetApplier to resolve a stored preset
// id at play / build time without duplicating PhysBonePresetWindow's
// discovery logic. The cache is invalidated on assembly reload via the
// static ctor's [InitializeOnLoad] anchor.

using System;
using System.Collections.Generic;
using UnityEditor;

namespace WhyKnot.AvatarQol.Tools {

    [InitializeOnLoad]
    internal static class PhysBonePresetRegistry {

        private static Dictionary<string, IPhysBonePreset> _byId;

        static PhysBonePresetRegistry() {
            // Drop the cache; first FindById after a reload rebuilds it.
            _byId = null;
        }

        public static IPhysBonePreset FindById(string id) {
            if (string.IsNullOrEmpty(id)) return null;
            EnsureCache();
            return _byId.TryGetValue(id, out var preset) ? preset : null;
        }

        public static IEnumerable<IPhysBonePreset> All() {
            EnsureCache();
            return _byId.Values;
        }

        private static void EnsureCache() {
            if (_byId != null) return;
            _byId = new Dictionary<string, IPhysBonePreset>(StringComparer.Ordinal);
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies()) {
                Type[] types;
                try { types = asm.GetTypes(); }
                catch { continue; }
                foreach (var t in types) {
                    if (t.IsAbstract || t.IsInterface) continue;
                    if (!typeof(IPhysBonePreset).IsAssignableFrom(t)) continue;
                    var ctor = t.GetConstructor(Type.EmptyTypes);
                    if (ctor == null) continue;
                    try {
                        var preset = (IPhysBonePreset)ctor.Invoke(null);
                        if (preset != null && !string.IsNullOrEmpty(preset.Id)) {
                            _byId[preset.Id] = preset;
                        }
                    } catch {
                        // Skip: preset constructor threw.
                    }
                }
            }
        }
    }
}
