using UnityEngine;

public class CameraRigController : MonoBehaviour
{
    [Header("References")]
    [Tooltip("Target to follow")]
    public Transform target;

    public Camera cam;

    [Header("Offsets")]
    [Tooltip("Default camera offset (local, relative to yaw/pitch). Z<0 = behind.")]
    public Vector3 defaultOffset = new Vector3(0f, 1.6f, -4.0f);

    [Tooltip("Wall-run offset base. X is horizontal shift, Z behind.")]
    public Vector3 wallRunOffset = new Vector3(2.5f, 1.6f, -4.0f);

    [Tooltip("Seconds to smoothly move offset when switching modes.")]
    public float transitionTime = 0.15f;

    [Header("Look")]
    [Tooltip("Look sensitivity (deg per second per input unit).")]
    public float sensitivity = 300f;

    [Tooltip("Pitch angle limits (deg).")]
    public Vector2 pitchLimits = new Vector2(-50f, 70f);

    [Tooltip("How fast the current yaw moves toward target yaw (1/sec).")]
    public float yawLerpSpeed = 12f;

    [Tooltip("How fast the current pitch moves toward target pitch (1/sec).")]
    public float pitchLerpSpeed = 12f;

    // orientation state
    float yaw;         // current yaw used for rendering
    float pitch;       // current pitch used for rendering
    float targetYaw;   // desired yaw
    float targetPitch; // desired pitch

    // offset interpolation
    Vector3 currentOffset;
    Vector3 targetOffset;

    void Reset()
    {
        if (!cam) cam = GetComponentInChildren<Camera>(true);
    }

    void Start()
    {
        if (!cam) cam = Camera.main;

        if (!target)
            Debug.LogWarning("[CameraRig] Target is null.");
        if (!cam)
            Debug.LogWarning("[CameraRig] Camera is null. Please assign a Camera or tag one as MainCamera.");

        float initialYaw = target ? target.eulerAngles.y : 0f;
        yaw = targetYaw = initialYaw;
        pitch = targetPitch = Mathf.Clamp(10f, pitchLimits.x, pitchLimits.y);

        currentOffset = defaultOffset;
        targetOffset  = defaultOffset;

        if (cam && cam.transform.parent == transform)
        {
            cam.transform.localPosition = Vector3.zero;
            cam.transform.localRotation = Quaternion.identity;
        }
    }

    /// Update target yaw/pitch using look input.
    public void ManualUpdate(float lookX, float lookY)
    {
        targetYaw   += lookX * sensitivity * Time.deltaTime;
        targetPitch -= lookY * sensitivity * Time.deltaTime;
        targetPitch  = Mathf.Clamp(targetPitch, pitchLimits.x, pitchLimits.y);
    }

    void LateUpdate()
    {
        if (!target || !cam) return;

        // Smooth yaw/pitch toward targets
        yaw   = Mathf.LerpAngle(yaw,   targetYaw,   Mathf.Clamp01(yawLerpSpeed   * Time.deltaTime));
        pitch = Mathf.Lerp     (pitch, targetPitch, Mathf.Clamp01(pitchLerpSpeed * Time.deltaTime));

        // Smooth offset toward target offset
        float t = (transitionTime > 1e-4f) ? Time.deltaTime / transitionTime : 1f;
        currentOffset = Vector3.Lerp(currentOffset, targetOffset, Mathf.Clamp01(t));

        Quaternion rot = Quaternion.Euler(pitch, yaw, 0f);
        Vector3    pos = target.position + rot * currentOffset;

        cam.transform.position = pos;
        cam.transform.rotation = rot;
    }

    /// Switch back to default camera offset (centered view).
    public void SetDefaultMode()
    {
        targetOffset = defaultOffset;
    }

    /// Switch into wall-run camera:
    /// - Yaw aligns to wallRunDir (XZ).
    /// - Offset shifts to a side based on wallIsRight.
    ///   wallIsRight = true  → wall on player right → put player near right edge.
    ///   wallIsRight = false → wall on player left  → put player near left edge.
    public void SetWallRunMode(Vector3 wallRunDir, bool wallIsRight)
    {
        if (!cam || !target) return;

        // Flat direction
        wallRunDir.y = 0f;
        if (wallRunDir.sqrMagnitude < 1e-6f)
            wallRunDir = FlatForward();

        wallRunDir.Normalize();

        // Target yaw = wall-run direction yaw (smoothed in LateUpdate)
        float yawWorld = Mathf.Atan2(wallRunDir.x, wallRunDir.z) * Mathf.Rad2Deg;
        targetYaw = yawWorld;

        // Decide which side to place the camera so that:
        // - when wall is on the right, the player should appear on the right edge (camera is a bit left)
        // - when wall is on the left, the player should appear on the left edge (camera is a bit right)
        float sideSign = wallIsRight ? -1f : +1f;

        float x = sideSign * Mathf.Abs(wallRunOffset.x);
        float y = defaultOffset.y;
        float z = wallRunOffset.z;

        targetOffset = new Vector3(x, y, z);
    }

    /// Gently pull the camera yaw toward a desired world-space direction (XZ).
    public void NudgeYawTowardsDirection(Vector3 dir, float maxDegreesDelta)
    {
        dir.y = 0f;
        if (dir.sqrMagnitude < 1e-6f) return;

        dir.Normalize();
        float desiredYaw = Mathf.Atan2(dir.x, dir.z) * Mathf.Rad2Deg;
        targetYaw = Mathf.MoveTowardsAngle(targetYaw, desiredYaw, maxDegreesDelta);
    }

    /// Flat (XZ) forward from camera.
    public Vector3 FlatForward()
    {
        if (!cam) return transform.forward;
        Vector3 f = cam.transform.forward;
        f.y = 0f;
        return (f.sqrMagnitude > 1e-6f) ? f.normalized : Vector3.forward;
    }

    /// Flat (XZ) right from camera.
    public Vector3 FlatRight()
    {
        if (!cam) return transform.right;
        Vector3 r = cam.transform.right;
        r.y = 0f;
        return (r.sqrMagnitude > 1e-6f) ? r.normalized : Vector3.right;
    }

    /// Full 3D camera forward.
    public Vector3 Forward3D()
    {
        if (!cam) return transform.forward;
        return cam.transform.forward;
    }
}
