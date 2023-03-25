namespace Revivify;

sealed class PlayerData
{
    public bool exhausted;

    public int animTime;
    public float compressionDepth; // serves as an indicator for how effective the compression was

    public int expireTime;
    public float waterInLungs;
    public int compressionsUntilBreath;
    public int lastCompression; // clock
    public int deaths;
    public float deathTime; // Ranges from -1 to 1 and starts at 0

    public bool Expired => expireTime > Options.CorpseExpiryTime.Value * 60 * 40;

    public void Unprepared() => animTime = -1;
    public void PreparedToGiveCpr() => animTime = 0;
    public void StartCompression() => animTime = 1;

    public AnimationStage Stage()
    {
        if (animTime < 0) {
            return AnimationStage.None;
        }
        if (animTime < 1) {
            return AnimationStage.Prepared;
        }
        return (animTime % 20) switch {
            < 3 => AnimationStage.CompressionDown,
            < 6 => AnimationStage.CompressionUp,
            _ => AnimationStage.CompressionRest
        };
    }
}
