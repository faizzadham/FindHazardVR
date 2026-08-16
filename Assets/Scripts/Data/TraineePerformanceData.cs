using System;

// <summary>
// A pure data class representing a single trainee's performance session.
// This class does NOT inherit from MonoBehaviour so it can be easily 
// serialized into a JSON string and saved to the Quest 3S local storage.
// </summary>
[Serializable]
public class TraineePerformanceData
{
    // The core data fields required by your project specifications
    public string username;
    public int currentScore;
    public int hazardsMissed;
    public float completionTime;

    // <summary>
    // Default constructor. 
    // Required by Newtonsoft.Json for deserialization (reading the file back).
    // </summary>
    public TraineePerformanceData()
    {
        // Leaves fields empty/default to be filled by the JSON parser
    }

    // <summary>
    // Parameterized constructor.
    // Use this to easily create a new record when the trainee finishes the VR simulation.
    // </summary>
    public TraineePerformanceData(string name, int score, int missed, float time)
    {
        username = name;
        currentScore = score;
        hazardsMissed = missed;
        completionTime = time;
    }
}