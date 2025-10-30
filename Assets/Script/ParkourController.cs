using UnityEngine;
using UnityEngine.InputSystem;

public enum MoveState { Normal, ReadyDash, Dashing, Falling, WallRun, HitStun }

[RequireComponent(typeof(CharacterController))]
public class ParkourController : MonoBehaviour {
    // ---------- Inspector Refs ----------
    [Header("Refs")]
    public CharacterController cc;
    public CameraRigController camRig;

    // ---------- Input State ----------
    [Header("Input")]
    public PlayerControls input;
    Vector2 moveInput, lookInput;
    bool dashPressedEdge;   // triangle pressed this frame
    bool dashHeld;          // triangle held
    bool jumpPressed;       // cross pressed this frame
    float r2Value;          // R2 analog value

    // ---------- Movement Tunables ----------
    [Header("Speeds")]
    public float walkSpeed = 6f;
    public float gravity = -20f;
    public float wallRunGravity = -4f;
    public float wallRunSpeedDecay = 1.8f;   // horizontal (tangent) only
    public float jumpImpulse = 8f;
    public float dashBurstSpeed = 24f;

    [Header("Dash Steering Limit (deg/s)")]
    public float omega0 = 360f, omegaK = 0.08f, omegaMin = 60f;

    [Header("Dash Speed Profile")]
    public float dashPlateauDelay = 0.35f;
    [Range(0.1f, 0.9f)] public float dashPlateauRatio = 0.55f;

    // ---------- Energy ----------
    [Header("Energy")]
    public float energyMax = 100f;
    public float dashDrainBase = 22f;
    public float dashDrainSpeedCoeff = 0.04f;
    public float regenRate = 6f;
    public float wallRegenRate = 10f;
    public float refundRatio = 0.7f;

    [Header("Energy Gates")]
    public float minEnergyToDash = 5f;          // normal gate
    public float rearmEnergyAfterDeplete = 50f; // must reach after deplete-to-zero
    bool needRearm = false;

    // ---------- Angles / Timings ----------
    [Header("Angles")]
    public float minLeaveSurfaceAngle = 30f;
    public float hitStunTime = 1.0f;

    // ---------- Wall Probe ----------
    [Header("Wall Probe")]
    public LayerMask wallMask = ~0;
    public float wallStickDist = 1.0f;
    public float minWallRunSpeed = 0.2f;
    public float wallCornerMaxAngle = 25f; // max allowed change while keeping wallrun

    // ---------- Falling Pre-Align ----------
    [Header("Pre-Align (falling)")]
    public float preAlignProbeDist = 3.0f;
    public float preAlignMaxWindow = 0.22f;

    // ---------- Runtime State ----------
    public MoveState state = MoveState.Normal;
    Vector3 velocity;
    Vector3 dashDir;
    float   dashSpeed;
    float   energy;
    bool    canRefund;
    bool    hasDoubleJump;
    float   hitStunTimer;
    Vector3 wallNormal;

    // ---------- Carry-over Speed Bonus ----------
    float landingSpeed;
    [Header("Bonus (landingSpeed)")]
    public float bonusMax = 10f;
    public float bonusK = 0.08f;
    public float bonusMin = 0f;

    // ---------- Dash Timers ----------
    float dashTimer, dashBurst, dashPlateau;

    // ---------- Path Tracker ----------
    PathTracker tracker;

    // ============================ LIFECYCLE ============================

    ///  Cache refs, create InputActions, init energy/UI. 
    void Awake(){
        cc ??= GetComponent<CharacterController>();
        tracker = GetComponent<PathTracker>();
        input = new PlayerControls();
        energy = energyMax;
        UpdateEnergyUI();
    }

    ///  Bind input callbacks and enable the input map. 
    void OnEnable(){
        input.Enable();

        input.Gameplay.Move.performed += ctx => moveInput = ctx.ReadValue<Vector2>();
        input.Gameplay.Move.canceled  += _   => moveInput = Vector2.zero;

        input.Gameplay.Look.performed += ctx => lookInput = ctx.ReadValue<Vector2>();
        input.Gameplay.Look.canceled  += _   => lookInput = Vector2.zero;

        input.Gameplay.Dash.started   += _   => { dashPressedEdge = true; dashHeld = true; };
        input.Gameplay.Dash.performed += _   => { dashHeld = true; };
        input.Gameplay.Dash.canceled  += _   => { dashHeld = false; };

        input.Gameplay.Jump.performed += _   => jumpPressed = true;

        input.Gameplay.Runhold.performed += ctx => r2Value = ctx.ReadValue<float>();
        input.Gameplay.Runhold.canceled  += _   => r2Value = 0f;
    }

