import os, glob
os.chdir(os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__)))))  # the repository root
pkg = glob.glob("Library/PackageCache/com.unity.render-pipelines.universal@*/Shaders")[0]
out = "Assets/Art/Shaders/CarPaint"
os.makedirs(out, exist_ok=True)

lit = open(os.path.join(pkg, "ComplexLit.shader"), encoding="utf-8").read().replace("\r\n", "\n")
inp = open(os.path.join(pkg, "LitInput.hlsl"), encoding="utf-8").read().replace("\r\n", "\n")

HEADER = """// Night Signal car paint: Universal Render Pipeline/Complex Lit (URP 17, Unity 6000.6; clear coat kept) with one addition — the signature paint
// swatches' flip tint, the colour a pearl or metallic paint shifts to at glancing angles (customization.json paintSwatches
// "flipTint"). Generated from the package's ComplexLit.shader / LitInput.hlsl by Tools/art/gen_carpaint.py; every pass is the
// package's own, only the CBUFFER gains _FlipColor/_FlipPower and the forward pass blends the albedo toward _FlipColor by
// a Fresnel term. Derived from Unity's URP package source (Unity Companion License).
"""

# ---- input: LitInput with two more material properties (same layout in every pass, so the SRP Batcher and the GPU
# Resident Drawer keep working).
def rep(s, old, new, count=1):
    assert s.count(old) >= 1, old[:60]
    return s.replace(old, new, count)

inp = rep(inp, "half _DetailNormalMapScale;\nUNITY_TEXTURE_STREAMING_DEBUG_VARS;",
          "half _DetailNormalMapScale;\nhalf4 _FlipColor;\nhalf _FlipPower;\nUNITY_TEXTURE_STREAMING_DEBUG_VARS;")
inp = rep(inp, "    UNITY_DOTS_INSTANCED_PROP(float , _DetailNormalMapScale)\nUNITY_DOTS_INSTANCING_END(MaterialPropertyMetadata)",
          "    UNITY_DOTS_INSTANCED_PROP(float , _DetailNormalMapScale)\n    UNITY_DOTS_INSTANCED_PROP(float4, _FlipColor)\n    UNITY_DOTS_INSTANCED_PROP(float , _FlipPower)\nUNITY_DOTS_INSTANCING_END(MaterialPropertyMetadata)")
inp = rep(inp, "static float  unity_DOTS_Sampled_DetailNormalMapScale;",
          "static float  unity_DOTS_Sampled_DetailNormalMapScale;\nstatic float4 unity_DOTS_Sampled_FlipColor;\nstatic float  unity_DOTS_Sampled_FlipPower;")
inp = rep(inp, "    unity_DOTS_Sampled_DetailNormalMapScale = UNITY_ACCESS_DOTS_INSTANCED_PROP_WITH_DEFAULT(float , _DetailNormalMapScale);",
          "    unity_DOTS_Sampled_DetailNormalMapScale = UNITY_ACCESS_DOTS_INSTANCED_PROP_WITH_DEFAULT(float , _DetailNormalMapScale);\n"
          "    unity_DOTS_Sampled_FlipColor            = UNITY_ACCESS_DOTS_INSTANCED_PROP_WITH_DEFAULT(float4, _FlipColor);\n"
          "    unity_DOTS_Sampled_FlipPower            = UNITY_ACCESS_DOTS_INSTANCED_PROP_WITH_DEFAULT(float , _FlipPower);")
marker = "#define _DetailNormalMapScale   unity_DOTS_Sampled_DetailNormalMapScale"
if marker not in inp:
    import re
    m = re.search(r"#define _DetailNormalMapScale\s+unity_DOTS_Sampled_DetailNormalMapScale", inp)
    marker = m.group(0)
inp = rep(inp, marker, marker + "\n#define _FlipColor              unity_DOTS_Sampled_FlipColor\n#define _FlipPower              unity_DOTS_Sampled_FlipPower")
open(os.path.join(out, "CarPaintInput.hlsl"), "w", encoding="utf-8", newline="\n").write(HEADER + inp)

