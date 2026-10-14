using Convai.Editor.SceneTagging;
using NUnit.Framework;

namespace Convai.Tests.EditMode.SceneTagging
{
    public sealed class SceneTaggerReviewEditingTests
    {
        [Test] public void SearchKeepsAnEditingRowVisibleUntilSelectionChanges()
        {
            var row = new SceneTaggerRow { Name = "Copper statue", Description = "A small sculpture", Note = "Refine the head" };
            string snapshot = SceneTaggerReview.SearchText(row);
            row.Name = "Bronze figure"; row.Description = "A bust"; row.Note = "";
            Assert.That(SceneTaggerReview.Matches(row, "copper", snapshot), Is.True);
            Assert.That(SceneTaggerReview.Matches(row, "sculpture", snapshot), Is.True);
            Assert.That(SceneTaggerReview.Matches(row, "refine", snapshot), Is.True);
            Assert.That(SceneTaggerReview.Matches(row, "bronze", snapshot), Is.True);
            Assert.That(SceneTaggerReview.Matches(row, "unrelated", snapshot), Is.False);
            Assert.That(SceneTaggerReview.Matches(row, "copper"), Is.False);
        }
    }
}
