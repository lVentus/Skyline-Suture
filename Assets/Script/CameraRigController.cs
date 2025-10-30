using UnityEngine;

public class CameraRigController : MonoBehaviour {
    [Header("Refs")]
    public Transform target;   // Player
    public Transform cam;      // MainCamera

    [Header("Orbit")]
    public float yawSpeed = 180f;      // 右摇杆水平
    public float pitchSpeed = 120f;    // 右摇杆垂直
    public float minPitch = -20f;
    public float maxPitch = 60f;
    public float distance = 4.5f;
    public Vector3 pivotOffset = new Vector3(0, 1.6f, 0);

    [Header("Align (Wall Leave, etc.)")]
    public float alignYawOmega = 540f; // 其它对齐用的最大角速度（度/秒）
    private bool aligning = false;
    private Vector3 targetFlatForward;

    [Header("Impact Pre-Align (falling before wall)")]
    public float preAlignMaxDuration = 0.2f; // 约 200ms 完成
    bool preAlignActive = false;
    float preAlignTimeLeft;
    float yaw, pitch;
    float targetYawForPreAlign;

    void Start(){
        Vector3 fwd = transform.forward;
        yaw   = Mathf.Atan2(fwd.x, fwd.z) * Mathf.Rad2Deg;
        pitch = 10f;
    }

    public void ManualUpdate(float lookX, float lookY){
        // 玩家输入
        if (!aligning){
            yaw   += lookX * yawSpeed   * Time.deltaTime;
            pitch -= lookY * pitchSpeed * Time.deltaTime;
            pitch  = Mathf.Clamp(pitch, minPitch, maxPitch);
            AlignYawByETA(); // 落墙预对齐
        } else {
            // 其它对齐（如从墙起冲≥30°矫正后平滑对齐）
            yaw   += lookX * 0.25f * yawSpeed   * Time.deltaTime;
            pitch -= lookY * 0.25f * pitchSpeed * Time.deltaTime;
            pitch  = Mathf.Clamp(pitch, minPitch, maxPitch);
            AlignYawStep();
            AlignYawByETA();
        }

        // 应用相机变换
        Quaternion rot = Quaternion.Euler(pitch, yaw, 0);
        transform.position = target.position + pivotOffset;
        transform.rotation = rot;
        cam.position = transform.position - transform.forward * distance;
        cam.rotation = transform.rotation;
    }

    // —— 通用“仅水平朝向”的平滑对齐 —— //
    public void AlignYawToDirection(Vector3 worldDir){
        Vector3 flat = Vector3.ProjectOnPlane(worldDir, Vector3.up).normalized;
        if (flat.sqrMagnitude < 1e-5f) return;
        targetFlatForward = flat;
        aligning = true;
    }

    void AlignYawStep(){
        Vector3 curFlat = new Vector3(Mathf.Sin(yaw * Mathf.Deg2Rad), 0, Mathf.Cos(yaw * Mathf.Deg2Rad));
        float delta = Vector3.SignedAngle(curFlat, targetFlatForward, Vector3.up);
        float step  = Mathf.Sign(delta) * Mathf.Min(Mathf.Abs(delta), alignYawOmega * Time.deltaTime);
        yaw += step;
        if (Mathf.Abs(delta) < 0.5f) aligning = false;
    }

    // —— 落墙前“按 ETA 完成”的预对齐（水平向） —— //
    public void StartImpactPreAlign(Vector3 targetWorldDir, float etaSeconds){
        Vector3 flat = Vector3.ProjectOnPlane(targetWorldDir, Vector3.up).normalized;
        if (flat.sqrMagnitude < 1e-5f) return;
        targetYawForPreAlign = Mathf.Atan2(flat.x, flat.z) * Mathf.Rad2Deg;
        preAlignTimeLeft     = Mathf.Clamp(etaSeconds, 0.08f, preAlignMaxDuration);
        preAlignActive       = true;
    }

    public void CancelImpactPreAlign(){ preAlignActive = false; }

    void AlignYawByETA(){
        if (!preAlignActive) return;
        if (preAlignTimeLeft <= 0f){ yaw = targetYawForPreAlign; preAlignActive = false; return; }
        float dt    = Time.deltaTime;
        float t     = Mathf.Clamp01(dt / preAlignTimeLeft);
        float delta = Mathf.DeltaAngle(yaw, targetYawForPreAlign);
        yaw        += delta * t;
        preAlignTimeLeft -= dt;
    }

    public Vector3 FlatForward => new Vector3(Mathf.Sin(yaw * Mathf.Deg2Rad), 0, Mathf.Cos(yaw * Mathf.Deg2Rad));
}
