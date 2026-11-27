using UnityEngine;

/// <summary>
/// Anything that carries mass and moves relative to the boat (sailor, boom and sail).
/// BoatMassModel asks every source each physics step and combines them with the fixed hull.
/// </summary>
public abstract class MassSource : MonoBehaviour
{
    /// <summary>Add this object's mass to acc, expressed in the frame (framePos, frameRot).</summary>
    public abstract void Accumulate(ref MassAccumulator acc, Vector3 framePos, Quaternion frameRot);

    protected static Vector3 ToFrame(Vector3 world, Vector3 framePos, Quaternion frameRot) =>
        Quaternion.Inverse(frameRot) * (world - framePos);

    protected static Vector3 DirToFrame(Vector3 worldDir, Quaternion frameRot) =>
        Quaternion.Inverse(frameRot) * worldDir;
}
