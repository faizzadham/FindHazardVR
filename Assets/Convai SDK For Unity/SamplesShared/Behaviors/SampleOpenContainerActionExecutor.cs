using System.Threading;
using System.Threading.Tasks;
using Convai.Runtime.Actions;
using UnityEngine;

namespace Convai.Sample.Behaviors
{
    /// <summary>
    ///     A worked example of an Action Behavior you write yourself: the character opens the thing
    ///     the action points at.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <b>Why this, and not a built-in "Open" behavior?</b> Opening means something different
    ///         in every project — a lid that hinges, a drawer that slides, a UI panel, a save file. A
    ///         built-in behavior would have to guess, and would be wrong for most projects. So the
    ///         SDK ships the verbs that mean the same thing everywhere (look, walk, point, wait) and
    ///         hands you this pattern for the ones that do not.
    ///     </para>
    ///     <para>
    ///         <b>The four things every Action Behavior does</b>, all visible below:
    ///     </para>
    ///     <list type="number">
    ///         <item><description>Derive from <see cref="ConvaiActionExecutorBase" /> or one of its
    ///         subclasses — that is what gives the component the Convai inspector for free.</description></item>
    ///         <item><description>Check what you need is there, and return
    ///         <see cref="ConvaiActionExecutionResult.Unhandled" /> when it is not. A decline lets
    ///         another behavior answer; a failure stops the batch.</description></item>
    ///         <item><description>Read per-call values with <c>GetOverride</c>, so the Inspector value
    ///         is the fallback and the character can vary it.</description></item>
    ///         <item><description>Honour the cancellation token, so a replaced or timed-out action
    ///         unwinds instead of finishing in the background.</description></item>
    ///     </list>
    ///     <para>
    ///         Add <c>[ConvaiActionArchetype]</c> as shown and your behavior appears in the Actions
    ///         Editor's <b>+ Add Action ▾</b> catalog alongside the built-in ones, with its parameters
    ///         pre-filled. See <c>Documentation~/ACTIONS-EXTENDING.md</c> for the full walkthrough.
    ///     </para>
    /// </remarks>
    [AddComponentMenu("Convai/Samples/Open Container (Sample)")]
    [ConvaiActionArchetype(
        "Open Container (Sample)",
        ActionName = "Open",
        Description = "Sample: opens the thing the action points at, if it has a lid this behavior can move.",
        TargetRequirement = ConvaiActionTargetRequirement.Object,
        Parameters = new[] { "angle,Number" })]
    public sealed class SampleOpenContainerActionExecutor : ConvaiTargetedActionExecutor
    {
        [SerializeField]
        [Tooltip("How far the lid swings open, in degrees. The character can ask for a different " +
                 "amount per call with the 'angle' parameter.")]
        private float _openAngle = 100f;

        [SerializeField]
        [Min(0.05f)]
        [Tooltip("How long the lid takes to swing, in seconds.")]
        private float _swingSeconds = 0.8f;

        [SerializeField]
        [Tooltip("Name of the child object that acts as the lid. The behavior looks for it on the " +
                 "object the action points at.")]
        private string _lidChildName = "Lid";

        /// <inheritdoc />
        protected override async Task<ConvaiActionExecutionResult> ExecuteCoreAsync(
            ConvaiActionInvocation invocation,
            CancellationToken cancellationToken)
        {
            // 1. Is what we need actually here? Decline clearly if not — and say what is missing,
            //    because whoever sees this message is the person who can fix it.
            GameObject targetObject = ResolvePhysicalTarget(invocation, CharacterTransform.position, out _);
            if (targetObject == null)
                return ConvaiActionExecutionResult.Unhandled("This action has nothing to open.");

            Transform lid = targetObject.transform.Find(_lidChildName);
            if (lid == null)
            {
                return ConvaiActionExecutionResult.Unhandled(
                    $"'{targetObject.name}' has no child called '{_lidChildName}', so there is nothing to swing open.");
            }

            // 2. The Inspector value is the default; the character can ask for something else.
            float openAngle = GetOverride(invocation, "angle", _openAngle);
            float swingSeconds = Mathf.Max(0.05f, _swingSeconds);

            Quaternion closed = lid.localRotation;
            Quaternion open = closed * Quaternion.Euler(-openAngle, 0f, 0f);

            // 3. Move over time, a frame at a time. Note what this is not: Task.Delay. A wall-clock
            //    wait ignores pausing and time scale, and is not even legal on WebGL.
            float elapsed = 0f;
            while (elapsed < swingSeconds)
            {
                // 4. Cancellation. Throwing out of here is correct: the dispatcher turns it into a
                //    cancelled or timed-out result depending on what actually happened.
                cancellationToken.ThrowIfCancellationRequested();

                elapsed += Time.deltaTime;
                lid.localRotation = Quaternion.Slerp(closed, open, Mathf.SmoothStep(0f, 1f, elapsed / swingSeconds));
                await Task.Yield();
            }

            lid.localRotation = open;
            return ConvaiActionExecutionResult.Succeeded($"Opened {ResolveTargetName(invocation)}.");
        }

        private void OnValidate() => _swingSeconds = Mathf.Max(0.05f, _swingSeconds);
    }
}
