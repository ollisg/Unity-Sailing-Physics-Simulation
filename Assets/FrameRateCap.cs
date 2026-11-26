using UnityEngine;

/// <summary>
/// Stops the game running at hundreds of frames per second when VSync is off, which makes
/// a laptop run hot for no visible benefit. Applies automatically at start-up.
/// Change TargetFps, or delete this file to remove the cap.
/// </summary>
public static class FrameRateCap
{
    public const int TargetFps = 60;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
    static void Apply()
    {
        // With VSync on, the display already limits the frame rate
        if (QualitySettings.vSyncCount == 0)
            Application.targetFrameRate = TargetFps;
    }
}
