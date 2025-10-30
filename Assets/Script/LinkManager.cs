using UnityEngine;

public class LinkManager : MonoBehaviour {
    public static LinkManager Instance;
    public Material lineMat;
    public float baseWidth = 0.06f;

    void Awake(){ Instance = this; }

    public void SpawnLink(Vector3[] points, float cost, float life){
        var go = new GameObject("RepairLink");
        var lr = go.AddComponent<LineRenderer>();
        lr.material = lineMat;
        lr.positionCount = points.Length;
        lr.SetPositions(points);
        lr.useWorldSpace = true;
        lr.numCapVertices = 4;

        float width = Mathf.Clamp(1f / (cost + 0.5f), 0.02f, 0.25f) * baseWidth;
        lr.widthMultiplier = width;

        Color c = Color.Lerp(Color.red, Color.cyan, Mathf.Clamp01(1f / (cost + 0.5f)));
        lr.startColor = lr.endColor = c;

        GameObject.Destroy(go, life);
    }
}