    ///  Disable input map. 
    void OnDisable(){ input.Disable(); }

    ///  Main update loop: read raw gamepad fallback, route camera input, tick FSM. 
    void Update(){
        // Raw gamepad fallback to avoid mapping issues
        if (Gamepad.current != null){
            if (!dashHeld && Gamepad.current.buttonNorth.isPressed) dashHeld = true;
            if ( dashHeld && !Gamepad.current.buttonNorth.isPressed) dashHeld = false;
            r2Value = Gamepad.current.rightTrigger.ReadValue();
        }

        // Camera input rule:
        // Hold R2 or in ReadyDash/WallRun → left stick controls camera; otherwise right stick.
        float lookX, lookY;
        if (IsR2Held() || state == MoveState.ReadyDash || state == MoveState.WallRun){
            lookX = moveInput.x; lookY = moveInput.y;
        } else {
            lookX = lookInput.x; lookY = lookInput.y;
        }
        camRig.ManualUpdate(lookX, lookY);

        // FSM dispatch
        switch(state){
            case MoveState.Normal:   TickNormal();   break;
            case MoveState.ReadyDash:TickReadyDash();break;
            case MoveState.Dashing:  TickDashing();  break;
            case MoveState.Falling:  TickFalling();  break;
            case MoveState.WallRun:  TickWallRun();  break;
            case MoveState.HitStun:  TickHitStun();  break;
        }

        dashPressedEdge = false;
        jumpPressed = false;
    }

    // ============================ STATE TICKS ============================

    ///  Grounded locomotion; enter ReadyDash when R2 held; gravity if walked off ledge. 
    void TickNormal(){
        MoveGround();
        if (IsR2Held()) state = MoveState.ReadyDash;
        Recover(regenRate);
        if (!cc.isGrounded){ state = MoveState.Falling; hasDoubleJump = true; }
    }

    ///  Slow ground move to aim; dash if R2 + triangle; fall if no longer grounded. 
    void TickReadyDash(){
        MoveGround(0.25f);
        if (IsR2Held() && (dashPressedEdge || dashHeld) && CanStartDash()){
            StartDash_FromFalling();
        }
        if (!IsR2Held()) state = MoveState.Normal;
        Recover(regenRate);
        if (!cc.isGrounded){ state = MoveState.Falling; hasDoubleJump = true; }
    }

    ///  Dash with capped angular steering; drain energy; end if released or depleted. 
    void TickDashing(){
        if (!dashHeld){
            EnterFalling(refund:true, fromDeplete:false);
            return;
        }

        DashSteerWithAngularLimit();

        dashTimer += Time.deltaTime;
        dashSpeed = (dashTimer < dashPlateauDelay) ? dashBurst : dashPlateau;

        velocity = dashDir * dashSpeed;
        Vector3 step = velocity * Time.deltaTime;

        if (CapsuleCastAhead(step, out RaycastHit hit)){
            EnterHitStun(hit);
            return;
        }
        cc.Move(step);

        float drain = dashDrainBase * (1f + dashDrainSpeedCoeff * dashSpeed) * Time.deltaTime;
        energy = Mathf.Max(0f, energy - drain);
        UpdateEnergyUI();

        if (energy <= 0f){
            energy = 0f;
            EnterFalling(refund:false, fromDeplete:true);
            return;
        }

        tracker.SampleDuringDash(transform.position);
    }

    ///  Hit-stun: apply gravity; when timer ends, go Normal if grounded else Falling. 
    void TickHitStun(){
        hitStunTimer -= Time.deltaTime;
        velocity.y += gravity * Time.deltaTime;
        cc.Move(velocity * Time.deltaTime);

        if (hitStunTimer <= 0f){
            if (cc.isGrounded){
                velocity = Vector3.zero;
                state = MoveState.Normal;
            } else {
                state = MoveState.Falling;
            }
        }
    }

