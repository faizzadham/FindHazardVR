using System;
using System.Collections.Generic;
using Convai.Runtime.SceneMetadata;
using UnityEditor;
using UnityEngine;
using static Convai.Editor.SceneTagging.SceneTaggerNative;

namespace Convai.Editor.SceneTagging
{
    internal enum SceneTaggerState { Idle, Discovering, Capturing, Analyzing, ReviewReady, Applying, Cancelled, NeedsAttention }
    internal enum SceneTaggerQuality { Fast, Accurate }
    internal enum SceneTaggerStatus { Candidate, Capturing, Analyzing, Ready, Attention, Skipped, Applied }
    internal enum SceneTaggerSource { Capture, IdenticalGeometry, AdjustedView, RestoredReview, ReleasedImage, MetadataCache }
    internal enum SceneTaggerGrouping { KeepSeparate, NearbyClusters, GroupAllVerified }

    [Serializable]
    internal sealed class SceneTaggerSettings
    {
        public string CharacterId = "", CharacterName = "", SceneDescription = "", Focus = "";
        [NonSerialized] internal string EndpointIdentity;
        public bool SelectedOnly, IncludeTagged, CacheEnabled, DebugCaptures;
        public bool ExcludeBasicShapes = true;
        public bool Spatial = true, Gaze = true, Actions = true, GenerateMovementPoints;
        public int Concurrency = 4, CandidateLimit = 300;
        public float Relevance = .52f, AutoIncludeConfidence = .75f;
        public SceneTaggerQuality Quality;
        public SceneTaggerGrouping Grouping = SceneTaggerGrouping.NearbyClusters;
        public int EvidenceSize => Quality == SceneTaggerQuality.Fast ? 768 : 1024;
        public int CellSize => Quality == SceneTaggerQuality.Fast ? 384 : 512;
        public int Grid => Quality == SceneTaggerQuality.Fast ? 4 : 3;
        public SceneTaggerSettings Copy() => JsonUtility.FromJson<SceneTaggerSettings>(JsonUtility.ToJson(this));
    }

    internal sealed class SceneTaggerRow
    {
        internal readonly string Id = Guid.NewGuid().ToString("N");
        internal GameObject Target;
        internal string GlobalId, Fingerprint, ProofGroup, LogicalGroupId, Name = "", Description = "", Note = "", Error = "";
        internal SceneTaggerSource Source;
        internal Renderer[] Renderers;
        internal HashSet<GameObject> Boundaries;
        internal Vector3[][] Corners;
        internal Bounds Bounds;
        internal Matrix4x4 WorldMatrix;
        internal long SceneId;
        internal float Significance, Confidence;
        internal uint SignificanceReasons;
        internal bool ExcludedBasicShape;
        internal bool AllowLowInformation;
        internal bool Explicit, Included = true, Checked, PendingAnalysis, Metallic;
        internal int ImageRevision, NoteRevision, EditRevision;
        internal SceneTaggerStatus Status;
        internal SceneTaggerEvidence Evidence;
        internal SceneTaggerEvidence RejectedEvidence;
        internal SceneTaggerReviewBackup PreviousUsable;
        internal bool CanApply => Target != null && Included && !PendingAnalysis && string.IsNullOrWhiteSpace(Note) &&
            Status == SceneTaggerStatus.Ready && !string.IsNullOrWhiteSpace(Name) &&
            !string.IsNullOrWhiteSpace(Description) && Name.Length <= ConvaiObjectMetadata.MaxNameLength && Description.Length <= ConvaiObjectMetadata.MaxDescriptionLength;
        internal bool IsCurrent => Target && Target.scene.IsValid() && Target.scene.isLoaded && !EditorUtility.IsPersistent(Target);
    }

    internal sealed class SceneTaggerReviewBackup
    {
        internal SceneTaggerEvidence Evidence;
        internal string Name, Description;
        internal SceneTaggerSource Source;
        internal float Confidence;
        internal SceneTaggerStatus Status;
        internal int EditRevision, NoteRevision;
        internal static SceneTaggerReviewBackup From(SceneTaggerRow row) => new()
        {
            Evidence = row.Evidence, Name = row.Name, Description = row.Description, Source = row.Source,
            Confidence = row.Confidence, Status = row.Status, EditRevision = row.EditRevision, NoteRevision = row.NoteRevision
        };
    }

    internal sealed class SceneTaggerEvidence
    {
        internal bool LowInformation;
        internal byte[] Png;
        internal Vector3 LocalDirection;
        internal Vector3? PresentationDirection;
        internal Vector2 Pan;
        internal float DistanceScale = 1;
        internal int LightingProfile;
        internal uint LightingPolicy;
        internal float HighlightExposure = -1;
        internal bool BroadFace, DescriptorValid;
        internal convai_sat_evidence_descriptor_v1 Descriptor;
        internal string Policy;
        internal string Failure;
    }

    internal sealed class SceneTaggerEvidenceBudgetException : InvalidOperationException
    {
        internal SceneTaggerEvidenceBudgetException() : base("The 256 MiB evidence budget is full. Release applied images or remove reviewed rows before capturing more objects.") { }
    }

    internal static class SceneTaggerText
    {
        internal const int SceneContextLimit = 800, FocusLimit = 400, NoteLimit = 300;
        internal static string Limit(string text, int limit)
        {
            text ??= "";
            if (text.Length <= limit) return text;
            if (limit > 0 && char.IsHighSurrogate(text[limit - 1])) limit--;
            return text.Substring(0, limit);
        }
        internal static string Normalize(string text, int limit)
        {
            text = (text ?? "").Trim();
            if (text.Length <= limit) return text;
            if (limit > 0 && char.IsHighSurrogate(text[limit - 1])) limit--;
            return text.Substring(0, limit).TrimEnd();
        }
    }
}
