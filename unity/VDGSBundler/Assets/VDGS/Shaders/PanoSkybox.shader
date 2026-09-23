// SPDX-License-Identifier: MIT
//
// An equirectangular panorama as the scene's skybox.
//
// Unity ships Skybox/Panoramic, but a shader that no scene references is stripped from
// the player build, so the game may or may not carry it. This is the same job in the
// few lines it actually takes, baked into the mod's own bundle where its presence is
// not a question.
//
// The panorama is in the capture's own world frame; the capture is placed into the game
// with a mirror and a turn. _SkyToPano carries that transform's inverse, so one matrix
// multiply per pixel puts the view direction back where the panorama was measured. The
// alternative - a yaw angle and a mirror flag - is two cheap ops instead of three dot
// products, and every sign of it is a chance to put the sun on the wrong side.
Shader "VDGS/PanoSkybox"
{
    Properties
    {
        _Pano ("Panorama (equirectangular)", 2D) = "grey" {}
        _Exposure ("Exposure", Float) = 1.0
    }

    SubShader
    {
        Tags { "Queue"="Background" "RenderType"="Background" "PreviewType"="Skybox" }
        Cull Off ZWrite Off

        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"

            sampler2D _Pano;
            half _Exposure;
            float4x4 _SkyToPano;

            struct appdata { float4 vertex : POSITION; };
            struct v2f
            {
                float4 vertex : SV_POSITION;
                float3 dir : TEXCOORD0;
            };

            v2f vert (appdata v)
            {
                v2f o;
                o.vertex = UnityObjectToClipPos(v.vertex);
                // Unity draws the skybox mesh in a frame that is already centred on the
                // camera, so the object-space position IS the view direction.
                o.dir = v.vertex.xyz;
                return o;
            }

            half4 frag (v2f i) : SV_Target
            {
                float3 d = normalize(mul((float3x3)_SkyToPano, i.dir));
                // Equirectangular: longitude around +y, latitude from the horizon.
                // atan2 is the whole cost of this shader.
                float u = atan2(d.x, -d.z) / (2 * UNITY_PI) + 0.5;
                float v = acos(clamp(-d.y, -1, 1)) / UNITY_PI;

                // u wraps from 1 back to 0 along one line of pixels behind the camera,
                // and tex2D picks its mip from the screen-space derivative of what it is
                // given: across that line the derivative is the width of the whole
                // texture, so the hardware drops to the 1x1 mip and draws a dark seam a
                // pixel wide. The fix is to hand it derivatives that do not jump. A
                // half-turn-shifted copy of u is continuous exactly where u is not, so
                // its derivative stands in there; v never wraps and keeps its own.
                float u2 = frac(u + 0.5);
                float2 du = float2(ddx(u), ddy(u));
                float2 du2 = float2(ddx(u2), ddy(u2));
                if (dot(du, du) > dot(du2, du2)) du = du2;
                float2 dvdxy = float2(ddx(v), ddy(v));
                return half4(tex2Dgrad(_Pano, float2(u, v),
                                       float2(du.x, dvdxy.x),
                                       float2(du.y, dvdxy.y)).rgb * _Exposure, 1);
            }
            ENDCG
        }
    }

    Fallback Off
}
