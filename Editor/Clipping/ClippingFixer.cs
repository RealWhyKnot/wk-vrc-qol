// ClippingFixer.cs
//
// Stable facade and shared model for the Clipping Fixer window and the
// build/play component. Scan, apply, and surface-spatial details live in
// focused partials beside this file.

using System;
using System.Collections.Generic;
using UnityEngine;

namespace WhyKnot.AvatarQol.Clipping {

    internal static partial class ClippingFixer {

        internal const string GeneratedFolder = "Assets/AvatarQol Generated";

        internal enum IssueKind {
            ComparisonMesh,
            SurfaceIntersection,
            SelfIntersection,
            PhysBoneMotion,
        }

        internal sealed class Settings {
            public Animator Animator;
            public bool CheckSelf = true;
            public bool IncludePhysBoneMotion = true;
            public float InsideTolerance = 0.001f;
            public float SurfacePadding = 0.005f;
            public float PhysBoneWeightFloor = 0.03f;
            public float PhysBoneClearanceMargin = 0.025f;
            public int MaxWarnings = 250;
            public int MaxIssuesPerPhysBone = 8;
        }

        internal sealed class Issue {
            public IssueKind Kind;
            public SkinnedMeshRenderer Renderer;
            public string RendererPath;
            public SkinnedMeshRenderer ComparisonRenderer;
            public string ComparisonPath;
            public int VertexIndex = -1;
            public int TargetTriangleIndex = -1;
            public int ComparisonTriangleIndex = -1;
            public int[] AffectedVertexIndices = Array.Empty<int>();
            public Vector3 WorldPosition;
            public Vector3 NearestSurfacePosition;
            public Vector3 SurfaceNormal;
            public Vector3 PushWorld;
            public float PenetrationDepth;
            public float Score;
            public string Reason;
            public Component PhysBoneComponent;
            public string PhysBoneSourceLabel;
            public Transform PhysBoneRoot;
            public Transform DrivenBone;
            public float PhysBoneWeight;
            public float EstimatedMotion;
            public float Clearance;
            public bool HasEffectiveColliders;
            public bool PhysBoneHighSeverity;
        }

        internal sealed class Result {
            public string Summary = "";
            public readonly List<string> Detail = new List<string>();
            public readonly List<string> ClonedPaths = new List<string>();
            public int IssuesFound;
            public int VerticesReweighted;
            public int FixPasses;
            public int MeshesCloned;
            public int RenderersTouched;
            public int UnreadableRenderers;
            public bool ConfigurationError;

            public bool DidAnything => VerticesReweighted > 0 || MeshesCloned > 0 || RenderersTouched > 0;
        }
    }
}
