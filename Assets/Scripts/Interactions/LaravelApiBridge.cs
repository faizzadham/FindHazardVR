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
        public float completion_time;
        public string[] found_hazards;
        public string[] missed_hazards;
    }

    [SerializeField] private string apiUrl = "http://127.0.0.1:8000/api/trainee-results";

    private void Awake()
    {
        if (Instance != null && Instance != this) Destroy(gameObject);
        else Instance = this;
    }

    public void SendSessionResult(int score, int found, int missed, float timeTaken, List<string> foundList, List<string> missedList)
    {
        string currentTrainee = PlayerPrefs.GetString("CurrentTrainee", "Faiz_Adham");

        TraineePayload payload = new TraineePayload
        {
            username = currentTrainee,
            score = score,
            hazards_found = found,
            hazards_missed = missed,
            completion_time = timeTaken,
            found_hazards = foundList.ToArray(),
            missed_hazards = missedList.ToArray()
        };

        StartCoroutine(PostResult(payload));
    }

    private IEnumerator PostResult(TraineePayload payload)
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
                Debug.LogError("[API Bridge] Error logging results: " + request.error);
            }
            else
            {
                Debug.Log("[API Bridge] Logged to Laravel DB: " + request.downloadHandler.text);
            }
        }
    }
}