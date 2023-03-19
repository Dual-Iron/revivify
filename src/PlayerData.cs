namespace Revivify;

sealed class PlayerData
{
    public int animTime;
    public float compressionDepth; // serves as an indicator for how effective the compression was

    public int compressionsUntilBreath;
    public int lastCompression; // clock
    public int deaths;
    public float deathTime; // Ranges from -1 to 1 and starts at 0

    public void Unprepared() => animTime = -1;
    public void PreparedToGiveCpr() => animTime = 0;
    public void StartCompression() => animTime = 1;
}
