using System.Collections.Immutable;
using AudioTranscriber.Core;
using Xunit;

namespace AudioTranscriber.Diarization.Tests;

public sealed class SpeakerReconcilerTests
{
    internal static float[] Vector(int index)
    {
        var values = new float[256];
        values[index] = 1;
        return values;
    }

    [Fact]
    public void SixSimulatedIdentitiesSurviveSerializationAndPermutedWindows()
    {
        var state = new SpeakerRegistry();
        var reconciler = new SpeakerReconciler();
        for (var i = 0; i < 6; i++)
        {
            var matches = reconciler.Reconcile(state, [new(0, 5, 0)], [new(0, Vector(i), 4)]);
            Assert.Equal($"Speaker{i + 1}", Assert.Single(matches).SpeakerId);
        }
        state = SpeakerRegistry.Deserialize(state.Serialize());
        var remapped = reconciler.Reconcile(state,
            [new(0, 5, 72), new(5, 10, 11), new(10, 15, 8)],
            [new(72, Vector(5), 4), new(11, Vector(0), 4), new(8, Vector(3), 4)]);
        Assert.Equal("Speaker6", remapped.Single(m => m.LocalSpeaker == 72).SpeakerId);
        Assert.Equal("Speaker1", remapped.Single(m => m.LocalSpeaker == 11).SpeakerId);
        Assert.Equal("Speaker4", remapped.Single(m => m.LocalSpeaker == 8).SpeakerId);
        Assert.Equal(6, state.Speakers.Count);
        for (var i = 0; i < 30; i++)
            reconciler.Reconcile(state, [new(0, 5, 0)], [new(0, Vector(0), 4)]);
        Assert.InRange(state.Speakers[0].Representatives.Count, 1, 5);
    }

    [Fact]
    public void OverlappingClustersCannotShareOneStableIdentity()
    {
        var state = new SpeakerRegistry();
        var result = new SpeakerReconciler().Reconcile(state,
            [new(0, 4, 0), new(2, 6, 1)],
            [new(0, Vector(0), 3), new(1, Vector(0), 3)]);
        Assert.Equal("Speaker1", result[0].SpeakerId);
        Assert.Null(result[1].SpeakerId);
        Assert.Equal("IncompatibleOverlap", result[1].Quality);
        Assert.Single(state.Speakers);
    }

    [Fact]
    public void DisjointLocalClustersCanMapToTheSameSpeaker()
    {
        var result = new SpeakerReconciler().Reconcile(new(),
            [new(0, 4, 0), new(5, 9, 1)], [new(0, Vector(0), 3), new(1, Vector(0), 3)]);
        Assert.Equal(result[0].SpeakerId, result[1].SpeakerId);
    }

    [Fact]
    public void ShortAndOverlapOnlySpeechAbstains()
    {
        var state = new SpeakerRegistry();
        var result = new SpeakerReconciler().Reconcile(state,
            [new(0, 0.5, 0), new(0, 5, 1)], [new(0, Vector(0), 0.4)]);
        Assert.All(result, m => Assert.Null(m.SpeakerId));
        Assert.Empty(state.Speakers);
    }

    [Fact]
    public void RunnerUpMarginAndGrayZoneAbstainRatherThanInventingIdentity()
    {
        var a = Vector(0);
        var b = Vector(0);
        b[0] = 0.8f; b[1] = 0.6f;
        var state = new SpeakerRegistry { NextSpeakerNumber = 3 };
        state.Speakers.Add(new("Speaker1", a, [a], 1));
        state.Speakers.Add(new("Speaker2", b, [b], 1));
        var ambiguous = Vector(0);
        ambiguous[0] = (float)Math.Sqrt(0.9); ambiguous[1] = (float)Math.Sqrt(0.1);
        var result = new SpeakerReconciler().Reconcile(state, [new(0, 4, 0)], [new(0, ambiguous, 3)]);
        Assert.Null(Assert.Single(result).SpeakerId);
        Assert.Equal(2, state.Speakers.Count);
        var gray = Vector(0); gray[0] = 0.6f; gray[2] = 0.8f;
        Assert.Null(Assert.Single(new SpeakerReconciler().Reconcile(state,
            [new(0, 4, 0)], [new(0, gray, 3)])).SpeakerId);
    }

    [Fact]
    public void CancellationBeforeReconciliationDoesNotMutateRegistry()
    {
        var state = new SpeakerRegistry();
        Assert.Throws<OperationCanceledException>(() => new SpeakerReconciler().Reconcile(state,
            [new(0, 4, 0)], [new(0, Vector(0), 3)], new CancellationToken(true)));
        Assert.Empty(state.Speakers);
    }

