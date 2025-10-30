using UnityEngine;
using TMPro;

public class UIHud : MonoBehaviour {
    public static UIHud Instance;
    public TextMeshProUGUI line1, line2, line3;

    void Awake(){ Instance = this; }

    public void SetEnergy(float ratio){
        if (line1) line1.text = $"Energy: {(int)(ratio*100)}%";
    }
    public void SetCost(float last, float total){
        if (line2) line2.text = $"Last Cost: {last:F2}";
        if (line3) line3.text = $"Total: {total:F2}";
    }
    public void ShowHint(string msg){
        if (line1) line1.text = msg;
    }
}
