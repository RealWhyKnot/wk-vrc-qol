// PhysBoneClippingAnalyzer.cs
//
// Conservative PhysBone clipping risk estimate for the Clipping Fixer
// PhysBone motion warning path.
// This is not VRChat's runtime solver. It uses the actual PhysBone settings
// that strongly affect motion (pull/spring/stiffness/gravity/radius/stretch
// and collider presence) to estimate how far weighted vertices can plausibly
// move, then compares that envelope to nearby non-driven mesh surface.

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

        internal enum Severity {
            Medium,
            High,
        }

        internal sealed class Settings {
            public float WeightFloor = 0.03f;
            public float ClearanceMargin = 0.025f;
            public int MaxIssuesPerPhysBone = 8;
            public int NativePhysBoneCount;
            public int CustomPhysBoneCount;
            public int DrivenVertexSampleCount;
            public int SurfaceSampleCount;

            public void ResetStats() {
                NativePhysBoneCount = 0;
                CustomPhysBoneCount = 0;
                DrivenVertexSampleCount = 0;
                SurfaceSampleCount = 0;
            }
        }

        internal sealed class Issue {
            public Severity Severity;
            public Component PhysBoneComponent;
            public string PhysBoneSourceLabel;
            public Transform PhysBoneRoot;
            public Transform DrivenBone;
            public SkinnedMeshRenderer Renderer;
            public string RendererPath;
            public int VertexIndex;
            public Vector3 WorldPosition;
            public Vector3 NearestSurfacePosition;
            public SkinnedMeshRenderer NearestSurfaceRenderer;
            public string NearestSurfacePath;
            public float Weight;
            public float EstimatedMotion;
            public float Clearance;
            public bool HasEffectiveColliders;
            public string Reason;
            public float Score;
        }

        internal static bool SdkAvailable {
            get {
#if VRC_SDK_VRCSDK3
                return true;
#else
                return false;
#endif
            }
        }

        internal sealed class MotionReductionResult {
            public int SourcesChanged;
            public int IssuesCovered;
            public int UnsupportedSources;
            public string Summary;
        }
    }
}
