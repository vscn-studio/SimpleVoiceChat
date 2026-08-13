namespace SimpleVoiceChat.Integration;

internal static class DirectorVoiceCaptureRegion
{
    private const int ChunkSize = 32;

    internal static bool Contains(
        double x,
        double z,
        int dimension,
        double centerX,
        double centerZ,
        int centerDimension,
        int radiusChunks)
    {
        if (!double.IsFinite(x)
            || !double.IsFinite(z)
            || !double.IsFinite(centerX)
            || !double.IsFinite(centerZ)
            || dimension != centerDimension)
        {
            return false;
        }

        int chunkX = (int)Math.Floor(x / ChunkSize);
        int chunkZ = (int)Math.Floor(z / ChunkSize);
        int centerChunkX = (int)Math.Floor(centerX / ChunkSize);
        int centerChunkZ = (int)Math.Floor(centerZ / ChunkSize);
        int radius = Math.Clamp(radiusChunks, 0, 16);
        return Math.Abs(chunkX - centerChunkX) <= radius
            && Math.Abs(chunkZ - centerChunkZ) <= radius;
    }
}