    ///  Airborne: early dash check, pre-align camera to incoming wall, gravity arc, wall latch. 
    void TickFalling(){
        // Early chance to dash in-air (R2 + triangle + energy gate)
        if (IsR2Held() && (dashPressedEdge || dashHeld) && CanStartDash()){
            if (velocity.y > 0.1f) StartDash_FromDoubleJumpAscend();
            else StartDash_FromFalling();
            return;
        }

        // Camera pre-align (~200ms window) toward reflected direction on the incoming wall
        if (PredictWallImpact(preAlignProbeDist, out RaycastHit preHit, out float eta)){
            Vector3 horizVel = new Vector3(velocity.x, 0, velocity.z);
            Vector3 reflect  = ReflectDir(horizVel, preHit.normal);
            camRig.StartImpactPreAlign(reflect, eta);
        } else {
            camRig.CancelImpactPreAlign();
        }

        Recover(regenRate * 2f);

        // Parabolic: keep horizontal, apply gravity
        velocity.y += gravity * Time.deltaTime;
        cc.Move(velocity * Time.deltaTime);

        // Optional double jump
        if (jumpPressed && hasDoubleJump){
            hasDoubleJump = false;
            velocity.y = jumpImpulse;
        }

        // Latch to wall (no corner limit when entering from falling)
        if (IsR2Held() && ProbeWall(out Vector3 n, limitCorner:false)){
            EnterWallRun(n);
            camRig.CancelImpactPreAlign();
            return;
        }

        // Land on ground
        if (cc.isGrounded){
            RecordLandingSpeed(new Vector3(velocity.x, 0, velocity.z));
            if (canRefund) RefundEnergy();
            camRig.CancelImpactPreAlign();

            velocity = Vector3.zero;
            state = MoveState.Normal;
        }
    }

    ///  Wall-run: keep contact with limited corner angle; decay tangent speed; fall when lost. 
    void TickWallRun(){
        if (!IsR2Held()){
            state = MoveState.Falling;
            return;
        }
        if (!ProbeWall(out Vector3 n, limitCorner:true)){
            state = MoveState.Falling;
            return;
        }
        wallNormal = n;

        // Only decay horizontal tangent; vertical uses lighter gravity
        Vector3 tangential = Vector3.ProjectOnPlane(velocity, wallNormal);
        float speed = tangential.magnitude;
        speed = Mathf.Max(0f, speed - wallRunSpeedDecay * Time.deltaTime);
        Vector3 tangentialDir = (speed > 1e-4f) ? tangential.normalized : Vector3.zero;

        velocity.x = tangentialDir.x * speed;
        velocity.z = tangentialDir.z * speed;
        velocity.y += wallRunGravity * Time.deltaTime;

        cc.Move(velocity * Time.deltaTime);

        Recover(wallRegenRate);

        // Auto fall when tangent speed exhausted
        if (speed <= minWallRunSpeed){
            state = MoveState.Falling;
            return;
        }

        // Dash off wall (angle constraint handled later)
        if ((dashPressedEdge || dashHeld) && CanStartDash()){
            StartDash_FromWallRunOrRecentLanding();
        }
    }

    // ============================ DASH STARTERS ============================

    ///  Start dash from wall-run or recent wall landing, with min 30° away from wall normal. 
    void StartDash_FromWallRunOrRecentLanding(){
        dashSpeed = landingSpeed + Bonus(landingSpeed);
        Vector3 desired = camRig.transform.forward;
        bool adjusted;
        Vector3 constrained = ConstrainDashDirFromWall(desired, wallNormal, out adjusted);
        dashDir = constrained.normalized;
        BeginDashCommon();
    }

    ///  Start dash from falling/ground using fixed burst speed, towards camera forward (3D). 
    void StartDash_FromFalling(){
        dashSpeed = dashBurstSpeed;
        dashDir   = camRig.transform.forward.normalized;
        BeginDashCommon();
    }

    ///  Start dash while ascending (after double-jump): max of burst vs currentHoriz+Bonus. 
    void StartDash_FromDoubleJumpAscend(){
        float currentHoriz = new Vector3(velocity.x, 0, velocity.z).magnitude;
        dashSpeed = Mathf.Max(dashBurstSpeed, currentHoriz + Bonus(currentHoriz));
        dashDir   = camRig.transform.forward.normalized;
        BeginDashCommon();
    }

    ///  Common init when entering dash: timers, speeds, UI/camera cleanup, clear rearm flag. 
    void BeginDashCommon(){
        state = MoveState.Dashing;
        tracker.BeginDashSegment(transform.position);
        canRefund = false;

        dashTimer   = 0f;
        dashBurst   = dashSpeed;
        dashPlateau = dashBurst * dashPlateauRatio;

        velocity = dashDir * dashSpeed;
        camRig.CancelImpactPreAlign();

        needRearm = false; // passed gate → clear rearm lock
    }

    // ============================ ENTER/EXIT HELPERS ============================

