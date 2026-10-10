using System;
using System.Collections;
using System.Collections.Generic;
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
        public int wrong_clicks;
        public float completion_time;
        public string[] found_hazards;
        public string[] missed_hazards;
    }

    [Header("API Endpoint Configuration")]
    [Tooltip("Target Laravel endpoint. Use http://127.0.0.1:8000 for PC Editor testing, or your computer's LAN IP / public URL when deploying to Meta Quest 3.")]
    [SerializeField] private string apiUrl = "http://127.0.0.1:8000/api/trainee-results";

    [Tooltip("Timeout in seconds before the request aborts if the server does not respond")]
    [SerializeField] private int requestTimeoutSeconds = 10;

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
    /// Dispatches session telemetry to the Laravel backend including misclick count and hazard names.
    /// Matches the parameter structure invoked by TrainingGameManager.cs.
    /// </summary>
    public void SendSessionResult(int score, int found, int missed, int wrong, float timeTaken, List<string> foundList, List<string> missedList)
    {
        string currentTrainee = PlayerPrefs.GetString("CurrentTrainee", "Faiz_Adham");

        TraineePayload payload = new TraineePayload
        {
            username = currentTrainee,
            score = score,
            hazards_found = found,
            hazards_missed = missed,
            wrong_clicks = wrong,
            completion_time = timeTaken,
            found_hazards = foundList != null ? foundList.ToArray() : Array.Empty<string>(),
            missed_hazards = missedList != null ? missedList.ToArray() : Array.Empty<string>()
        };

        StartCoroutine(PostResult(payload));
    }

    /// <summary>
    /// Fallback overload for calls that do not provide a wrong-click count.
    /// </summary>
    public void SendSessionResult(int score, int found, int missed, float timeTaken, List<string> foundList, List<string> missedList)
    {
        SendSessionResult(score, found, missed, 0, timeTaken, foundList, missedList);
    }

    private IEnumerator PostResult(TraineePayload payload)
    {
        string json = JsonUtility.ToJson(payload);

        using (UnityWebRequest request = new UnityWebRequest(apiUrl, "POST"))
        {
            byte[] bodyRaw = Encoding.UTF8.GetBytes(json);
            request.uploadHandler = new UploadHandlerRaw(bodyRaw);
            request.downloadHandler = new DownloadHandlerBuffer();
            request.timeout = requestTimeoutSeconds;
            request.SetRequestHeader("Content-Type", "application/json");
            request.SetRequestHeader("Accept", "application/json");

            yield return request.SendWebRequest();

            if (request.result != UnityWebRequest.Result.Success)
            {
                Debug.LogError($"[API Bridge] Error logging results: {request.error} | Response: {request.downloadHandler?.text}");
            }
            else
            {
                Debug.Log($"<color=green>[API Bridge] Trainee session successfully saved to Laravel DB: {request.downloadHandler.text}</color>");
            }
        }
    }
}