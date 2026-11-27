using UnityEngine;

/// <summary>
/// Drives the sailor's pose through the Sailor animator controller.
/// hike: 0 = SitClose, 0.5 = SitFar, 1 = SitLean (fully hiked); values in between blend.
/// duck: crossfades to the Duck pose (e.g. while tacking under the boom) and back.
/// Set these from the simulation, or move the slider in the inspector during Play mode.
/// </summary>
[RequireComponent(typeof(Animator))]
public class SailorPose : MonoBehaviour
{
    [Tooltip("0 = SitClose, 0.5 = SitFar, 1 = SitLean (fully hiked)")]
    [Range(0f, 1f)] public float hike;

    [Tooltip("Crossfade to the Duck pose")]
    public bool duck;

    [Tooltip("Seconds used to smooth changes in hike (0 = instant)")]
    [Min(0f)] public float hikeDamp = 0.3f;

    static readonly int HikeId = Animator.StringToHash("Hike");
    static readonly int DuckId = Animator.StringToHash("Duck");

    Animator anim;

    void Awake()
    {
        anim = GetComponent<Animator>();
    }

    void Update()
    {
        if (anim.runtimeAnimatorController == null) return;

        if (hikeDamp > 0f)
            anim.SetFloat(HikeId, hike, hikeDamp, Time.deltaTime);
        else
            anim.SetFloat(HikeId, hike);

        anim.SetBool(DuckId, duck);
    }
}
