using UnityEngine;
using UnityEngine.SceneManagement;

public class BriefingToWarehouse : MonoBehaviour
{
    [Header("Target Scene")]
    [Tooltip("Exact name of your warehouse scene in Build Settings")]
    public string warehouseSceneName = "Warehouse";

    [Header("Optional Fade")]
    public FadeScreen fadeScreen;

    public void LoadWarehouseScene()
    {
        Debug.Log($"[Briefing] Transitioning to {warehouseSceneName}...");

        if (fadeScreen != null)
        {
            fadeScreen.FadeToScene(warehouseSceneName);
        }
        else if (FadeScreen.Instance != null)
        {
            FadeScreen.Instance.FadeToScene(warehouseSceneName);
        }
        else
        {
            SceneManager.LoadScene(warehouseSceneName);
        }
    }
}