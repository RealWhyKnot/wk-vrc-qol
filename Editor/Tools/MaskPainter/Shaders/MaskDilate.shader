// MaskDilate.shader
//
// One iteration of UV-island dilation. For each empty pixel (a channel
// value at or near zero), copy the value from the brightest 8-neighbour
// in that channel. Iterating N times grows painted regions N pixels
// outward into surrounding empty UV space, eliminating the black-edge
// halo that bilinear filtering and mipmaps would otherwise introduce at
// UV island borders.
//
// Per-channel handling matters for per-channel mask mode: a pixel can
// be "empty in R" but "filled in G", and we want the R dilation to
// proceed without overwriting the G value. Each channel is dilated
// independently.
//
// Driven by ping-pong Blit between two RenderTextures from
// MaskPainterIO.

Shader "Hidden/WhyKnot/MaskPainter/Dilate" {
    Properties {
        _MainTex ("Mask", 2D) = "black" {}
    }

    SubShader {
        Tags { "RenderType"="Opaque" }
        Cull Off ZWrite Off ZTest Always

        Pass {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"

            struct appdata { float4 vertex : POSITION; float2 uv : TEXCOORD0; };
            struct v2f     { float4 pos : SV_POSITION; float2 uv : TEXCOORD0; };

            sampler2D _MainTex;
            float4    _MainTex_TexelSize; // .xy = 1/width, 1/height

            v2f vert(appdata v) {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
                o.uv  = v.uv;
                return o;
            }

            fixed4 sampleOff(float2 uv, float dx, float dy) {
                return tex2D(_MainTex, uv + float2(dx, dy) * _MainTex_TexelSize.xy);
            }

            fixed4 frag(v2f i) : SV_Target {
                fixed4 c = tex2D(_MainTex, i.uv);
                // Below this value, treat the channel as "empty" and
                // candidate for filling from a neighbour. Above this,
                // the pixel already has content and is preserved.
                const float EMPTY = 0.001;

                fixed4 result = c;
                fixed4 n[8];
                n[0] = sampleOff(i.uv, -1, -1);
                n[1] = sampleOff(i.uv,  0, -1);
                n[2] = sampleOff(i.uv,  1, -1);
                n[3] = sampleOff(i.uv, -1,  0);
                n[4] = sampleOff(i.uv,  1,  0);
                n[5] = sampleOff(i.uv, -1,  1);
                n[6] = sampleOff(i.uv,  0,  1);
                n[7] = sampleOff(i.uv,  1,  1);

                [unroll]
                for (int k = 0; k < 8; k++) {
                    if (result.r < EMPTY && n[k].r > result.r) result.r = n[k].r;
                    if (result.g < EMPTY && n[k].g > result.g) result.g = n[k].g;
                    if (result.b < EMPTY && n[k].b > result.b) result.b = n[k].b;
                    if (result.a < EMPTY && n[k].a > result.a) result.a = n[k].a;
                }
                return result;
            }
            ENDCG
        }
    }
}
