Shader "Custom/StateScroll"
{
    Properties
    {
        _MainTex ("Texture", 2D) = "white" {}
        _ScrollSpeed("Scroll Speed", Float) = 0.01
    }
	SubShader
	{
	   Lighting Off
	   Blend One Zero

	   Pass
	   {
		   CGPROGRAM
		   #include "UnityCustomRenderTexture.cginc"
		   #pragma vertex CustomRenderTextureVertexShader
		   #pragma fragment frag
			#pragma target 3.0

		   float4      _Color;
		   sampler2D   _MainTex;
		   float _ScrollSpeed;

		   float4 frag(v2f_customrendertexture IN) : COLOR
		   {
			   IN.localTexcoord.x += IN.localTexcoord.x * _ScrollSpeed;
			   return tex2D(_MainTex, IN.localTexcoord.xy);
		   }
		   ENDCG
	   }
	}
}
