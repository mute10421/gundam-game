// HEAD CAM inset (center display) - per "랙이 조금 거림 ... 최적화좀해줘".
//
// The inset used to be a SECOND full render of the whole scene every frame
// (HeadCam auto-rendering into its flat 1024x1024 targetTexture) on top of the
// six cubemap faces GundamHeadCam360 already renders from the very same camera
// position. This shader shows the same picture without that extra render: it
// samples the live cubemap in the direction each inset pixel looks through
// HeadCam's own frustum (rotation _CamRotQ, vertical half-FOV tangent
// _TanHalfFov, aspect _Aspect - all set every frame by GundamHeadCam360).
// Cubemap faces are world-axis aligned, so a world direction is all it needs.
//
// Used by a uGUI RawImage on a world-space canvas: vertex color/alpha from the
// UI is kept, _MainTex is accepted (uGUI always sets it) but not used.
Shader "Custom/HeadCamInsetCube"
{
    Properties
    {
        [PerRendererData] _MainTex("UI Texture (unused)", 2D) = "white" {}
        _CubeTex("Live Cubemap", CUBE) = "" {}
        _CamRotQ("Camera Rotation (quaternion xyzw)", Vector) = (0, 0, 0, 1)
        _TanHalfFov("tan(vertical FOV / 2)", Float) = 0.57735
        _Aspect("Aspect (w/h)", Float) = 1
    }

    SubShader
    {
        Tags { "Queue" = "Transparent" "RenderType" = "Transparent" "IgnoreProjector" = "True" "RenderPipeline" = "UniversalPipeline" "PreviewType" = "Plane" }
        Cull Off
        ZWrite Off
        ZTest LEqual
        Blend SrcAlpha OneMinusSrcAlpha

        Pass
        {
            Name "InsetCube"

            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            TEXTURECUBE(_CubeTex);
            SAMPLER(sampler_CubeTex);
            float4 _CamRotQ;
            float _TanHalfFov;
            float _Aspect;

            float3 RotateByQuat(float3 v, float4 q)
            {
                float3 t = 2.0 * cross(q.xyz, v);
                return v + q.w * t + cross(q.xyz, t);
            }

            struct Attributes
            {
                float4 positionOS : POSITION;
                float4 color      : COLOR;
                float2 uv         : TEXCOORD0;
            };

            struct Varyings
            {
                float4 positionHCS : SV_POSITION;
                half4  color       : COLOR;
                float2 uv          : TEXCOORD0;
            };

            Varyings Vert(Attributes IN)
            {
                Varyings OUT;
                OUT.positionHCS = TransformObjectToHClip(IN.positionOS.xyz);
                OUT.color = IN.color;
                OUT.uv = IN.uv;
                return OUT;
            }

            half4 Frag(Varyings IN) : SV_Target
            {
                float2 ndc = IN.uv * 2.0 - 1.0;
                float3 dirCam = float3(ndc.x * _TanHalfFov * _Aspect, ndc.y * _TanHalfFov, 1.0);
                float3 dir = RotateByQuat(normalize(dirCam), _CamRotQ);
                half3 col = SAMPLE_TEXTURECUBE(_CubeTex, sampler_CubeTex, dir).rgb;
                return half4(col * IN.color.rgb, IN.color.a);
            }
            ENDHLSL
        }
    }
}
