// UvSpaceBrush.shader
//
// Renders mesh triangles directly into a RenderTexture in UV space:
// the vertex shader emits clip-space coordinates derived from each
// vertex's UV (uv -> (-1..1) NDC), so each rasterized pixel corresponds
// 1:1 to its UV-space pixel in the RT. The fragment then tests world
// distance from the brush center (and its mirror, when symmetry is on)
// to decide whether and how strongly to paint that pixel.
//
// Why world distance, not UV distance: world distance keeps the brush
// footprint round on the avatar regardless of how stretched the UVs
// are. A 5 cm brush stamp paints a 5 cm region of skin, period -- even
// across UV seams or islands of wildly different scale.
//
// Five passes, picked from C# via material.SetPass(passIndex):
//   0  ColorMask RGBA   -- grayscale (paints all four channels)
//   1  ColorMask R
//   2  ColorMask G
//   3  ColorMask B
//   4  ColorMask A
//
// Blend is SrcAlpha OneMinusSrcAlpha (standard paint-over), NOT additive.
// Continuous drag with additive blending saturates in a fraction of a
// second; SrcAlpha lets _Strength behave like an opacity multiplier.

Shader "Hidden/WhyKnot/MaskPainter/UvSpaceBrush" {
    Properties {
        _BrushCenter       ("Brush Center (world)",        Vector) = (0,0,0,0)
        _MirrorBrushCenter ("Mirror Brush Center (world)", Vector) = (0,0,0,0)
        _SymmetryEnabled   ("Symmetry Enabled",            Float)  = 0
        _BrushRadius       ("Brush Radius (world m)",      Float)  = 0.05
        _BrushHardness     ("Brush Hardness (0..1)",       Float)  = 0.5
        _Strength          ("Strength (0..1)",             Float)  = 0.5
        _BrushColor        ("Brush Color",                 Color)  = (1,1,1,1)
    }

    CGINCLUDE
    #include "UnityCG.cginc"

    struct appdata { float4 vertex : POSITION; float2 uv : TEXCOORD0; };
    struct v2f     { float4 pos : SV_POSITION; float3 worldPos : TEXCOORD0; };

    float3 _BrushCenter;
    float3 _MirrorBrushCenter;
    float  _SymmetryEnabled;
    float  _BrushRadius;
    float  _BrushHardness;
    float  _Strength;
    float4 _BrushColor;

    v2f vert(appdata v) {
        v2f o;
        // UV (0..1) -> clip (-1..1). Y is flipped on platforms whose
        // texture V starts at top so the saved/sampled mask matches the
        // mesh's UV layout regardless of graphics API.
        o.pos = float4(v.uv.x * 2.0 - 1.0, v.uv.y * 2.0 - 1.0, 0.0, 1.0);
        #if UNITY_UV_STARTS_AT_TOP
            o.pos.y = -o.pos.y;
        #endif
        // The caller (MaskPainterWindow.ApplyStroke) draws a pre-baked
        // world-space mesh via CommandBuffer.DrawMesh with Matrix4x4.identity,
        // so v.vertex IS the world position. Multiplying by unity_ObjectToWorld
        // would still be a no-op when the model matrix is identity, but
        // referencing the built-in opens a door: Unity's editor render path
        // has historically left unity_ObjectToWorld bound to whatever the
        // last SceneView draw used, and Graphics.DrawMeshNow inherited that
        // state. The symptom was the brush stamping the camera-visible UV
        // region instead of a tiny patch around the click. Reading POSITION
        // directly keeps the worldPos pipeline independent of any leaked
        // model matrix.
        o.worldPos = v.vertex.xyz;
        return o;
    }

    float4 frag(v2f i) : SV_Target {
        float d1 = distance(i.worldPos, _BrushCenter);
        float d2 = _SymmetryEnabled > 0.5 ? distance(i.worldPos, _MirrorBrushCenter) : 1e9;
        float d  = min(d1, d2);
        clip(_BrushRadius - d);
        // Hardness 0 -> linear falloff across the full radius.
        // Hardness 1 -> hard edge (smoothstep degenerates to a step).
        float falloff = 1.0 - smoothstep(_BrushRadius * _BrushHardness, _BrushRadius, d);
        return float4(_BrushColor.rgb, _BrushColor.a * falloff * _Strength);
    }
    ENDCG

    SubShader {
        Tags { "RenderType"="Opaque" }

        // State directives REPEATED in every Pass. Setting them at the
        // SubShader scope wasn't reliably propagating through Unity's
        // SetPass + DrawMeshNow path -- the previous draw's Cull mode and
        // blend state were sticking, which made the brush paint every
        // camera-facing triangle regardless of its world-space distance
        // to the click. Spell out the state per pass so SetPass(idx)
        // resets it explicitly.
        Pass {
            Name "Grayscale_RGBA"
            Cull Off ZWrite Off ZTest Always
            Blend SrcAlpha OneMinusSrcAlpha
            ColorMask RGBA
            CGPROGRAM #pragma vertex vert #pragma fragment frag ENDCG
        }
        Pass {
            Name "Channel_R"
            Cull Off ZWrite Off ZTest Always
            Blend SrcAlpha OneMinusSrcAlpha
            ColorMask R
            CGPROGRAM #pragma vertex vert #pragma fragment frag ENDCG
        }
        Pass {
            Name "Channel_G"
            Cull Off ZWrite Off ZTest Always
            Blend SrcAlpha OneMinusSrcAlpha
            ColorMask G
            CGPROGRAM #pragma vertex vert #pragma fragment frag ENDCG
        }
        Pass {
            Name "Channel_B"
            Cull Off ZWrite Off ZTest Always
            Blend SrcAlpha OneMinusSrcAlpha
            ColorMask B
            CGPROGRAM #pragma vertex vert #pragma fragment frag ENDCG
        }
        Pass {
            Name "Channel_A"
            Cull Off ZWrite Off ZTest Always
            Blend SrcAlpha OneMinusSrcAlpha
            ColorMask A
            CGPROGRAM #pragma vertex vert #pragma fragment frag ENDCG
        }
    }
}
