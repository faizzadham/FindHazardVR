using System.Collections;
using System.Text;
using UnityEngine;
using UnityEngine.Networking;

public class LaravelApiBridge : MonoBehaviour
{
    public static LaravelApiBridge Instance { get; private set; }

    [System.Serializable]
    public class TraineePayload
    {
        public string username;
        public int score;
        public int hazards_found;
        public int hazards_missed;
        public float completion_time;
    }

    [Header("Backend API Configuration")]
    [Tooltip("API endpoint URL on your Laravel server")]
    // Use http://127.0.0.1:8000/api/trainee-results when testing in Unity Editor on your laptop.
    // Use your PC LAN IP (e.g., http://192.168.1.50:8000) or public URL when running on Meta Quest.
    public string apiUrl = "http://127.0.0.1:8000/api/trainee-results";

    private void Awake()
    {
        if (Instance != null && Instance != this)
        {
            Destroy(gameObject);
            return;
        }
        Instance = this;
    }

    /// <summary>
    /// Packages session metrics and triggers the HTTP POST coroutine.
    /// </summary>
    public void SendSessionResult(int score, int found, int missed, float timeTaken)
    {
        // Retrieve the username stored during the StartingPoint onboarding scene
        string registeredName = PlayerPrefs.GetString("CurrentTrainee", "Anonymous_Worker");

        TraineePayload payload = new TraineePayload
        {
            username = registeredName,
            score = score,
            hazards_found = found,
            hazards_missed = missed,
            completion_time = timeTaken
        };

        StartCoroutine(PostResultRoutine(payload));
    }

    private IEnumerator PostResultRoutine(TraineePayload payload)
    {
        string json = JsonUtility.ToJson(payload);

        using (UnityWebRequest request = new UnityWebRequest(apiUrl, "POST"))
        {
            byte[] bodyRaw = Encoding.UTF8.GetBytes(json);
            request.uploadHandler = new UploadHandlerRaw(bodyRaw);
            request.downloadHandler = new DownloadHandlerBuffer();
            request.SetRequestHeader("Content-Type", "application/json");
            request.SetRequestHeader("Accept", "application/json");

            yield return request.SendWebRequest();

            if (request.result != UnityWebRequest.Result.Success)
            {
                Debug.LogError($"[API Bridge] Failed to send trainee result: {request.error}");
            }
            else
            {
                Debug.Log($"[API Bridge] Successfully logged to Laravel DB: {request.downloadHandler.text}");
            }
        }
    }
}
