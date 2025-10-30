using UnityEngine;
using System.Collections.Generic;

[RequireComponent(typeof(ParkourController))]
public class PathTracker : MonoBehaviour {
    public float sampleHz = 30f;
    public float rdpEps = 0.3f;  // 轨迹简化容忍
    public float kLen = 1f;      // 长度成本权重
    public float kAng = 0.3f;    // 角度惩罚权重

    List<Vector3> raw = new();
    float sampleTimer;
    bool recording;

    public float LastSegmentCost { get; private set; }
    public float TotalCost       { get; private set; }

    void Update(){
        if (!recording) return;
        sampleTimer += Time.deltaTime;
        if (sampleTimer >= 1f / sampleHz){
            sampleTimer = 0f;
            raw.Add(transform.position);
        }
    }

    public void BeginDashSegment(Vector3 startPos){
        recording = true;
        raw.Clear();
        raw.Add(startPos);
        sampleTimer = 0f;
    }

    public void SampleDuringDash(Vector3 pos){
        // 如需更密，可直接 Add
    }

    public void EndDashSegment(Vector3 endPos, bool finalize){
        if (!recording) return;
        recording = false;
        raw.Add(endPos);
        if (!finalize || raw.Count < 2) return;

        var pts = RDP(raw, rdpEps);
        var (len, ang) = Eval(pts);
        LastSegmentCost = kLen * len + kAng * ang;
        TotalCost      += LastSegmentCost;

        LinkManager.Instance?.SpawnLink(pts.ToArray(), LastSegmentCost, 2.5f);
        UIHud.Instance?.SetCost(LastSegmentCost, TotalCost);
    }

    (float L, float A) Eval(List<Vector3> pts){
        if (pts.Count < 2) return (0,0);
        float L=0,A=0;
        Vector3? prevDir=null;
        for (int i=0;i<pts.Count-1;i++){
            var d   = pts[i+1] - pts[i];
            float s = d.magnitude;
            if (s < 1e-4f) continue;
            L += s;
            var dir = d / s;
            if (prevDir.HasValue){
                float cos = Mathf.Clamp(Vector3.Dot(prevDir.Value, dir), -1f, 1f);
                A += (1f - cos);
            }
            prevDir = dir;
        }
        return (L,A);
    }

    List<Vector3> RDP(List<Vector3> pts, float eps){
        if (pts.Count <= 2) return new List<Vector3>(pts);
        return RDPRec(pts, 0, pts.Count-1, eps);
    }
    List<Vector3> RDPRec(List<Vector3> pts, int start, int end, float eps){
        float maxDist=0f; int idx=-1;
        Vector3 a=pts[start], b=pts[end];
        for (int i=start+1;i<end;i++){
            float d = PerpDist(pts[i], a, b);
            if (d>maxDist){ maxDist=d; idx=i; }
        }
        if (maxDist>eps && idx!=-1){
            var left = RDPRec(pts, start, idx, eps);
            var right= RDPRec(pts, idx, end, eps);
            left.RemoveAt(left.Count-1);
            left.AddRange(right);
            return left;
        }else{
            return new List<Vector3>{ a, b };
        }
    }
    float PerpDist(Vector3 p, Vector3 a, Vector3 b){
        if ((b-a).sqrMagnitude < 1e-6f) return Vector3.Distance(p,a);
        return Vector3.Cross(b-a, p-a).magnitude / (b-a).magnitude;
    }
}
