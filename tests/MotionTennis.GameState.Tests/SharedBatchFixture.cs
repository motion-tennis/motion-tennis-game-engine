using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using MotionTennis.GameState;

internal sealed record FailedInput(ulong QueryId, TransactionKey Transaction, uint Outcome, ulong ObservedPiUs);

internal sealed record SharedBatchFixture(
    WriterRegistration Writer, MatchSetup Setup, MatchSnapshot Snapshot, BallStateRequest Query,
    StateUpdate Update, ImmutableArray<FailedInput> Rejected)
{
    // The shared runner maps decoded v0.3 messages into these existing C# records.
    // UUID strings use RFC network order, including the controller session in each transaction.
    public static void Verify(string path)
    {
        using var input = File.OpenRead(path);
        var fixture = JsonSerializer.Deserialize<SharedBatchFixture>(input, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
        }) ?? throw new InvalidDataException("Empty shared fixture.");
        fixture.Run();
    }

    private void Run()
    {
        Check(Query.Queries.Length == 3 && Rejected.Length == 1 && Update.Decisions.Length == 2,
            "Expected three queried inputs, one rejection and two surviving decisions.");
        var failed = Rejected[0];
        var middle = Query.Queries[1];
        const uint staleInput = 5; // motion.v03.Outcome.STALE_INPUT
        Check(failed.QueryId == middle.QueryId && failed.Transaction == middle.Transaction &&
              failed.Outcome == staleInput && failed.ObservedPiUs > Snapshot.Match.StateTimePiUs,
            "The stale-input evidence must name the middle query and its Pi observation time.");
        var hit = Update.Decisions[0];
        var miss = Update.Decisions[1];
        Check(hit.Transaction == Query.Queries[0].Transaction && hit.EventIndex == 0 && hit.Outcome == DecisionOutcome.Hit &&
              miss.Transaction == Query.Queries[2].Transaction && miss.EventIndex == 1 && miss.Outcome == DecisionOutcome.Miss,
            "Expected the first and third query to survive in order as HIT and MISS.");

        TransactionKey[] rejected = [failed.Transaction];
        var cases = new List<string>();
        void ExpectInvalid(string name, StateUpdate update, TransactionKey[] rejectedKeys)
        {
            // The store retains the first commit result for a batch, including a rejection.
            var independent = OpenStore();
            var result = independent.Commit(update, rejectedKeys);
            Check(result.Status == CommitStatus.Invalid && SameSnapshot(independent.ReadSnapshot(), Snapshot) &&
                  ReadResult(independent) == result, $"{name} must reject and preserve the complete base snapshot.");
            cases.Add(name);
        }

        ExpectInvalid("missing_rejection", Update, []);
        ExpectInvalid("wrong_rejection", Update, [hit.Transaction]);
        ExpectInvalid("duplicate_rejection", Update, [failed.Transaction, failed.Transaction]);
        ExpectInvalid("missing_survivor", Update with { Decisions = [hit] }, rejected);
        ExpectInvalid("reordered_survivors", Update with
        {
            Decisions = [miss with { EventIndex = 0 }, hit with { EventIndex = 1 }]
        }, rejected);

        var store = OpenStore();
        var accepted = store.Commit(Update, rejected);
        var expected = new MatchSnapshot(Query.GameSessionId, Snapshot.StateVersion + 1,
            Update.TargetSimulationTick, Snapshot.TickDurationUs, Update.CandidateMatch);
        Check(accepted.Status == CommitStatus.Accepted, $"Game rejected the genuine physics batch: {accepted.Reason}");
        Check(accepted.StateVersion == expected.StateVersion && accepted.SimulationTick == expected.SimulationTick &&
              accepted.GameSessionId == Query.GameSessionId && accepted.WriterEpoch == Query.WriterEpoch &&
              accepted.BatchId == Query.BatchId && SameSnapshot(store.ReadSnapshot(), expected) &&
              expected.Match.Rally.ContactId == Snapshot.Match.Rally.ContactId + 1,
            "The committed result must identify this batch and install the entire rebuilt candidate with one new contact.");
        Check(store.Commit(Update, rejected) == accepted && SameSnapshot(store.ReadSnapshot(), expected),
            "An identical retry must return the original outcome and preserve the committed version.");
        Check(store.Commit(Update, []).Status == CommitStatus.Invalid && ReadResult(store) == accepted &&
              SameSnapshot(store.ReadSnapshot(), expected), "Changed retry accounting must preserve the original result and state.");
        cases.Add("changed_retry_accounting");

        Console.WriteLine(JsonSerializer.Serialize(new
        {
            status = "passed",
            game_session_id = accepted.GameSessionId,
            writer_epoch = accepted.WriterEpoch,
            batch_id = accepted.BatchId,
            state_version = accepted.StateVersion,
            simulation_tick = accepted.SimulationTick,
            decision_count = Update.Decisions.Length,
            rejected_count = Rejected.Length,
            accepted_decisions = Update.Decisions.Select(d => new { d.EventIndex, d.Transaction, d.Outcome }),
            rejected = Rejected,
            negative_cases = cases
        }));
    }

    private GameStateStore OpenStore()
    {
        var store = new GameStateStore(new ManualTimeProvider());
        Check(store.RegisterWriter(Writer, out var reason), reason);
        Check(store.InstallSetup(Setup, out reason), reason);
        Check(store.InitializeSnapshot(Snapshot, out reason), reason);
        var response = store.Query(Query);
        Check(response.Status == QueryStatus.Accepted && SameSnapshot(response.Snapshot, Snapshot) &&
              response.GameSessionId == Query.GameSessionId && response.WriterEpoch == Query.WriterEpoch &&
              response.BatchId == Query.BatchId && response.Queries.SequenceEqual(Query.Queries),
            $"Game must echo the physics query and return the complete base snapshot. {response.Reason}");
        return store;
    }

    private StateCommitResult ReadResult(GameStateStore store) =>
        store.GetCommitStatus(new BatchKey(Query.GameSessionId, Query.WriterEpoch, Query.BatchId));

    // Records containing ImmutableArray<T> compare arrays by storage identity. Compare field values here.
    private static bool SameSnapshot(MatchSnapshot? actual, MatchSnapshot expected) =>
        JsonNode.DeepEquals(JsonSerializer.SerializeToNode(actual), JsonSerializer.SerializeToNode(expected));

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidDataException(message);
    }
}