    ///  Exit to Falling, keeping current velocity; flag rearm if depleted; allow refund toggle. 
    void EnterFalling(bool refund, bool fromDeplete){
        state = MoveState.Falling;
        tracker.EndDashSegment(transform.position, finalize:true);
        canRefund = refund;
        hasDoubleJump = true;
        if (fromDeplete) needRearm = true;
        // keep velocity
    }

    ///  Enter WallRun: record landing speed, give flat +30 energy, set wall normal. 
    void EnterWallRun(Vector3 n){
        wallNormal = n;
        Vector3 tangential = Vector3.ProjectOnPlane(velocity, n);
        RecordLandingSpeed(tangential);
        state = MoveState.WallRun;

        // Fixed +30 energy on wall-landing
        GainEnergyFlat(30f);
        canRefund = false;
    }

    ///  Enter HitStun: zero velocity, start timer, end dash segment. 
    void EnterHitStun(RaycastHit hit){
        state = MoveState.HitStun;
        hitStunTimer = hitStunTime;
        velocity = Vector3.zero;
        tracker.EndDashSegment(transform.position, finalize:true);
        canRefund = false;
    }

    // ============================ UTILS: INPUT / ENERGY / MOVE ============================

    ///  True if R2 is considered held (trigger threshold). 
    bool IsR2Held() => r2Value > 0.5f;

    ///  Energy gate: if last dash depleted to 0, require rearmEnergy; else minEnergyToDash. 
    bool CanStartDash(){
        float gate = needRearm ? rearmEnergyAfterDeplete : minEnergyToDash;
        return energy >= gate;
    }

    ///  Update HUD energy bar (0..1). 
    void UpdateEnergyUI() => UIHud.Instance?.SetEnergy(energy / energyMax);

    ///  Standard ground locomotion aligned to camera forward; applies gravity when airborne. 
    void MoveGround(float multiplier = 1f){
        Vector3 fwd   = new Vector3(camRig.transform.forward.x, 0f, camRig.transform.forward.z).normalized;
        Vector3 right = Vector3.Cross(Vector3.up, fwd);
        Vector3 dir   = (fwd * moveInput.y + right * moveInput.x).normalized;
        Vector3 v     = dir * walkSpeed * multiplier;
        velocity.x = v.x; velocity.z = v.z;

        if (cc.isGrounded){
            velocity.y = -2f;
            if (jumpPressed){ velocity.y = jumpImpulse; hasDoubleJump = true; }
        } else {
            velocity.y += gravity * Time.deltaTime;
        }
        cc.Move(velocity * Time.deltaTime);
    }

    ///  Passive energy regeneration by rate (clamped to max). 
    void Recover(float rate){
        energy = Mathf.Min(energyMax, energy + rate * Time.deltaTime);
        UpdateEnergyUI();
    }

    ///  Gain a flat amount of energy (clamped to max). 
    void GainEnergyFlat(float amount){
        energy = Mathf.Min(energyMax, energy + amount);
        UpdateEnergyUI();
    }

    ///  Refund a ratio chunk (used on some landings by previous design; kept for ground). 
    void RefundEnergy(){
        energy = Mathf.Min(energyMax, energy + energyMax * 0.15f * refundRatio);
        UpdateEnergyUI();
        canRefund = false;
    }

    ///  Update landingSpeed (smoothed) from tangent/horizontal component. 
    void RecordLandingSpeed(Vector3 tangentOrHoriz){
        float v = tangentOrHoriz.magnitude * 0.9f;
        landingSpeed = Mathf.Max(landingSpeed * 0.5f + v * 0.5f, v);
    }

    // ============================ UTILS: PHYSICS / PROBES ============================

    ///  Capsule cast ahead by step to detect impact during dash. 
    bool CapsuleCastAhead(Vector3 step, out RaycastHit hit){
        Vector3 p1 = transform.position + Vector3.up * (cc.radius);
        Vector3 p2 = transform.position + Vector3.up * (cc.height - cc.radius);
        return Physics.CapsuleCast(p1, p2, cc.radius * 0.95f, step.normalized, out hit, step.magnitude + 0.05f, wallMask);
    }

