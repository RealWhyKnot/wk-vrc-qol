// MaskPreviewOverlay.shader
//
// Live overlay that tints the painted region of the avatar in the
// Scene view. Drawn from MaskPainterWindow via Graphics.DrawMesh against
// the baked snapshot mesh, layered on top of the real SkinnedMeshRenderer.
//
// Why a separate overlay instead of swapping the renderer's material:
// no risk of leaving the avatar with a leftover preview material if the
// Editor crashes, domain-reloads, or the window closes unexpectedly.
//
// _ChannelMask picks which mask channel becomes the tint alpha:
//   grayscale -> (1,0,0,0) -- any channel works since all are equal
//   R         -> (1,0,0,0)
//   G         -> (0,1,0,0)
//   B         -> (0,0,1,0)
//   A         -> (0,0,0,1)
//
// Offset -1, -1 plus a tiny model-matrix scale-up (1.001) wins the
// depth fight against the actual SkinnedMeshRenderer without visible
// inflation. ZTest LEqual keeps the overlay correctly occluded by
// foreground geometry (eyelashes, hair sitting in front of skin).

Shader "Hidden/WhyKnot/MaskPainter/PreviewOverlay" {
    Properties {
        _MaskTex     ("Mask Texture",         2D)     = "black" {}
        _ChannelMask ("Channel Mask (RGBA)",  Vector) = (1,0,0,0)
        _TintColor   ("Tint Color",           Color)  = (1.0, 0.55, 0.10, 1.0)
        _TintAlpha   ("Tint Alpha (0..1)",    Float)  = 0.55
    }

    SubShader {
        Tags { "RenderType"="Transparent" "Queue"="Overlay" "IgnoreProjector"="True" }

        Pass {
            Cull Back
            ZWrite Off
            ZTest LEqual
            Offset -1, -1
            Blend SrcAlpha OneMinusSrcAlpha

            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"

            struct appdata { float4 vertex : POSITION; float2 uv : TEXCOORD0; };
            struct v2f     { float4 pos : SV_POSITION; float2 uv : TEXCOORD0; };

            sampler2D _MaskTex;
            float4    _ChannelMask;
            fixed4    _TintColor;
            float     _TintAlpha;

            v2f vert(appdata v) {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
                o.uv  = v.uv;
                return o;
            }

            fixed4 frag(v2f i) : SV_Target {
                fixed4 m = tex2D(_MaskTex, i.uv);
                float  v = saturate(dot(m, _ChannelMask));
                return fixed4(_TintColor.rgb, v * _TintAlpha);
            }
            ENDCG
        }
    }
}
