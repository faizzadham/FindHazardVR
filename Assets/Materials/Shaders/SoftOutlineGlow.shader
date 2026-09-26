Shader "Custom/URP_SoftOutlineGlow"
{
    Properties
    {
        [HDR] _OutlineColor ("Glow Color", Color) = (1.0, 0.85, 0.1, 1.0)
        _OutlineWidth ("Outline Width (Meters)", Range(0.001, 0.05)) = 0.008
        _Falloff ("Glow Falloff", Range(0.5, 4.0)) = 1.5
    }
    SubShader
    {
        Tags 
        { 
            "RenderType" = "Transparent" 
            "Queue" = "Transparent+100" 
            "RenderPipeline" = "UniversalPipeline" 
        }

        Pass
        {
            Name "OutlineGlow"
            Cull Front
            ZWrite Off
            ZTest LEqual
            Blend SrcAlpha One  // Additive glow

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            struct Attributes
            {
                float4 positionOS   : POSITION;
                float3 normalOS     : NORMAL;
            };

            struct Varyings
            {
                float4 positionCS   : SV_POSITION;
                float3 normalWS     : TEXCOORD0;
                float3 viewDirWS    : TEXCOORD1;
            };

            CBUFFER_START(UnityPerMaterial)
                float4 _OutlineColor;
                float _OutlineWidth;
                float _Falloff;
            CBUFFER_END

            Varyings vert(Attributes input)
            {
                Varyings output;
                
                // Get world position and world normal
                VertexPositionInputs vertexInput = GetVertexPositionInputs(input.positionOS.xyz);
                float3 normalWS = normalize(TransformObjectToWorldNormal(input.normalOS));
                
                // WORLD-SPACE EXTRUSION: Prevents scale (e.g. scale 100) from exploding the mesh
                float3 extrudedWorldPos = vertexInput.positionWS + (normalWS * _OutlineWidth);
                
                output.positionCS = TransformWorldToHClip(extrudedWorldPos);
                output.normalWS = normalWS;
                output.viewDirWS = GetWorldSpaceNormalizeViewDir(vertexInput.positionWS);
                return output;
            }

            half4 frag(Varyings input) : SV_Target
            {
                float rim = saturate(dot(input.normalWS, normalize(input.viewDirWS)));
                float alpha = pow(1.0 - rim, _Falloff);
                return half4(_OutlineColor.rgb, alpha * _OutlineColor.a);
            }
            ENDHLSL
        }
    }
}
