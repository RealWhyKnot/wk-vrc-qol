// SkinnedMeshSnapshot.cs
//
// Rest-mesh geometry transformed into a shared comparison space. Weight
// transfer defaults to rest geometry; the target weights are written back
// to the target mesh's bone-weight stream, not to a baked posed mesh.

using System;
using UnityEngine;
using WhyKnot.AvatarQol.Tools;

namespace WhyKnot.AvatarQol.Weighting {

    internal sealed class SkinnedMeshSnapshot {

        public Mesh Mesh;
        public Vector3[] Positions;
        public Vector3[] Normals;
        public Matrix4x4 MeshLocalToSpace;

        public static SkinnedMeshSnapshot Build(SkinnedMeshRenderer renderer, Transform spaceRoot = null) {
            var mesh = renderer != null ? renderer.sharedMesh : null;
            if (mesh == null) {
                return new SkinnedMeshSnapshot {
                    Mesh = null,
                    Positions = Array.Empty<Vector3>(),
                    Normals = Array.Empty<Vector3>(),
                    MeshLocalToSpace = Matrix4x4.identity,
                };
            }

            Matrix4x4 worldToSpace = spaceRoot != null ? spaceRoot.worldToLocalMatrix : Matrix4x4.identity;
            Matrix4x4 localToSpace = worldToSpace * renderer.transform.localToWorldMatrix;
            var positions = UvTextureTransferCore.TransformVertices(mesh.vertices, localToSpace);
            var normals = UvTextureTransferCore.TransformNormals(
                UvTextureTransferCore.ResolveNormals(mesh),
                localToSpace);

            return new SkinnedMeshSnapshot {
                Mesh = mesh,
                Positions = positions,
                Normals = normals,
                MeshLocalToSpace = localToSpace,
            };
        }
    }
}
