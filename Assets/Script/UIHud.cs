using UnityEngine;
using TMPro;
using UnityEngine.UI;

public class UIHud : MonoBehaviour
{
    public static UIHud Instance;

    [Header("Text (optional)")]
    public TextMeshProUGUI line1, line2, line3;

    [Header("Energy Bar")]
    public Image energyBar;   // 在 Inspector 把 EnergyBar Image 拖进来

    void Awake()
    {
        Instance = this;
    }

    // ratio: 0~1
    public void SetEnergy(float ratio)
    {
        ratio = Mathf.Clamp01(ratio);

        // 蓝色能量条
        if (energyBar)
            energyBar.fillAmount = ratio;

        // 你要的话可以保留数字显示，也可以注释掉
        if (line1)
            line1.text = $"Energy: {(int)(ratio * 100)}%";
    }

    public void SetCost(float last, float total)
    {
        if (line2) line2.text = $"Last Cost: {last:F2}";
        if (line3) line3.text = $"Total: {total:F2}";
    }

    public void ShowHint(string msg)
    {
        if (line1) line1.text = msg;
    }
}
