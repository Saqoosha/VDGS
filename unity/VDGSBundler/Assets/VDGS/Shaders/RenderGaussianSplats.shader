// SPDX-License-Identifier: MIT
Shader "Gaussian Splatting/Render Splats"
{
    SubShader
    {
        Tags { "RenderType"="Transparent" "Queue"="Transparent" }

        Pass
        {
            ZWrite Off
            Blend OneMinusDstAlpha One
            Cull Off
            
CGPROGRAM
#pragma vertex vert
#pragma fragment frag
#pragma require compute
#pragma use_dxc

#include "UnityCG.cginc"
#include "GaussianSplatting.hlsl"

StructuredBuffer<uint> _OrderBuffer;

// Scene depth for manual occlusion. The splat pass renders into its own color-only RT:
// binding the camera depth buffer as a depth attachment (SetRenderTarget with
// CurrentActive) silently invalidates the whole bind on the game's HDR+PostProcessing
// cameras under D3D12 - the splats then draw straight into the camera target and the
// composite reads an empty RT. So no depth attachment, and the occlusion the hardware
// Z-test used to give is done here by sampling the camera depth texture instead.
// The plugin forces DepthTextureMode.Depth on hooked cameras; where the texture is
// absent (menu, offline render harness) the unbound SRV reads 0 = far plane under
// reversed-Z, and every splat passes - same behaviour as before.
UNITY_DECLARE_DEPTH_TEXTURE(_CameraDepthTexture);

struct v2f
{
    half4 col : COLOR0;
    float2 pos : TEXCOORD0;
    float viewZ : TEXCOORD1;
    float4 vertex : SV_POSITION;
};

StructuredBuffer<SplatViewData> _SplatViewData;
ByteAddressBuffer _SplatSelectedBits;
uint _SplatBitsValid;

// Squared-radius cutoff for the gaussian, in the same units as the quad (0 = no cutoff,
// upstream's behaviour). The web reference viewers hard-discard at 4, i.e. two sigma:
//
//     if (A < -4.0) discard;              antimatter15/splat, fragment shader
//
// Without it every splat keeps a faint 1-5/255 ring out to where alpha crosses 1/255,
// and with a million overlapping splats those rings sum into exactly the haze that made
// dark sky glow here and stay black in a web viewer.
float _SplatGaussCut;
// 0 disables the manual scene-depth test below (splats then draw over everything).
float _SplatDepthClip;

v2f vert (uint vtxID : SV_VertexID, uint instID : SV_InstanceID)
{
    v2f o = (v2f)0;
    instID = _OrderBuffer[instID];
	SplatViewData view = _SplatViewData[instID];
	float4 centerClipPos = view.pos;
	bool behindCam = centerClipPos.w <= 0;
	if (behindCam)
	{
		o.vertex = asfloat(0x7fc00000); // NaN discards the primitive
	}
	else
	{
		o.col.r = f16tof32(view.color.x >> 16);
		o.col.g = f16tof32(view.color.x);
		o.col.b = f16tof32(view.color.y >> 16);
		o.col.a = f16tof32(view.color.y);

		uint idx = vtxID;
		float2 quadPos = float2(idx&1, (idx>>1)&1) * 2.0 - 1.0;
		quadPos *= 2;

		o.pos = quadPos;

		float2 deltaScreenPos = (quadPos.x * view.axis1 + quadPos.y * view.axis2) * 2 / _ScreenParams.xy;
		o.vertex = centerClipPos;
		o.vertex.xy += deltaScreenPos * centerClipPos.w;
		// View-space depth of the splat centre - the same constant the quad's hardware
		// depth test used when a depth attachment was still bound.
		o.viewZ = centerClipPos.w;

		// is this splat selected?
		if (_SplatBitsValid)
		{
			uint wordIdx = instID / 32;
			uint bitIdx = instID & 31;
			uint selVal = _SplatSelectedBits.Load(wordIdx * 4);
			if (selVal & (1 << bitIdx))
			{
				o.col.a = -1;				
			}
		}
	}
	FlipProjectionIfBackbuffer(o.vertex);
    return o;
}

half4 frag (v2f i) : SV_Target
{
	// Manual scene-depth test (see _CameraDepthTexture note above). This mod only runs
	// under -force-d3d12, where render-target UVs start at the top - so SV_Position
	// pixel coordinates map straight onto the depth texture.
	if (_SplatDepthClip != 0)
	{
		float2 depthUV = i.vertex.xy / _ScreenParams.xy;
		float sceneEyeZ = LinearEyeDepth(SAMPLE_DEPTH_TEXTURE(_CameraDepthTexture, depthUV));
		if (i.viewZ > sceneEyeZ * 1.005 + 0.1)
			discard;
	}

	float r2 = dot(i.pos, i.pos);
	if (_SplatGaussCut > 0 && r2 > _SplatGaussCut)
		discard;

	float power = -r2;
	half alpha = exp(power);
	if (i.col.a >= 0)
	{
		alpha = saturate(alpha * i.col.a);
	}
	else
	{
		// "selected" splat: magenta outline, increase opacity, magenta tint
		half3 selectedColor = half3(1,0,1);
		if (alpha > 7.0/255.0)
		{
			if (alpha < 10.0/255.0)
			{
				alpha = 1;
				i.col.rgb = selectedColor;
			}
			alpha = saturate(alpha + 0.3);
		}
		i.col.rgb = lerp(i.col.rgb, selectedColor, 0.5);
	}
	
    if (alpha < 1.0/255.0)
        discard;

    half4 res = half4(i.col.rgb * alpha, alpha);
    return res;
}
ENDCG
        }
    }
}
