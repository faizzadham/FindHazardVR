Shader "Hidden/Convai/SceneTaggerMask"
{
    Properties { _MainTex("Texture", 2D) = "white" {} _Cutoff("Alpha cutoff", Float) = 0 _Alpha("Alpha", Float) = 1 }
    SubShader
    {
        Pass
        {
            Cull Off ZWrite On ZTest LEqual
            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"
            sampler2D _MainTex;
            float4 _MainTex_ST;
            float _Cutoff, _Alpha;
            struct Input { float4 vertex : POSITION; float2 uv : TEXCOORD0; };
            struct Output { float4 vertex : SV_POSITION; float2 uv : TEXCOORD0; };
            Output vert(Input input) { Output o; o.vertex = UnityObjectToClipPos(input.vertex); o.uv = TRANSFORM_TEX(input.uv, _MainTex); return o; }
            float4 frag(Output input) : SV_Target { clip(tex2D(_MainTex, input.uv).a * _Alpha - _Cutoff); return 1; }
            ENDHLSL
        }
    }
}
