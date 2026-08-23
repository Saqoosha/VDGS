// SPDX-License-Identifier: MIT
Shader "Hidden/Gaussian Splatting/Composite"
{
    SubShader
    {
        Pass
        {
            ZWrite Off
            ZTest Always
            Cull Off
            // Premultiplied "under" output. The RT holds display-referred premultiplied
            // colour exactly like a WebGL splat viewer's canvas; converting the whole
            // premultiplied product keeps dim accumulations (alpha << 1) at the web
            // viewers' brightness. The previous SrcAlpha blend converted the
            // unpremultiplied colour first and re-multiplied alpha in linear space,
            // which lifted low-alpha haze 3-5x and painted a veil over dark sky that
            // SuperSplat/antimatter15 never show. It also divided by zero on empty
            // pixels.
            Blend One OneMinusSrcAlpha

CGPROGRAM
#pragma vertex vert
#pragma fragment frag
#pragma require compute
#pragma use_dxc
#include "UnityCG.cginc"

struct v2f
{
    float4 vertex : SV_POSITION;
};

v2f vert (uint vtxID : SV_VertexID)
{
    v2f o;
    float2 quadPos = float2(vtxID&1, (vtxID>>1)&1) * 4.0 - 1.0;
	o.vertex = float4(quadPos, 1, 1);
    return o;
}

Texture2D _GaussianSplatRT;

half4 frag (v2f i) : SV_Target
{
    half4 col = _GaussianSplatRT.Load(int3(i.vertex.xy, 0));
    return float4(GammaToLinearSpace(col.rgb),col.a);
}
ENDCG
        }
    }
}
