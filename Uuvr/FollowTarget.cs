using UnityEngine;

namespace Uuvr;

public class FollowTarget: UuvrBehaviour
{
#if CPP
    public FollowTarget(System.IntPtr pointer) : base(pointer)
    {
    }
#endif

    public Transform? Target;
    public Vector3 LocalPosition = Vector3.zero;
    public Quaternion LocalRotation = Quaternion.identity;

    private void Update()
    {
        SnapToTarget();
    }

    private void LateUpdate()
    {
        SnapToTarget();
    }

    protected override void OnBeforeRender()
    {
        SnapToTarget();
    }

#if MODERN
    protected override void OnBeginFrameRendering()
    {
        // After VrCamera RelativeTransform swaps parent rotation for the frame.
        SnapToTarget();
    }
#endif

    private void SnapToTarget()
    {
        if (Target == null) return;

        transform.position = Target.TransformPoint(LocalPosition);
        transform.rotation = Target.rotation * LocalRotation;
    }
}
