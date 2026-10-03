// Night Signal car paint: Universal Render Pipeline/Complex Lit (URP 17, Unity 6000.6; clear coat kept) with one addition — the signature paint
// swatches' flip tint, the colour a pearl or metallic paint shifts to at glancing angles (customization.json paintSwatches
// "flipTint"). Generated from the package's ComplexLit.shader / LitInput.hlsl by Tools/art/gen_carpaint.py; every pass is the
// package's own, only the CBUFFER gains _FlipColor/_FlipPower and the forward pass blends the albedo toward _FlipColor by
// a Fresnel term. Derived from Unity's URP package source (Unity Companion License).
#ifndef NIGHT_SIGNAL_CAR_PAINT_FORWARD_INCLUDED
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
