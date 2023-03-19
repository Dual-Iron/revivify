namespace Revivify;

enum AnimationStage
{
    None,
    Prepared,

    // -- Compressions --
    CompressionDown, // moving hands down on chest
    CompressionUp, // moving hands up from chest
    CompressionRest, // stillness
}