# ---- the forward pass: the package's fragment with the flip blend before lighting.
fwd = HEADER + r'''#ifndef NIGHT_SIGNAL_CAR_PAINT_FORWARD_INCLUDED
#define NIGHT_SIGNAL_CAR_PAINT_FORWARD_INCLUDED

#include "Packages/com.unity.render-pipelines.universal/Shaders/LitForwardPass.hlsl"

// The flip tint: toward _FlipColor as the surface turns away from the eye (_FlipColor.a = how far, 0 = plain Lit).
void ApplyFlipTint(inout SurfaceData surfaceData, InputData inputData)
{
    half facing = saturate(dot(normalize(inputData.normalWS), normalize(inputData.viewDirectionWS)));
    half flip = pow(1.0h - facing, max(_FlipPower, 0.5h)) * _FlipColor.a;
    surfaceData.albedo = lerp(surfaceData.albedo, _FlipColor.rgb, flip);
}

void CarPaintFragment(
    Varyings input
    , out half4 outColor : SV_Target0
#ifdef _WRITE_RENDERING_LAYERS
    , out uint outRenderingLayers : SV_Target1
#endif
)
{
    UNITY_SETUP_INSTANCE_ID(input);
    UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);

#if defined(_PARALLAXMAP)
#if defined(REQUIRES_TANGENT_SPACE_VIEW_DIR_INTERPOLATOR)
    half3 viewDirTS = input.viewDirTS;
#else
    half3 viewDirWS = GetWorldSpaceNormalizeViewDir(input.positionWS);
    half3 viewDirTS = GetViewDirectionTangentSpace(input.tangentWS, input.normalWS, viewDirWS);
#endif
    ApplyPerPixelDisplacement(viewDirTS, input.uv);
#endif

    SurfaceData surfaceData;
    InitializeStandardLitSurfaceData(input.uv, surfaceData);

#ifdef LOD_FADE_CROSSFADE
    LODFadeCrossFade(input.positionCS);
#endif

    InputData inputData;
    InitializeInputData(input, surfaceData.normalTS, inputData);
    SETUP_DEBUG_TEXTURE_DATA(inputData, UNDO_TRANSFORM_TEX(input.uv, _BaseMap));

    ApplyFlipTint(surfaceData, inputData);

#if defined(_DBUFFER)
    ApplyDecalToSurfaceData(input.positionCS, surfaceData, inputData);
#endif

    InitializeBakedGIData(input, inputData);

    half4 color = UniversalFragmentPBR(inputData, surfaceData);
    color.rgb = MixFog(color.rgb, inputData.fogCoord);
    color.a = OutputAlpha(color.a, IsSurfaceTypeTransparent());

    outColor = color;

#ifdef _WRITE_RENDERING_LAYERS
    outRenderingLayers = EncodeMeshRenderingLayer();
#endif
}

#endif
'''
open(os.path.join(out, "CarPaintForwardPass.hlsl"), "w", encoding="utf-8", newline="\n").write(fwd)

# ---- the shader: the package's passes, our input, our forward fragment.
sh = lit
sh = rep(sh, 'Shader "Universal Render Pipeline/Complex Lit"', 'Shader "Night Signal/Car Paint"')
sh = rep(sh, '        [ToggleUI] _ReceiveShadows("Receive Shadows", Float) = 1.0',
         '        // Signature paint flip tint (glancing-angle colour shift); alpha = strength, 0 = plain Lit.\n'
         '        _FlipColor("Flip Tint", Color) = (1,1,1,0)\n'
         '        _FlipPower("Flip Falloff", Range(0.5, 8.0)) = 2.5\n\n'
         '        [ToggleUI] _ReceiveShadows("Receive Shadows", Float) = 1.0')
sh = sh.replace('#include "Packages/com.unity.render-pipelines.universal/Shaders/LitInput.hlsl"', '#include "CarPaintInput.hlsl"')
sh = rep(sh, '#pragma fragment LitPassFragment', '#pragma fragment CarPaintFragment')
sh = rep(sh, '#include "Packages/com.unity.render-pipelines.universal/Shaders/LitForwardPass.hlsl"', '#include "CarPaintForwardPass.hlsl"')
sh = rep(sh, '    CustomEditor "UnityEditor.Rendering.Universal.ShaderGUI.LitShader"\n', '')
open(os.path.join(out, "CarPaint.shader"), "w", encoding="utf-8", newline="\n").write(HEADER + sh)
print("written", os.listdir(out), "from", pkg)
