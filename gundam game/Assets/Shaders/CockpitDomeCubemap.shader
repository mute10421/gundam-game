// Cockpit_Dome's 360-degree live-feed shader - see MakeCubemapScreenMat in
// GundamCockpitSetup.cs and the "cubemap" field on GundamHeadCam360.cs.
//
// Why this exists: painting the Gundam's head-cam feed onto Cockpit_Dome used
// to just stretch one flat 2D perspective image (headCamTex, ~60 degree FOV)
// over the whole enclosing sphere. That only ever contained whatever was
// directly in front of the head-cam at the moment it was rendered - anything
// outside that narrow forward cone (per report, "밖에 자쿠가 보이지 않아" -
// ZakuEnemy standing further out) was never in the image at all, no matter
// how the UV-stretching distorted it around the sphere.
//
// This shader instead samples a real CUBEMAP (rendered every frame by
// Camera.RenderToCubemap in GundamHeadCam360.cs, the same one already used for
// RenderSettings.skybox) using the true world-space direction from the dome's
// center outward to each point on its surface. Since the dome is built
// centered close to the pilot's own head, that direction is a very good
// approximation of "which way the pilot is looking from here" - exactly the
// same idea a normal Skybox uses, just sampled on a regular mesh instead of
// as the camera's background (which would never actually be seen anyway,
// since the solid dome always fully encloses the camera and occludes it).
//
// Written by hand as a plain URP ShaderLab/HLSL shader (not Shader Graph) so
// it's a single readable text file. Deliberately minimal - single unlit pass,
// no lighting - matching the same "guaranteed to render correctly under URP"
// reasoning already used for MakeUnlitScreenMat/MakeShellMat (this project
// avoids legacy built-in shaders like "Skybox/Cubemap" on regular meshes,
// since those aren't written for URP's pipeline tags and tend to show up as
// broken/pink "unsupported shader" instead).
Shader "Custom/CockpitDomeCubemap"
{
    Properties
    {
        _CubeTex("Live Cubemap (RGB)", CUBE) = "" {}
        _Exposure("Exposure", Range(0.1, 4)) = 1.0
    }

    SubShader
    {
        Tags { "RenderType" = "Opaque" "Queue" = "Geometry" "RenderPipeline" = "UniversalPipeline" }

        // Cull Front: the camera sits INSIDE this sphere (same reasoning as
        // MakeShellMat/MakeUnlitScreenMat), so only its back faces (the ones
        // facing inward, toward the pilot) should ever render.
        Cull Front
        ZWrite On

        Pass
        {
            Name "Unlit360"
            Tags { "LightMode" = "UniversalForward" }

            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            TEXTURECUBE(_CubeTex);
            SAMPLER(sampler_CubeTex);
            float _Exposure;

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS   : NORMAL;
            };

            struct Varyings
            {
                float4 positionHCS : SV_POSITION;
                float3 dirWS       : TEXCOORD0;
            };

            Varyings Vert(Attributes IN)
            {
                Varyings OUT;
                OUT.positionHCS = TransformObjectToHClip(IN.positionOS.xyz);

                // The dome is a standard sphere primitive, so its object-space
                // vertex normals already point radially outward from its own
                // center - exactly the direction we want to sample the
                // cubemap by, once rotated into world space (translation
                // doesn't matter for a direction, only rotation/scale do, so
                // this correctly ignores where the dome itself is positioned).
                OUT.dirWS = TransformObjectToWorldNormal(IN.normalOS);
                return OUT;
            }

            half4 Frag(Varyings IN) : SV_Target
            {
                half3 col = SAMPLE_TEXTURECUBE(_CubeTex, sampler_CubeTex, normalize(IN.dirWS)).rgb;
                return half4(col * _Exposure, 1.0);
            }
            ENDHLSL
        }
    }

    FallBack "Universal Render Pipeline/Unlit"
}