    [Fact]
    public void MergedIdsAreAliasesAndNumbersAreNeverReused()
    {
        var state = new SpeakerRegistry();
        var reconciler = new SpeakerReconciler();
        reconciler.Reconcile(state, [new(0, 4, 0), new(5, 9, 1)],
            [new(0, Vector(0), 3), new(1, Vector(1), 3)]);
        state.Merge("Speaker2", "Speaker1");
        state = SpeakerRegistry.Deserialize(state.Serialize());
        Assert.Equal("Speaker1", state.ResolveAlias("Speaker2"));
        for (var i = 0; i < 15; i++)
            reconciler.Reconcile(state, [new(0, 4, 0)], [new(0, Vector(0), 3)]);
        Assert.Equal("Speaker1", Assert.Single(reconciler.Reconcile(state,
            [new(0, 4, 0)], [new(0, Vector(1), 3)])).SpeakerId);
        var result = reconciler.Reconcile(state, [new(0, 4, 0)], [new(0, Vector(2), 3)]);
        Assert.Equal("Speaker3", Assert.Single(result).SpeakerId);
    }

    [Fact]
    public void CoreRegistryRoundTripPreservesGuidDisplayNameAndParticipantProvenance()
    {
        var session = Guid.NewGuid();
        var speaker = Guid.NewGuid();
        var values = Vector(0).ToImmutableArray();
        var identity = new SpeakerIdentity(speaker, session, 1, "Renamed player", ExternalParticipantId: "external-person-id");
        var snapshot = new SpeakerRegistrySnapshot(session, 8, [
            new(identity, DiarizationModels.EmbeddingSha256, values, [
                new(Guid.NewGuid(), session, speaker, DiarizationModels.EmbeddingSha256, values, 0, 30_000_000)
            ], 30_000_000)
        ]);
        snapshot = CoreRegistrySerializer.Deserialize(CoreRegistrySerializer.Serialize(snapshot));
        var state = CoreRegistrySerializer.ToState(snapshot);
        var evidence = new SpeakerEvidence[] { new(7, Vector(0), 3) };
        var matches = new SpeakerReconciler().Reconcile(state, [new(0, 4, 7)], evidence);
        var output = CoreRegistrySerializer.ToSnapshot(state, snapshot,
            new(session, Guid.NewGuid(), "unused.pcm", 64000, 0), evidence, matches);
        Assert.Equal(identity, Assert.Single(output.Speakers).Identity);
        Assert.Equal("Speaker1", Assert.Single(matches).SpeakerId);
    }

    [Fact]
    public void InvalidRegistryModelAndNaNEmbeddingsAreRejected()
    {
        Assert.Throws<InvalidDataException>(() => new SpeakerRegistry { EmbeddingModelSha256 = "other" }.Serialize());
        var bad = Vector(0); bad[1] = float.NaN;
        Assert.Throws<InvalidDataException>(() => new SpeakerReconciler().Reconcile(new(),
            [new(0, 4, 0)], [new(0, bad, 3)]));
    }

    [Fact]
    public void CoreMergedSpeakerRetainsSecondVoiceAfterRestart()
    {
        var session = Guid.NewGuid();
        var firstId = Guid.NewGuid();
        var secondId = Guid.NewGuid();
        SpeakerRegistryEntry Entry(Guid id, int number, Guid? mergedInto)
        {
            var vector = Vector(number - 1).ToImmutableArray();
            return new(new(id, session, number, $"Speaker {number}", MergedIntoId: mergedInto),
                DiarizationModels.EmbeddingSha256, vector, [
                    new(Guid.NewGuid(), session, id, DiarizationModels.EmbeddingSha256, vector, 0, 30_000_000)
                ], 30_000_000);
        }
        var snapshot = new SpeakerRegistrySnapshot(session, 3, [Entry(firstId, 1, null), Entry(secondId, 2, firstId)]);
        var evidence = new SpeakerEvidence[] { new(0, Vector(1), 3) };
        for (var i = 0; i < 3; i++)
        {
            var state = CoreRegistrySerializer.ToState(snapshot);
            var match = new SpeakerReconciler().Reconcile(state, [new(0, 4, 0)], evidence);
            Assert.Equal("Speaker1", Assert.Single(match).SpeakerId);
            snapshot = CoreRegistrySerializer.ToSnapshot(state, snapshot,
                new(session, Guid.NewGuid(), "unused.pcm", 64000, 0), evidence, match);
            snapshot = CoreRegistrySerializer.Deserialize(CoreRegistrySerializer.Serialize(snapshot));
            Assert.Equal(firstId, snapshot.Speakers[0].Identity.Id);
            Assert.Equal(firstId, snapshot.Speakers[1].Identity.MergedIntoId);
            Assert.Equal(2, snapshot.Speakers.Length);
        }
    }

    [Fact]
    public void SourceSampleCoordinatesAreNotConfusedWithMultiHourSessionTicks()
    {
        var session = Guid.NewGuid();
        var track = Guid.NewGuid();
        const long origin = 5L * 3600 * TimeSpan.TicksPerSecond;
        var request = new DiarizationRequest(session, track, "unused.pcm", 480000, origin);
        var turn = new SpeakerTurn(track, origin + 625, origin + 5000, null);
        var result = new DiarizationResult([turn], new(session, 0, []), []);
        var mapped = Assert.Single(DiarizationCoordinates.ToSourceIntervals(result, request, 2_000_000_000));
        Assert.Equal(1, mapped.InputStartSample);
        Assert.Equal(8, mapped.InputEndSample);
        Assert.Equal(2_000_000_001, mapped.NormalizedSourceStartSample);
        Assert.Equal(origin + 625, mapped.Turn.StartTicks);
    }
}
