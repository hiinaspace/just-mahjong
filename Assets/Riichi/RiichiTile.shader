Shader "Custom/RiichiTile"
{
    Properties
    {
        _Color ("Color", Color) = (1,1,1,1)
        _BackColor ("Back Color", Color) = (0, 1, 0, 1)
        _MainTex ("Tile Texture", 2D) = "white" {}
        _FaceTex ("Tile Face Texture Atlas", 2D) = "white" {}
        _NormalFaceTex ("Tile Normal Texture Atlas", 2D) = "white" {}
        _HeightFaceTex ("Tile Height Texture Atlas", 2D) = "white" {}
        _Glossiness ("Smoothness", Range(0,1)) = 0.5
        _FaceGlossiness ("Face Smoothness", Range(0,1)) = 0.5
        _Metallic ("Metallic", Range(0,1)) = 0.0
		_Parallax("Parallax", float) = 0
        [PerRendererData] _Tile ("Tile index", Int) = 0
    }
    SubShader
    {
		// the RiichiTile tag for the wacky ReplacementShader system in unity
        Tags {
            "RenderType"="Opaque"
            "RiichiTile"="Yes" 
        }
        LOD 200

        CGPROGRAM
        // Physically based Standard lighting model, and enable shadows on all light types
        #pragma surface surf Standard fullforwardshadows

        // Use shader model 3.0 target, to get nicer looking lighting
        #pragma target 3.0

        sampler2D _MainTex;
        sampler2D _FaceTex;
        sampler2D _NormalFaceTex;
        sampler2D _HeightFaceTex;

        struct Input
        {
            float2 uv_MainTex;
            float2 uv2_FaceTex;
            float4 tileFaceMask: COLOR; // red component = front face
            half3 viewDir;
        };

        half _Glossiness;
        half _FaceGlossiness;
        half _Metallic;
        fixed4 _Color;
        fixed4 _BackColor;
        float _Parallax;

        // Add instancing support for this shader. You need to check 'Enable Instancing' on materials that use the shader.
        // See https://docs.unity3d.com/Manual/GPUInstancing.html for more information about instancing.
        // #pragma instancing_options assumeuniformscaling
        UNITY_INSTANCING_BUFFER_START(Props)
            // put more per-instance properties here
            UNITY_DEFINE_INSTANCED_PROP(float, _Tile)
            UNITY_DEFINE_INSTANCED_PROP(fixed4, _BackColorOffset)
        UNITY_INSTANCING_BUFFER_END(Props)

        void surf (Input IN, inout SurfaceOutputStandard o)
        {
            // tiles are in a weird 7x6 grid
            // determine uv2 offset for specific tile face
	        float2 offset = float2(
              fmod(UNITY_ACCESS_INSTANCED_PROP(Props, _Tile), 7)/7.0,
              (5 - floor(UNITY_ACCESS_INSTANCED_PROP(Props, _Tile) / 7.0))/6.0);
            IN.uv2_FaceTex.x = IN.uv2_FaceTex.x / 7;
            IN.uv2_FaceTex.y = IN.uv2_FaceTex.y / 6;

            float height = tex2D(_HeightFaceTex, offset + IN.uv2_FaceTex).r;
            float2 parallaxOffset = ParallaxOffset(height, _Parallax, IN.viewDir);

            // tileFace on uv2s
            fixed4 tileFace = tex2D(_FaceTex, offset + IN.uv2_FaceTex + parallaxOffset);
            tileFace.rgb = tileFace.rgb * tileFace.a;
            fixed isFrontFace = IN.tileFaceMask.r; // dumb masking technique in tile model.

            // MainTex is a mask, black = top color, white = bottom color
            fixed4 mask = tex2D(_MainTex, IN.uv_MainTex);
            // fmod to clamp to 0-1
            fixed4 backColor = fmod(_BackColor + UNITY_ACCESS_INSTANCED_PROP(Props, _BackColorOffset), 1.0001);
            fixed4 background = lerp(_Color, backColor, mask);
            o.Albedo.rgb = lerp(background, tileFace, tileFace.a * isFrontFace);
            //o.Albedo.rgb = IN.tileFaceMask;

            // Metallic and smoothness come from slider variables
            o.Metallic = _Metallic;
            o.Smoothness = lerp(_FaceGlossiness, _Glossiness, height);
            o.Alpha = 1.0;

            o.Normal = UnpackNormal(tex2D(_NormalFaceTex, offset + IN.uv2_FaceTex + parallaxOffset));
        }
        ENDCG
    }
    FallBack "Diffuse"
}
