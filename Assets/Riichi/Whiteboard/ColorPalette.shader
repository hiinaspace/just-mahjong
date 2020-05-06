Shader "Custom/ColorPalette"
{
    Properties
    {
        _Color ("Color", Color) = (1,1,1,1)
        _MainTex ("Albedo (RGB)", 2D) = "white" {}
        _Glossiness ("Smoothness", Range(0,1)) = 0.5
        _Metallic ("Metallic", Range(0,1)) = 0.0
    }
    SubShader
    {
        Tags { "RenderType"="Opaque" }
        LOD 200

        CGPROGRAM
        // Physically based Standard lighting model, and enable shadows on all light types
        #pragma surface surf Standard fullforwardshadows

        // Use shader model 3.0 target, to get nicer looking lighting
        #pragma target 3.0

        sampler2D _MainTex;

        struct Input
        {
            float2 uv_MainTex;
        };

        half _Glossiness;
        half _Metallic;
        fixed4 _Color;

        // Add instancing support for this shader. You need to check 'Enable Instancing' on materials that use the shader.
        // See https://docs.unity3d.com/Manual/GPUInstancing.html for more information about instancing.
        // #pragma instancing_options assumeuniformscaling
        UNITY_INSTANCING_BUFFER_START(Props)
            // put more per-instance properties here
        UNITY_INSTANCING_BUFFER_END(Props)

		float3 Hue(float H)
        {
            float R = abs(H * 6 - 3) - 1;
            float G = 2 - abs(H * 6 - 2);
            float B = 2 - abs(H * 6 - 4);
            return saturate(float3(R, G, B));
        }

        float4 HSVtoRGB(in float3 HSV)
        {
            return float4(((Hue(HSV.x) - 1) * HSV.y + 1) * HSV.z, 1);
        }

        void surf (Input IN, inout SurfaceOutputStandard o)
        {
            float hue = IN.uv_MainTex.x;
            // at 0 value, saturation doesn't matter (all black)
            // at low saturation max value, all white;
            // so split Y range from 0 - 1 - 0 on saturation, triangle
            float saturation = saturate(min(IN.uv_MainTex.y, 1 - IN.uv_MainTex.y) * 3);
            float value = smoothstep(0, 1, saturate(IN.uv_MainTex.y * 2)); // 0-1 for half, then steady at 1

            // back to linear color space
            float4 c = pow(HSVtoRGB(float3(hue, saturation, value)), 2.2);
            o.Emission = c;
            o.Albedo = c.rgb;
            o.Metallic = _Metallic;
            o.Smoothness = _Glossiness;
            o.Alpha = 1.0;
        }
        ENDCG
    }
    FallBack "Diffuse"
}
