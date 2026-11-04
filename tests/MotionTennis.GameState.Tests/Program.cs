using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text.Json;
using MotionTennis.GameState;

var checks = new (string Name, Action Run)[]
{
    ("eight-query full-match snapshot and atomic commit", SetupQueryAndCommit),
    ("identical commit retry returns the original outcome", DuplicateCommit),
    ("changed payload under a retained batch key is rejected", ChangedDuplicate),
    ("stale base version does not mutate committed state", StaleBase),
    ("invalid candidate rolls back every field", InvalidCandidateRollback),
    ("wrong sessions and epochs are fenced", WriterFencing),
    ("periodic-only state advance is accepted", PeriodicAdvance),
    ("expired commit status is explicitly unknown", ExpiredCommit),
    ("deduplication is bounded without evicting live results", BoundedRetention),
    ("setup retry is idempotent and changed setup is rejected", SetupRetry),
    ("rejected query inputs are explicitly accounted in source order", RejectedQueryAccounting),
    ("writer restart requires setup reconciliation and preserves the snapshot", WriterRestart),
    ("score change requires a sporting award event", ScoreAward),
    ("technical pause event requires a paused match state", TechnicalPause)
};

var failures = 0;
foreach (var check in checks)
{
    try
    {
        check.Run();
        Console.WriteLine($"PASS {check.Name}");
    }
    catch (Exception ex)
    {
        failures++;
        Console.Error.WriteLine($"FAIL {check.Name}: {ex.Message}");
    }
}

Console.WriteLine($"{checks.Length - failures}/{checks.Length} checks passed.");
return failures == 0 ? 0 : 1;

static void SetupQueryAndCommit()
{
    var fixture = Fixture.Create();
    var request = fixture.Query(1, fixture.Queries(1));
    var first = fixture.Store.Query(request);
    Equal(QueryStatus.Accepted, first.Status);
    Equal(8, first.Queries.Length);
    Equal(request.Queries, first.Queries);
    Equal(1UL, first.Snapshot!.StateVersion);

    var update = fixture.Update(request, first.Snapshot.StateVersion, 1, fixture.Advance(), fixture.Decisions(request));
    var result = fixture.Store.Commit(update);
    Equal(CommitStatus.Accepted, result.Status);
    Equal(2UL, result.StateVersion);
    Equal(2UL, fixture.Store.ReadSnapshot()!.StateVersion);
    Equal(1.25f, fixture.Store.ReadSnapshot()!.Match.Ball.PositionM.X);

    var retry = fixture.Store.Query(request);
    Equal(1UL, retry.Snapshot!.StateVersion);
    var secondReader = fixture.Store.ReadSnapshot();
    Equal(2UL, secondReader!.StateVersion);
    Equal(fixture.Store.ReadSnapshot()!.StateVersion, secondReader.StateVersion);
}

static void DuplicateCommit()
{
    var f = Fixture.Create();
    var q = f.Query(2, f.Queries(2));
    var snapshot = f.Store.Query(q).Snapshot!;
    var update = f.Update(q, snapshot.StateVersion, 1, f.Advance(), f.Decisions(q));
    var original = f.Store.Commit(update);
    var replay = f.Store.Commit(update);
    Equal(CommitStatus.Accepted, original.Status);
    Equal(original, replay);
    Equal(2UL, f.Store.ReadSnapshot()!.StateVersion);
}

static void ChangedDuplicate()
{
    var f = Fixture.Create();
    var q = f.Query(3, f.Queries(3));
    var snapshot = f.Store.Query(q).Snapshot!;
    var update = f.Update(q, snapshot.StateVersion, 1, f.Advance(), f.Decisions(q));
    Equal(CommitStatus.Accepted, f.Store.Commit(update).Status);
    var altered = update with { CandidateMatch = update.CandidateMatch with { Ball = update.CandidateMatch.Ball with { RadiusM = 0.04f } } };
    Equal(CommitStatus.Invalid, f.Store.Commit(altered).Status);
    Equal(2UL, f.Store.ReadSnapshot()!.StateVersion);
}

