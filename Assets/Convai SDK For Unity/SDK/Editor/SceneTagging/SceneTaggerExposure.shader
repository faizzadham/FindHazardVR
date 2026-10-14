Shader "Hidden/Convai/SceneTaggerExposure"
{
    Properties { _MainTex("Image", 2D) = "white" {} _Exposure("Exposure", Float) = 1 }
    SubShader
    {
        Pass
        {
            ZTest Always Cull Off ZWrite Off
            HLSLPROGRAM
            #pragma vertex vert_img
            #pragma fragment frag
            #include "UnityCG.cginc"
            sampler2D _MainTex;
            float _Exposure;
            float4 frag(v2f_img input) : SV_Target
            {
                float3 color = tex2D(_MainTex, input.uv).rgb;
                #ifdef UNITY_COLORSPACE_GAMMA
                color = GammaToLinearSpace(color);
                #endif
                color *= _Exposure;
                #ifdef UNITY_COLORSPACE_GAMMA
                color = LinearToGammaSpace(color);
                #endif
                return float4(color, 1);
            }
            ENDHLSL
        }
    }
}
