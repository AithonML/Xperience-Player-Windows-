//---------------------------------------------------------------------------
// Direct2D1 Effect Shader Helpers (for fxc.exe /T ps_4_0 /D D2D_FULL_SHADER)
//---------------------------------------------------------------------------
#ifndef D2D_INPUT_COUNT
#define D2D_INPUT_COUNT 1
#endif

#define __D2D_DEFINE_PS_GLOBALS(inputIndex) \
    Texture2D<float4> InputTexture##inputIndex : register(t##inputIndex); \
    SamplerState InputSampler##inputIndex : register(s##inputIndex);

#if (D2D_INPUT_COUNT >= 1)
__D2D_DEFINE_PS_GLOBALS(0)
#endif
#if (D2D_INPUT_COUNT >= 2)
__D2D_DEFINE_PS_GLOBALS(1)
#endif
#if (D2D_INPUT_COUNT >= 3)
__D2D_DEFINE_PS_GLOBALS(2)
#endif
#if (D2D_INPUT_COUNT >= 4)
__D2D_DEFINE_PS_GLOBALS(3)
#endif

static float4 __d2dstatic_scenePos = float4(0, 0, 0, 0);

#if (D2D_INPUT_COUNT >= 1)
static float4 __d2dstatic_uv0 = float4(0, 0, 0, 0);
#endif
#if (D2D_INPUT_COUNT >= 2)
static float4 __d2dstatic_uv1 = float4(0, 0, 0, 0);
#endif
#if (D2D_INPUT_COUNT >= 3)
static float4 __d2dstatic_uv2 = float4(0, 0, 0, 0);
#endif
#if (D2D_INPUT_COUNT >= 4)
static float4 __d2dstatic_uv3 = float4(0, 0, 0, 0);
#endif

inline float4 D2DGetScenePosition()
{
    return __d2dstatic_scenePos;
}

#define D2DGetInputCoordinate(index)            __d2dstatic_uv##index
#define D2DGetInput(index)                      InputTexture##index.Sample(InputSampler##index, __d2dstatic_uv##index.xy)
#define D2DSampleInput(index, position)         InputTexture##index.Sample(InputSampler##index, position)
#define D2DSampleInputAtOffset(index, offset)   InputTexture##index.Sample(InputSampler##index, __d2dstatic_uv##index.xy + (offset) * __d2dstatic_uv##index.zw)
#define D2DSampleInputAtPosition(index, pos)    InputTexture##index.Sample(InputSampler##index, __d2dstatic_uv##index.xy + __d2dstatic_uv##index.zw * ((pos) - __d2dstatic_scenePos.xy))

#ifndef D2D_ENTRY
#define D2D_ENTRY main
#endif

#define CONCAT(a, b) a##b
#define D2D_PS_ENTRY(name) float4 CONCAT(name, _Impl)()

float4 CONCAT(D2D_ENTRY, _Impl)();

#if (D2D_INPUT_COUNT == 0)
float4 D2D_ENTRY(
    float4 pos : SV_POSITION,
    float4 scenePos : SCENE_POSITION) : SV_TARGET
{
    __d2dstatic_scenePos = scenePos;
    return CONCAT(D2D_ENTRY, _Impl)();
}
#elif (D2D_INPUT_COUNT == 1)
float4 D2D_ENTRY(
    float4 pos : SV_POSITION,
    float4 scenePos : SCENE_POSITION,
    float4 uv0 : TEXCOORD0) : SV_TARGET
{
    __d2dstatic_scenePos = scenePos;
    __d2dstatic_uv0 = uv0;
    return CONCAT(D2D_ENTRY, _Impl)();
}
#elif (D2D_INPUT_COUNT == 2)
float4 D2D_ENTRY(
    float4 pos : SV_POSITION,
    float4 scenePos : SCENE_POSITION,
    float4 uv0 : TEXCOORD0,
    float4 uv1 : TEXCOORD1) : SV_TARGET
{
    __d2dstatic_scenePos = scenePos;
    __d2dstatic_uv0 = uv0;
    __d2dstatic_uv1 = uv1;
    return CONCAT(D2D_ENTRY, _Impl)();
}
#endif