static void StaleBase()
{
    var f = Fixture.Create();
    var q1 = f.Query(4, f.Queries(4));
    var s0 = f.Store.Query(q1).Snapshot!;
    Equal(CommitStatus.Accepted, f.Store.Commit(f.Update(q1, 1, 1, f.Advance(), f.Decisions(q1))).Status);

    var q2 = f.Query(5, f.Queries(5));
    Equal(2UL, f.Store.Query(q2).Snapshot!.StateVersion);
    var stale = f.Update(q2, s0.StateVersion, 2, f.Advance(), f.Decisions(q2));
    Equal(CommitStatus.Conflict, f.Store.Commit(stale).Status);
    Equal(2UL, f.Store.ReadSnapshot()!.StateVersion);
}

static void InvalidCandidateRollback()
{
    var f = Fixture.Create();
    var q = f.Query(6, f.Queries(6));
    var snapshot = f.Store.Query(q).Snapshot!;
    var before = f.Store.ReadSnapshot();
    var invalid = f.Advance() with { Ball = f.Advance().Ball with { PositionM = new Vector3f(float.NaN, 0, 0) } };
    var result = f.Store.Commit(f.Update(q, snapshot.StateVersion, 1, invalid, f.Decisions(q)));
    Equal(CommitStatus.Invalid, result.Status);
    Equal(before, f.Store.ReadSnapshot());
    Equal(result, f.Store.GetCommitStatus(new BatchKey(q.GameSessionId, q.WriterEpoch, q.BatchId)));
}

static void WriterFencing()
{
    var f = Fixture.Create();
    var wrongSession = f.Query(7, f.Queries(7)) with { GameSessionId = Guid.NewGuid() };
    Equal(QueryStatus.Rejected, f.Store.Query(wrongSession).Status);
    var wrongEpoch = f.Query(8, f.Queries(8)) with { WriterEpoch = f.Writer.WriterEpoch + 1 };
    Equal(QueryStatus.Rejected, f.Store.Query(wrongEpoch).Status);
    var prior = f.Update(f.Query(9, f.Queries(9)), 1, 1, f.Advance(), ImmutableArray<Decision>.Empty) with
    {
        WriterEpoch = f.Writer.WriterEpoch + 1
    };
    Equal(CommitStatus.WrongWriter, f.Store.Commit(prior).Status);

    var validQuery = f.Query(10, f.Queries(10));
    var queriedSnapshot = f.Store.Query(validQuery).Snapshot!;
    var accepted = f.Store.Commit(f.Update(validQuery, queriedSnapshot.StateVersion, 1, f.Advance(), f.Decisions(validQuery)));
    Equal(CommitStatus.Accepted, accepted.Status);

    var replacement = f.Writer with { PhysicsControlSessionId = Guid.NewGuid(), WriterEpoch = f.Writer.WriterEpoch + 1 };
    True(f.Store.RegisterWriter(replacement, out _));
    Equal(2UL, f.Store.ReadSnapshot()!.StateVersion);
    Equal(QueryStatus.Rejected, f.Store.Query(f.Query(10, ImmutableArray<QueryEntry>.Empty)).Status);
}

static void PeriodicAdvance()
{
    var f = Fixture.Create();
    var q = f.Query(11, ImmutableArray<QueryEntry>.Empty);
    var snapshot = f.Store.Query(q).Snapshot!;
    var result = f.Store.Commit(f.Update(q, snapshot.StateVersion, 1, f.Advance(), ImmutableArray<Decision>.Empty));
    Equal(CommitStatus.Accepted, result.Status);
    Equal(101UL, result.SimulationTick);
    Equal(2UL, f.Store.ReadSnapshot()!.StateVersion);
}

