namespace Revivify;

enum AnimationStage
{
    None,
    Prepared,

    // -- Compressions --
    CompressionDown, // moving hands down on chest
    CompressionUp, // moving hands up from chest
    CompressionRest, // stillness

    // -- Breaths --
    BreathingIn, // inhaling deeply
    MeetingHeads, // mwa
    BreathingOut, // facing down, eyes closed, on head. Reviving player's chest rises
    BreathingInAgain,
    BreathingOutAgain,
    MovingBack, // moving back into position
}
