// PhysBoneClippingAnalyzer.SpatialHash.cs

using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using UnityEditor;
using UnityEngine;
using WhyKnot.AvatarQol.Internal.Styling;
using WhyKnot.AvatarQol.Internal.Utilities;

#if VRC_SDK_VRCSDK3
using VRC.SDK3.Dynamics.PhysBone.Components;
#endif

namespace WhyKnot.AvatarQol.Tools {

    internal static partial class PhysBoneClippingAnalyzer {

#if VRC_SDK_VRCSDK3
        private sealed class SpatialHash {
            private readonly float _cellSize;
            private readonly Dictionary<Vector3Int, List<SurfaceSample>> _cells = new Dictionary<Vector3Int, List<SurfaceSample>>();

            public SpatialHash(float cellSize) {
                _cellSize = Mathf.Max(0.005f, cellSize);
            }

            public void Add(SurfaceSample sample) {
                var cell = Cell(sample.Position);
                if (!_cells.TryGetValue(cell, out var list)) {
                    list = new List<SurfaceSample>();
                    _cells[cell] = list;
                }
                list.Add(sample);
            }

            public IEnumerable<SurfaceSample> Query(Vector3 position, float radius) {
                int r = Mathf.CeilToInt(radius / _cellSize);
                var center = Cell(position);
                for (int x = center.x - r; x <= center.x + r; x++) {
                    for (int y = center.y - r; y <= center.y + r; y++) {
                        for (int z = center.z - r; z <= center.z + r; z++) {
                            if (_cells.TryGetValue(new Vector3Int(x, y, z), out var list)) {
                                foreach (var sample in list) yield return sample;
                            }
                        }
                    }
                }
            }

            private Vector3Int Cell(Vector3 p) {
                return new Vector3Int(
                    Mathf.FloorToInt(p.x / _cellSize),
                    Mathf.FloorToInt(p.y / _cellSize),
                    Mathf.FloorToInt(p.z / _cellSize));
            }
        }
#endif
    }
}