static void ExpiredCommit()
{
    var f = Fixture.Create();
    var q = f.Query(12, f.Queries(12));
    var snapshot = f.Store.Query(q).Snapshot!;
    var update = f.Update(q, snapshot.StateVersion, 1, f.Advance(), f.Decisions(q));
    Equal(CommitStatus.Accepted, f.Store.Commit(update).Status);
    f.Clock.Advance(GameStateStore.DedupRetention + TimeSpan.FromMilliseconds(1));
    Equal(CommitStatus.UnknownBatch, f.Store.GetCommitStatus(new BatchKey(q.GameSessionId, q.WriterEpoch, q.BatchId)).Status);
    Equal(2UL, f.Store.ReadSnapshot()!.StateVersion);
}

static void BoundedRetention()
{
    var f = Fixture.Create();
    for (ulong batch = 100; batch < 100 + GameStateStore.DedupCapacity; batch++)
        Equal(QueryStatus.Accepted, f.Store.Query(f.Query(batch, ImmutableArray<QueryEntry>.Empty)).Status);
    Equal(QueryStatus.Overloaded, f.Store.Query(f.Query(100 + GameStateStore.DedupCapacity, ImmutableArray<QueryEntry>.Empty)).Status);
    f.Clock.Advance(GameStateStore.DedupRetention + TimeSpan.FromMilliseconds(1));
    Equal(QueryStatus.Accepted, f.Store.Query(f.Query(10_000, ImmutableArray<QueryEntry>.Empty)).Status);
}

static void SetupRetry()
{
    var f = Fixture.Create();
    True(f.Store.InstallSetup(f.Setup, out _));
    True(f.Store.SetupReply(f.Setup).Accepted);
    var alteredBytes = ImmutableArray.Create<byte>(1, 2, 3, 4);
    var altered = f.Setup with { ConfigurationSha256 = ImmutableArray.CreateRange(SHA256.HashData(alteredBytes.AsSpan())), CanonicalConfigurationBytes = alteredBytes };
    False(f.Store.InstallSetup(altered, out _));
}

static void RejectedQueryAccounting()
{
    var f = Fixture.Create();
    var query = f.Query(20, f.Queries(20));
    f.Store.Query(query);
    var candidate = f.Advance();
    var surviving = f.Decisions(query).RemoveAt(0).Select((decision, index) => decision with { EventIndex = (uint)index }).ToImmutableArray();
    var rejected = new[] { query.Queries[0].Transaction };
    Equal(CommitStatus.Invalid, f.Store.Commit(f.Update(query, 1, 1, candidate, surviving)).Status);

    var g = Fixture.Create();
    var query2 = g.Query(21, g.Queries(21));
    g.Store.Query(query2);
    var decisions = g.Decisions(query2).RemoveAt(0).Select((decision, index) => decision with { EventIndex = (uint)index }).ToImmutableArray();
    Equal(CommitStatus.Accepted, g.Store.Commit(g.Update(query2, 1, 1, g.Advance(), decisions),
        new[] { query2.Queries[0].Transaction }).Status);
}

static void WriterRestart()
{
    var f = Fixture.Create();
    var query = f.Query(22, f.Queries(22));
    var snapshot = f.Store.Query(query).Snapshot!;
    Equal(CommitStatus.Accepted, f.Store.Commit(f.Update(query, snapshot.StateVersion, 1, f.Advance(), f.Decisions(query))).Status);
    var previous = f.Store.ReadSnapshot()!;

    var replacement = f.Writer with { PhysicsControlSessionId = Guid.NewGuid(), WriterEpoch = 2 };
    True(f.Store.RegisterWriter(replacement, out _));
    Equal(previous, f.Store.ReadSnapshot());
    Equal(QueryStatus.Rejected, f.Store.Query(query with { BatchId = 23, WriterEpoch = 2,
        Queries = ImmutableArray<QueryEntry>.Empty }).Status);

    var replacementSetup = f.Setup with { CommandId = 2, WriterEpoch = 2 };
    True(f.Store.InstallSetup(replacementSetup, out _));
    var ack = f.Store.SetupReply(replacementSetup);
    True(ack.Accepted);
    True(f.Store.ResumeReconciledWriter(ack, out _));
    var newQuery = query with { BatchId = 24, WriterEpoch = 2, Queries = ImmutableArray<QueryEntry>.Empty };
    Equal(QueryStatus.Accepted, f.Store.Query(newQuery).Status);
}

