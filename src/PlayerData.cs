namespace Revivify;

sealed class PlayerData
{
    public int animationTime;
    public int lastHurt;
    public float lastHurtAmount;
    public bool died;
    public float death; // Ranges from -1 to 1 and starts at 0

    public bool DeadForReal => death > 1;

    public bool Compression => Stage() is AnimationStage.CompressionDown or AnimationStage.CompressionUp or AnimationStage.CompressionRest;
    public AnimationStage Stage()
    {
        if (animationTime < 40) {
            return AnimationStage.Chill;
        }
        // Compressions 0-9 are actual compressions
        int compressionCount = 0;//(animationTime - 40) / 20;
        if (compressionCount % 14 < 10) {
            return (animationTime % 20) switch {
                < 3 => AnimationStage.CompressionDown,
                < 6 => AnimationStage.CompressionUp,
                _ => AnimationStage.CompressionRest
            };
        }
        // Compressions 10-13 are breaths
        return (animationTime % 80) switch {
            < 20 => AnimationStage.BreathingIn,
            < 23 => AnimationStage.MeetingHeads,
            < 40 => AnimationStage.BreathingOut,
            < 60 => AnimationStage.BreathingInAgain,
            < 77 => AnimationStage.BreathingOutAgain,
            _ => AnimationStage.MovingBack
        };
    }
}
