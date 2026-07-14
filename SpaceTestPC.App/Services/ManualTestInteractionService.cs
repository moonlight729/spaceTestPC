namespace SpaceTestPC.App.Services;

public sealed class ManualTestInteractionService
{
    private readonly Lock _gate = new();
    private readonly Dictionary<string, TaskCompletionSource<bool>> _pending = [];
    private readonly Dictionary<string, bool> _submittedDecisions = [];

    public Task<bool> WaitForDecisionAsync(string testId, CancellationToken cancellationToken = default)
    {
        TaskCompletionSource<bool> source;
        lock (_gate)
        {
            if (_submittedDecisions.Remove(testId, out var submittedDecision))
            {
                return Task.FromResult(submittedDecision);
            }

            source = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            _pending[testId] = source;
        }

        cancellationToken.Register(() => source.TrySetCanceled(cancellationToken));
        return source.Task;
    }

    public void SubmitDecision(string testId, bool passed)
    {
        TaskCompletionSource<bool>? source;
        lock (_gate)
        {
            _pending.TryGetValue(testId, out source);
            if (source is not null)
            {
                _pending.Remove(testId);
            }
            else
            {
                _submittedDecisions[testId] = passed;
            }
        }

        source?.TrySetResult(passed);
    }
}