static void ScoreAward()
{
    var f = Fixture.Create();
    var query = f.Query(25, ImmutableArray<QueryEntry>.Empty);
    f.Store.Query(query);
    var candidate = f.Advance() with { Score = f.Advance().Score with { PointsA = PointScore.Fifteen }, PointId = 2 };
    var noAward = f.Update(query, 1, 1, candidate, ImmutableArray<Decision>.Empty);
    Equal(CommitStatus.Invalid, f.Store.Commit(noAward).Status);

    var g = Fixture.Create();
    var query2 = g.Query(26, ImmutableArray<QueryEntry>.Empty);
    g.Store.Query(query2);
    var candidate2 = g.Advance() with { Score = g.Advance().Score with { PointsA = PointScore.Fifteen }, PointId = 2 };
    var award = new RuleEvent(8, RuleEventKind.PointAwarded, 2, 0, Team.A, false, 0, 101, 0);
    Equal(CommitStatus.Accepted, g.Store.Commit(g.Update(query2, 1, 1, candidate2, ImmutableArray<Decision>.Empty) with
    {
        RuleEvents = ImmutableArray.Create(award)
    }).Status);
}

static void TechnicalPause()
{
    var f = Fixture.Create();
    var query = f.Query(27, ImmutableArray<QueryEntry>.Empty);
    f.Store.Query(query);
    var candidate = f.Advance() with { Phase = MatchPhase.Paused };
    var pause = new RuleEvent(8, RuleEventKind.TechnicalPause, 1, 0, null, false, 0, 101, 0);
    Equal(CommitStatus.Accepted, f.Store.Commit(f.Update(query, 1, 1, candidate, ImmutableArray<Decision>.Empty) with
    {
        RuleEvents = ImmutableArray.Create(pause)
    }).Status);
}

static void True(bool value) { if (!value) throw new InvalidOperationException("Expected true."); }
static void False(bool value) { if (value) throw new InvalidOperationException("Expected false."); }
static void Equal<T>(T expected, T actual)
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual))
        throw new InvalidOperationException($"Expected '{expected}', got '{actual}'.");
}

sealed class Fixture
{
    private Fixture(GameStateStore store, ManualTimeProvider clock, WriterRegistration writer, MatchSetup setup, MatchState initial, PlayerBinding firstPlayer)
        => (Store, Clock, Writer, Setup, Initial, FirstPlayer) = (store, clock, writer, setup, initial, firstPlayer);

    public GameStateStore Store { get; }
    public ManualTimeProvider Clock { get; }
    public WriterRegistration Writer { get; }
    public MatchSetup Setup { get; }
    public MatchState Initial { get; }
    public PlayerBinding FirstPlayer { get; }

    public static Fixture Create()
    {
        var gameSession = Guid.NewGuid();
        var trackingSession = Guid.NewGuid();
        var players = Enumerable.Range(1, 4).Select(i => new PlayerBinding((uint)i, (uint)(100 + i), Guid.NewGuid(), trackingSession,
            (uint)i, (uint)(200 + i), 1, 1, true, (uint)(300 + i))).ToImmutableArray();
        var rules = new MatchRules(2, 6, 2, 6, 7, 2, true, ImmutableArray.CreateRange(SHA256.HashData(new byte[] { 42 })));
        var config = new MatchConfiguration(1, 1, players, rules);
        var configBytes = JsonSerializer.SerializeToUtf8Bytes(config);
        var digest = ImmutableArray.CreateRange(SHA256.HashData(configBytes));
        var writer = new WriterRegistration(gameSession, Guid.NewGuid(), 1);
        var setup = new MatchSetup(1, gameSession, 1, config, digest, ImmutableArray.CreateRange(configBytes));
        var initial = new MatchSnapshot(gameSession, 1, 100, 5_000, State(config, digest, 500_000));
        var clock = new ManualTimeProvider();
        var store = new GameStateStore(clock);
        if (!store.RegisterWriter(writer, out var writerError)) throw new InvalidOperationException(writerError);
        if (!store.InstallSetup(setup, out var setupError)) throw new InvalidOperationException(setupError);
        if (!store.InitializeSnapshot(initial, out var stateError)) throw new InvalidOperationException(stateError);
        return new Fixture(store, clock, writer, setup, initial.Match, players[0]);
    }