    ///  Robust wall detection: choose best contact; optionally limit corner angle while wallrunning. 
    bool ProbeWall(out Vector3 n, bool limitCorner){
        n = Vector3.zero;

        Vector3 originTop    = transform.position + Vector3.up * (cc.height - cc.radius);
        Vector3 originBottom = transform.position + Vector3.up * (cc.radius);
        float   radius       = cc.radius * 0.95f;

        Vector3 horizVel = new Vector3(velocity.x, 0f, velocity.z);
        Vector3[] dirs = new Vector3[]{
            (wallNormal.sqrMagnitude>0.001f ? -wallNormal.normalized : transform.forward),
            (horizVel.sqrMagnitude>1e-4f ? horizVel.normalized : transform.forward),
            transform.forward, -transform.forward,
            transform.right, -transform.right
        };

        RaycastHit bestHit = default;
        bool found = false;
        float bestScore = float.NegativeInfinity;

        foreach (var d in dirs){
            if (Physics.CapsuleCast(originBottom, originTop, radius, d, out RaycastHit hit, wallStickDist, wallMask)
                || Physics.Raycast(originTop, d, out hit, wallStickDist, wallMask))
            {
                Vector3 candidateN = hit.normal;
                if (limitCorner && wallNormal.sqrMagnitude > 0.001f){
                    float ang = Vector3.Angle(candidateN, wallNormal);
                    if (ang > wallCornerMaxAngle) continue;
                }
                float alignment = (wallNormal.sqrMagnitude>0.001f) ? Vector3.Dot(candidateN.normalized, wallNormal.normalized) : 1f;
                float distanceScore = -hit.distance;
                float score = alignment * 2f + distanceScore;
                if (score > bestScore){
                    bestScore = score;
                    bestHit = hit;
                    found = true;
                }
            }
        }

        if (found){
            n = bestHit.normal;
            return true;
        }
        return false;
    }

    ///  Constrain dash direction to leave wall by at least min angle (>=30°), keep facing outward. 
    Vector3 ConstrainDashDirFromWall(Vector3 desiredDir, Vector3 wallN, out bool adjusted){
        adjusted = false;
        Vector3 d = desiredDir.normalized;
        Vector3 nrm = wallN.normalized;

        if (Vector3.Dot(d, nrm) <= 0f){
            d = Vector3.ProjectOnPlane(d, nrm);
            d = (d + 0.001f * nrm).normalized;
            adjusted = true;
        }
        float limitToNormal = 90f - minLeaveSurfaceAngle;
        float angToNormal   = Vector3.Angle(d, nrm);
        if (angToNormal > limitToNormal){
            float delta = angToNormal - limitToNormal;
            d = Vector3.RotateTowards(d, nrm, Mathf.Deg2Rad * delta, 0f).normalized;
            adjusted = true;
        }
        return d;
    }

    ///  Steer dashDir toward camera forward with speed-dependent angular cap (3D). 
    void DashSteerWithAngularLimit(){
        Vector3 target = camRig.transform.forward.normalized;
        float omegaMaxDeg = Mathf.Max(omegaMin, omega0 / (1f + omegaK * dashSpeed));
        float maxRad = Mathf.Deg2Rad * omegaMaxDeg * Time.deltaTime;
        dashDir = Vector3.RotateTowards(dashDir, target, maxRad, float.PositiveInfinity).normalized;
    }

    ///  Predict an incoming wall within a short ETA to start camera pre-align. 
    bool PredictWallImpact(float probeDist, out RaycastHit hit, out float eta){
        hit = default; eta = 999f;
        Vector3 horizVel = new Vector3(velocity.x, 0, velocity.z);
        Vector3 dir = (horizVel.sqrMagnitude > 1e-4f) ? horizVel.normalized : transform.forward;
        Vector3 p1 = transform.position + Vector3.up * (cc.radius);
        Vector3 p2 = transform.position + Vector3.up * (cc.height - cc.radius);
        if (Physics.CapsuleCast(p1, p2, cc.radius * 0.95f, dir, out hit, probeDist, wallMask)){
            float approach = Vector3.Dot(-horizVel, hit.normal);
            if (approach <= 0.1f) return false;
            float dist = hit.distance;
            float t    = dist / Mathf.Max(approach, 0.01f);
            if (t <= preAlignMaxWindow){
                eta = Mathf.Clamp(t, 0.08f, preAlignMaxWindow);
                return true;
            }
        }
        return false;
    }

    ///  Reflect a vector about a normal (fallback to camera flat forward when tiny). 
    Vector3 ReflectDir(Vector3 v, Vector3 n){
        if (v.sqrMagnitude < 1e-5f) return new Vector3(camRig.transform.forward.x, 0, camRig.transform.forward.z).normalized;
        return (v - 2f * Vector3.Dot(v, n) * n).normalized;
    }

    ///  Carry-over speed bonus: decays with higher landingSpeed, no hard cap. 
    float Bonus(float v){
        v = Mathf.Max(0f, v);
        return bonusMax * Mathf.Exp(-bonusK * v) + bonusMin;
    }
}
