using System;
using System.Collections;
using System.Collections.Generic;
using System.Text;
using UnityEngine;
using UnityEngine.Networking;

public class LaravelApiBridge : MonoBehaviour
{
    private static LaravelApiBridge _instance;
    public static LaravelApiBridge Instance
    {
        get
        {
            if (_instance == null)
            {
                _instance = FindFirstObjectByType<LaravelApiBridge>();
                if (_instance == null)
                {
                    GameObject go = new GameObject("[LaravelApiBridge]");
                    _instance = go.AddComponent<LaravelApiBridge>();
                    DontDestroyOnLoad(go);
                }
            }
            return _instance;
        }
    }

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

    [System.Serializable]
    public class ActiveTraineePayload
    {
        public string username;
        public int hazards_found;
        public int hazards_missed;
        public int wrong_clicks;
        public string[] found_hazards;
        public float score;
        public float completion_time;
    }

    [Header("API Server Host Configuration")]
    [Tooltip("Primary LAN IP via Apache port 80 (Always allowed by firewall, works instantly over campus Wi-Fi)")]
    [SerializeField] private string primaryLanUrl = "http://10.101.129.153/laravel/FindHazardAdmin/public/api";

    [Tooltip("USB Reverse Tunnel localhost (Works instantly via ADB reverse over USB cable or ADB WiFi)")]
    [SerializeField] private string usbLocalhostUrl = "http://127.0.0.1:8000/api";

    [Tooltip("Timeout in seconds before an HTTP request times out")]
    [SerializeField] private int requestTimeoutSeconds = 3;

    // Caches the confirmed working URL to make all subsequent calls instant (0ms failover)
    private static string lastWorkingBaseUrl = null;

    private void Awake()
    {
        if (_instance == null)
        {
            _instance = this;
            if (transform.parent == null && GetComponent<TrainingGameManager>() == null)
            {
                DontDestroyOnLoad(gameObject);
            }
        }
        else if (_instance != this)
        {
            // Another persistent instance exists; destroy this duplicate component only (never the GameObject)
            Destroy(this);
        }
    }

    /// <summary>
    /// Returns ordered candidate URLs for a given API route (e.g. "active-trainee" or "trainee-results").
    /// </summary>
    private List<string> GetCandidateUrls(string endpoint)
    {
        List<string> list = new List<string>();

        // 1. If we already found a working URL in this session, prioritize it immediately
        if (!string.IsNullOrEmpty(lastWorkingBaseUrl))
        {
            list.Add(lastWorkingBaseUrl.TrimEnd('/') + "/" + endpoint);
        }

        // 2. USB Reverse Tunnel (127.0.0.1:8000) - Instantaneous (0.1ms) over USB connection
        string usbEndpoint = usbLocalhostUrl.TrimEnd('/') + "/" + endpoint;
        if (!list.Contains(usbEndpoint))
        {
            list.Add(usbEndpoint);
        }

        // 3. Localhost Apache Port 80 (Works on PC XR Simulator with XAMPP Apache)
        string localApacheEndpoint = "http://127.0.0.1/laravel/FindHazardAdmin/public/api/" + endpoint;
        if (!list.Contains(localApacheEndpoint))
        {
            list.Add(localApacheEndpoint);
        }

        // 4. Direct Wi-Fi Apache Port 80 (Used for standalone Quest 3 over Wi-Fi)
        string apacheEndpoint = primaryLanUrl.TrimEnd('/') + "/" + endpoint;
        if (!list.Contains(apacheEndpoint))
        {
            list.Add(apacheEndpoint);
        }

        return list;
    }

    /// <summary>
    /// Transmits candidate username to the Laravel Live Monitor.
    /// </summary>
    public void SendActiveTrainee(string username)
    {
        if (string.IsNullOrWhiteSpace(username)) return;

        PlayerPrefs.SetString("CurrentTrainee", username);
        PlayerPrefs.Save();

        ActiveTraineePayload payload = new ActiveTraineePayload
        {
            username = username,
            hazards_found = 0,
            hazards_missed = 13,
            wrong_clicks = 0,
            found_hazards = Array.Empty<string>(),
            score = 0f,
            completion_time = 0f
        };

        string json = JsonUtility.ToJson(payload);
        StartCoroutine(PostWithFailover("active-trainee", json));
    }