    public ImmutableArray<QueryEntry> Queries(ulong batch) => Enumerable.Range(0, 8).Select(index =>
    {
        var player = Setup.Configuration.Players[index / 2];
        return new QueryEntry((ulong)(index + 1), new TransactionKey(player.ControllerId,
            player.ControllerSessionId, (ulong)(batch * 10 + (ulong)index + 1)));
    }).ToImmutableArray();

    public BallStateRequest Query(ulong batch, ImmutableArray<QueryEntry> queries) => new(Writer.GameSessionId, Writer.WriterEpoch, batch, queries);

    public MatchState Advance() => Initial with
    {
        Ball = Initial.Ball with { PositionM = new Vector3f(1.25f, 1.0f, 0.5f) },
        StateTimePiUs = Initial.StateTimePiUs + 5_000
    };

    public ImmutableArray<Decision> Decisions(BallStateRequest query) => query.Queries.Select((q, index) =>
    {
        var binding = Setup.Configuration.Players.Single(player => player.ControllerId == q.Transaction.ControllerId);
        return new Decision(q.Transaction, (ulong)(index + 1), 10, binding.PersonId, binding.AssociationVersion,
            500_000, 495_000, DecisionOutcome.Miss, (uint)index, 0,
            new Cue(CueKind.None, 0, 0, 0, false));
    }).ToImmutableArray();

    public StateUpdate Update(BallStateRequest query, ulong baseVersion, ulong tickOffset, MatchState candidate, ImmutableArray<Decision> decisions) =>
        new(query.GameSessionId, query.WriterEpoch, query.BatchId, baseVersion, 100 + tickOffset, FirstPlayer.TrackingSessionId,
            decisions, candidate, ImmutableArray<RuleEvent>.Empty);

    private static MatchState State(MatchConfiguration config, ImmutableArray<byte> digest, ulong time) => new(
        new BallState(new Vector3f(0, 1, 0), new Vector3f(0, 0, 1), 0.033f, 1),
        config.RosterRevision, config.RulesRevision, digest,
        Enumerable.Range(1, 4).Select(i => new AvatarState((uint)i, new Vector3f(i, 0, 0), new Vector3f(0, 0, 0), 0,
            new Vector3f(i, 0, 0), MovementMode.Holding, 0)).ToImmutableArray(),
        new ServeState(ServePhase.Ready, 1, 3, ServiceSide.Deuce, 1, false, false, 0,
            ImmutableArray.Create<uint>(1, 3, 2, 4), ImmutableArray.Create<uint>(1, 2, 3, 4), 0, 0),
        new RallyState(1, 0, null, 0, false, false, 0, 0, 0, null),
        new ScoreState(PointScore.Love, PointScore.Love, 0, 0, 0, 0, false, 0, 0, ImmutableArray<CompletedSet>.Empty),
        true, MatchPhase.Ready, null, 1, time, Guid.NewGuid(), 1);
}

sealed class ManualTimeProvider : TimeProvider
{
    private long _timestamp;
    public override long TimestampFrequency => TimeSpan.TicksPerSecond;
    public override long GetTimestamp() => _timestamp;
    public void Advance(TimeSpan elapsed) => _timestamp += elapsed.Ticks;
}
