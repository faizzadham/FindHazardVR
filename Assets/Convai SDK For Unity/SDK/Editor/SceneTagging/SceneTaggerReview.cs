using System;
using System.Collections.Generic;
using System.Linq;

namespace Convai.Editor.SceneTagging
{
    /// <summary>Shared review rules for list rows, counts and bulk actions.</summary>
    internal static class SceneTaggerReview
    {
        private static readonly string[] RelevanceReasons = { "useful object scale", "large object", "small object", "multiple visible parts", "multiple materials", "recognizable object label", "structural surface", "authored object name", "plain engine shape", "authored panel surface", "low geometric detail", "low visual detail" };
        internal static bool Eligible(SceneTaggerRow row) => row.IsCurrent && row.Status != SceneTaggerStatus.Applied && row.Status != SceneTaggerStatus.Capturing && row.Status != SceneTaggerStatus.Analyzing;
        internal static bool Removable(SceneTaggerRow row) => row.Status != SceneTaggerStatus.Applied && row.Status != SceneTaggerStatus.Capturing && row.Status != SceneTaggerStatus.Analyzing;
        internal static bool Ready(SceneTaggerRow row) => Complete(row) && row.Status != SceneTaggerStatus.Applied;
        internal static bool Complete(SceneTaggerRow row) => row.IsCurrent && string.IsNullOrEmpty(row.Error) && !row.PendingAnalysis && string.IsNullOrWhiteSpace(row.Note) && (row.Status == SceneTaggerStatus.Ready || row.Status == SceneTaggerStatus.Applied);
        internal static bool Attention(SceneTaggerRow row) => !row.IsCurrent || row.Status == SceneTaggerStatus.Attention || row.Status != SceneTaggerStatus.Skipped && !string.IsNullOrEmpty(row.Error);
        internal static bool NeedsAnalysis(SceneTaggerRow row) => !Complete(row) && !Attention(row) && row.Status != SceneTaggerStatus.Skipped;
        internal static bool NeedsIncludedAnalysis(SceneTaggerRow row) => row.Included && Eligible(row) && NeedsAnalysis(row);
        internal static string ConfidenceLabel(SceneTaggerRow row) => Complete(row) ? $"{row.Confidence:P0} (uncalibrated)" : "pending";
        internal static bool HasUsableImage(SceneTaggerRow row) => row.Evidence?.Png != null && (string.IsNullOrEmpty(row.Evidence.Failure) || row.Evidence.LowInformation);
        internal static string Status(SceneTaggerRow row)
        {
            if (!row.IsCurrent) return "Object missing";
            if (row.Status == SceneTaggerStatus.Applied) return "Applied";
            if (row.Status == SceneTaggerStatus.Capturing || row.Status == SceneTaggerStatus.Analyzing) return "Processing";
            if (row.Status == SceneTaggerStatus.Skipped) return "Skipped";
            if (Attention(row)) return "Attention";
            if (row.PendingAnalysis || !string.IsNullOrWhiteSpace(row.Note)) return "Needs analysis";
            if (Complete(row)) return row.Included ? "Included" : "Not included";
            return "Not analyzed";
        }
        internal static string Recovery(SceneTaggerRow row)
        {
            if (!row.IsCurrent) return "The reviewed object no longer exists in the current editor scene.";
            if (!string.IsNullOrEmpty(row.Note)) return "A refinement note is saved. Analyze the note or clear it before Apply.";
            string retry = HasUsableImage(row) ? "Check this row, then choose More > Analyze all checked." : "Check this row, then choose More > Recapture & analyze checked.";
            if (!string.IsNullOrEmpty(row.Error)) return row.Error + "\n" + retry;
            if (row.PendingAnalysis) return "This saved view has not been analyzed. " + retry;
            return Complete(row) ? "Review the image and suggested text before applying." : "Exploration stopped before this object was analyzed. " + retry;
        }
        internal static string Tooltip(SceneTaggerRow row) => (row.IsCurrent ? SceneTaggerDiscovery.StablePath(row.Target) : "Object missing") +
            $"\nLocal relevance: {row.Significance:P0} ({Reasons(row)})\nSource: {SceneTaggerReview.SourceLabel(row)}\n" + Recovery(row) + (string.IsNullOrEmpty(row.Note) ? "" : "\nSaved refinement note: " + row.Note);
        internal static string Reasons(SceneTaggerRow row)
        {
            var reasons = new List<string>();
            if (row.Explicit) reasons.Add("explicitly selected");
            for (int i = 0; i < RelevanceReasons.Length; i++) if ((row.SignificanceReasons & (1u << i)) != 0) reasons.Add(RelevanceReasons[i]);
            return reasons.Count == 0 ? "eligible scene geometry" : string.Join(", ", reasons);
        }
        internal static string EvidencePlaceholder(SceneTaggerRow row)
        {
            if (row.Source == SceneTaggerSource.MetadataCache || row.Source == SceneTaggerSource.IdenticalGeometry) return "This suggestion was reused. The previous analysis image was not retained.";
            if (!string.IsNullOrEmpty(row.Error)) return "No usable analysis image was captured for this object.";
            if (row.Status == SceneTaggerStatus.Capturing || row.Status == SceneTaggerStatus.Analyzing) return "The selected object's analysis image will appear here.";
            if (row.Status == SceneTaggerStatus.Candidate) return "No image was retained before exploration stopped. Check this row, then choose More > Recapture & analyze checked.";
            return "No analysis image is available for this suggestion.";
        }
        internal static string EvidenceCaption(SceneTaggerRow row, bool editing)
        {
            if (editing) return "Adjust this isolated view. Use view saves it for later batch analysis; Use & analyze saves and analyzes it now. The object and Scene view camera are not moved.";
            if (!string.IsNullOrEmpty(row.Error)) return HasUsableImage(row) ? "This image is still available. Check this row, then choose More > Analyze all checked; use Edit view first if the image itself is wrong." : "No usable image was captured. Check this row, then choose More > Recapture & analyze checked.";
            if (row.PendingAnalysis) return "Saved view needs analysis. The suggestion and score still belong to the previous image. Check this row, then choose More > Analyze all checked.";
            if (row.Status == SceneTaggerStatus.Candidate) return HasUsableImage(row) ? "Exploration stopped before this image was analyzed. Check this row, then choose More > Analyze all checked." : "Exploration stopped before an image was captured. Check this row, then choose More > Recapture & analyze checked.";
            if (row.Source == SceneTaggerSource.AdjustedView) return "This adjusted view was used for the object's most recent description.";
            return "Image used for this suggestion. It may differ from the Scene view.";
        }
        internal static string SourceLabel(SceneTaggerRow row) => row.Source switch
        {
            SceneTaggerSource.IdenticalGeometry => "Identical geometry",
            SceneTaggerSource.AdjustedView => "Adjusted view",
            SceneTaggerSource.RestoredReview => "Restored review (no saved image)",
            SceneTaggerSource.ReleasedImage => "Applied result (image released)",
            SceneTaggerSource.MetadataCache => "Metadata cache (no saved image)",
            _ => "Capture"
        };
        internal static string SearchText(SceneTaggerRow row) =>
            string.Join(" ", row.Name, row.Target ? row.Target.name : "", row.Description, row.Note, row.Error, SourceLabel(row));

        // Keep the selected row searchable under the text it had when editing began.
        internal static bool Matches(SceneTaggerRow row, string search, string editingSnapshot = null) =>
            string.IsNullOrEmpty(search) || SearchText(row).IndexOf(search, StringComparison.OrdinalIgnoreCase) >= 0 ||
            editingSnapshot != null && editingSnapshot.IndexOf(search, StringComparison.OrdinalIgnoreCase) >= 0;

        internal static IOrderedEnumerable<SceneTaggerRow> Sort(IEnumerable<SceneTaggerRow> rows) => rows.OrderByDescending(r => r.Status == SceneTaggerStatus.Ready || r.Status == SceneTaggerStatus.Applied)
            .ThenByDescending(r => r.Confidence).ThenByDescending(r => r.Significance)
            .ThenBy(r => r.Target ? r.Target.name : r.GlobalId, StringComparer.Ordinal).ThenBy(r => r.Id, StringComparer.Ordinal);
    }
}
