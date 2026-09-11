namespace AudioTranscriber.Benchmarks;

public sealed class RequestBudget
{
    public const int MaximumAttempts = 9;
    public const double MaximumAudioSeconds = 135;
    private readonly object gate = new();
    public int Attempts { get; private set; }
    public double SubmittedAudioSeconds { get; private set; }

    public void CountBeforeAttempt(TimeSpan audioDuration)
    {
        lock (gate)
        {
            if (audioDuration <= TimeSpan.Zero || audioDuration > TimeSpan.FromSeconds(15) ||
                Attempts >= MaximumAttempts || SubmittedAudioSeconds + audioDuration.TotalSeconds > MaximumAudioSeconds)
                throw new InvalidOperationException("Public benchmark request budget exhausted.");
            Attempts++;
            SubmittedAudioSeconds += audioDuration.TotalSeconds;
        }
    }
}