    /// <summary>
    /// Dispatches live hazard updates as hazards are clicked during the warehouse session.
    /// </summary>
    public void SendHazardUpdate(string username, int foundCount, List<string> foundList, int wrongClicks = 0, float completionTime = 0f)
    {
        if (string.IsNullOrWhiteSpace(username))
        {
            username = PlayerPrefs.GetString("CurrentTrainee", "Anonymous_Worker");
        }

        ActiveTraineePayload payload = new ActiveTraineePayload
        {
            username = username,
            hazards_found = foundCount,
            hazards_missed = Mathf.Max(0, 13 - foundCount),
            wrong_clicks = wrongClicks,
            found_hazards = foundList != null ? foundList.ToArray() : Array.Empty<string>(),
            score = Mathf.Round(((float)foundCount / 13f) * 100f * 10f) / 10f,
            completion_time = completionTime
        };

        string json = JsonUtility.ToJson(payload);
        StartCoroutine(PostWithFailover("active-trainee", json));
    }

    /// <summary>
    /// Resets the VR monitor to standby ("No trainee active yet").
    /// </summary>
    public void ClearActiveTrainee()
    {
        StartCoroutine(PostWithFailover("active-trainee/clear", "{}"));
    }

    /// <summary>
    /// Dispatches final drill evaluation to Laravel MySQL database at session end.
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

        string json = JsonUtility.ToJson(payload);
        StartCoroutine(PostWithFailover("trainee-results", json));
    }

    /// <summary>
    /// Fallback overload for calls without wrong clicks argument.
    /// </summary>
    public void SendSessionResult(int score, int found, int missed, float timeTaken, List<string> foundList, List<string> missedList)
    {
        SendSessionResult(score, found, missed, 0, timeTaken, foundList, missedList);
    }

    private IEnumerator PostWithFailover(string endpoint, string json)
    {
        List<string> candidateUrls = GetCandidateUrls(endpoint);
        byte[] bodyRaw = Encoding.UTF8.GetBytes(json);

        foreach (string targetUrl in candidateUrls)
        {
            UnityWebRequest request = null;
            try
            {
                request = new UnityWebRequest(targetUrl, "POST");
                UploadHandlerRaw uploadHandler = new UploadHandlerRaw(bodyRaw);
                uploadHandler.contentType = "application/json";
                request.uploadHandler = uploadHandler;
                request.downloadHandler = new DownloadHandlerBuffer();
                request.timeout = requestTimeoutSeconds;
                request.SetRequestHeader("Content-Type", "application/json");
                request.SetRequestHeader("Accept", "application/json");
            }
            catch (Exception ex)
            {
                Debug.LogError($"[LaravelApiBridge] Error creating web request for {targetUrl}: {ex.Message}");
                request?.Dispose();
                continue;
            }

            using (request)
            {
                UnityWebRequestAsyncOperation op = null;
                try
                {
                    op = request.SendWebRequest();
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"[LaravelApiBridge] SendWebRequest error for {targetUrl}: {ex.Message}. Trying next endpoint...");
                    continue;
                }

                yield return op;

                if (request.result == UnityWebRequest.Result.Success)
                {
                    // Cache the base URL prefix for future requests
                    int lastSlash = targetUrl.LastIndexOf('/');
                    if (lastSlash > 0)
                    {
                        lastWorkingBaseUrl = targetUrl.Substring(0, lastSlash);
                    }
                    Debug.Log($"<color=green>[LaravelApiBridge] Successfully synced '{endpoint}' to {targetUrl}: {request.downloadHandler.text}</color>");
                    yield break;
                }
                else
                {
                    Debug.LogWarning($"[LaravelApiBridge] Failed posting to {targetUrl} ({request.error}). Trying next endpoint...");
                }
            }
        }

        Debug.LogError($"[LaravelApiBridge] All endpoints failed for route '{endpoint}'.");
    }
}