using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MotionTennis.GameState;

public sealed class GameStateStore
{
    public const ulong MaxWireUint64 = (1UL << 56) - 1;
    public const int DedupCapacity = 512;
    public static readonly TimeSpan DedupRetention = TimeSpan.FromSeconds(2);

    private readonly object _gate = new();
    private readonly TimeProvider _timeProvider;
    private static readonly JsonSerializerOptions FingerprintOptions = new()
    {
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals
    };
    private readonly Dictionary<BatchKey, BatchRecord> _batches = new();
    private WriterRegistration? _writer;
    private MatchSetup? _setup;
    private MatchSetup? _pendingSetup;
    private MatchSnapshot? _snapshot;
    private bool _paused;

    public GameStateStore(TimeProvider? timeProvider = null) => _timeProvider = timeProvider ?? TimeProvider.System;

    public MatchSnapshot? ReadSnapshot()
    {
        lock (_gate) return _snapshot;
    }

    public bool RegisterWriter(WriterRegistration registration, out string reason)
    {
        lock (_gate)
        {
            if (registration.GameSessionId == Guid.Empty || registration.PhysicsControlSessionId == Guid.Empty ||
                registration.WriterEpoch == 0 || !ValidWireId(registration.WriterEpoch))
                return Fail("Writer registration contains an empty session or out-of-range epoch.", out reason);

            if (_writer is null)
            {
                _writer = registration;
                reason = string.Empty;
                return true;
            }

            if (registration == _writer)
            {
                reason = string.Empty;
                return true;
            }

            if (registration.GameSessionId != _writer.GameSessionId)
                return Fail("A new game session requires a new store instance.", out reason);
            if (registration.WriterEpoch <= _writer.WriterEpoch)
                return Fail("Writer epoch must increase when replacing a physics session.", out reason);
            if (registration.PhysicsControlSessionId == _writer.PhysicsControlSessionId)
                return Fail("A replacement writer must have a new control session.", out reason);

            _writer = registration;
            _batches.Clear();
            _pendingSetup = null;
            _paused = true;
            reason = string.Empty;
            return true;
        }
    }

    public bool InstallSetup(MatchSetup setup, out string reason)
    {
        lock (_gate)
        {
            if (_writer is null || setup.GameSessionId != _writer.GameSessionId || setup.WriterEpoch != _writer.WriterEpoch)
                return Fail("Setup is not fenced by the current registered writer.", out reason);
            if (!ValidWireId(setup.CommandId) || !ValidWireId(setup.WriterEpoch))
                return Fail("Setup command or writer epoch is outside the wire range.", out reason);
            if (setup.CommandId == 0 || setup.Response || setup.Accepted || setup.Reason.Length != 0)
                return Fail("Setup installation requires a positive command request, not a response.", out reason);
            if (!ValidateConfiguration(setup.Configuration, out reason)) return false;
            if (setup.CanonicalConfigurationBytes.IsDefaultOrEmpty || setup.CanonicalConfigurationBytes.Length > 4_096 ||
                setup.ConfigurationSha256.Length != 32 || setup.Reason.Length != 0)
                return Fail("Setup must carry canonical configuration bytes and a 32-byte SHA-256.", out reason);
            var computed = SHA256.HashData(setup.CanonicalConfigurationBytes.AsSpan());
            if (!CryptographicOperations.FixedTimeEquals(computed, setup.ConfigurationSha256.AsSpan()))
                return Fail("Configuration digest does not match its canonical bytes.", out reason);

            if (_setup is not null)
            {
                if (_paused)
                {
                    if (setup.GameSessionId != _setup.GameSessionId ||
                        !_setup.ConfigurationSha256.AsSpan().SequenceEqual(setup.ConfigurationSha256.AsSpan()))
                        return Fail("Replacement writer must reconcile the retained game configuration.", out reason);
                    _pendingSetup = setup;
                    reason = string.Empty;
                    return true;
                }
                if (_setup.CommandId == setup.CommandId && Fingerprint(_setup).SequenceEqual(Fingerprint(setup)))
                {
                    reason = string.Empty;
                    return true;
                }
                return Fail("Setup is immutable for the active session; changed or new commands are rejected.", out reason);
            }

            _setup = setup;
            reason = string.Empty;
            return true;
        }
    }

    public MatchSetup SetupReply(MatchSetup request)
    {
        lock (_gate)
        {
            var reason = string.Empty;
            var accepted = false;
            if (_setup is null || request.GameSessionId != _setup.GameSessionId)
                reason = "wrong game session";
            else if (_writer is null || request.WriterEpoch != _writer.WriterEpoch)
                reason = "writer epoch is fenced";
            else if (request.Response || request.Accepted || request.Reason.Length != 0 ||
                     !Fingerprint(request with { Response = false, Accepted = false, Reason = string.Empty })
                         .SequenceEqual(Fingerprint(_pendingSetup ?? _setup)))
                reason = "setup command or payload differs from retained setup";
            else
                accepted = true;
            return request with { Response = true, Accepted = accepted, Reason = reason };
        }
    }

    public bool ResumeReconciledWriter(MatchSetup acknowledgedSetup, out string reason)
    {
        lock (_gate)
        {
            if (!_paused || _writer is null || _setup is null || _pendingSetup is null)
                return Fail("No replacement writer is waiting for reconciliation.", out reason);
            if (!acknowledgedSetup.Response || !acknowledgedSetup.Accepted || acknowledgedSetup.Reason.Length != 0 ||
                acknowledgedSetup.GameSessionId != _writer.GameSessionId || acknowledgedSetup.WriterEpoch != _writer.WriterEpoch ||
                acknowledgedSetup.CommandId != _pendingSetup.CommandId ||
                !acknowledgedSetup.ConfigurationSha256.AsSpan().SequenceEqual(_setup.ConfigurationSha256.AsSpan()) ||
                !Fingerprint(acknowledgedSetup with { Response = false, Accepted = false, Reason = string.Empty })
                    .SequenceEqual(Fingerprint(_pendingSetup)))
                return Fail("Replacement writer setup acknowledgement does not match the pending configuration and epoch.", out reason);

            _setup = _pendingSetup;
            _pendingSetup = null;
            _paused = false;
            reason = string.Empty;
            return true;
        }
    }

    public bool InitializeSnapshot(MatchSnapshot initialSnapshot, out string reason)
    {
        lock (_gate)
        {
            if (_writer is null || _setup is null)
                return Fail("Register a writer and install setup before initializing state.", out reason);
            if (_snapshot is not null)
                return Fail("The initial snapshot can only be installed once per store.", out reason);
            if (initialSnapshot.GameSessionId != _writer.GameSessionId || initialSnapshot.StateVersion == 0 ||
                !ValidWireId(initialSnapshot.StateVersion) || !ValidWireId(initialSnapshot.SimulationTick))
                return Fail("Initial snapshot must use the registered session and positive bounded version/tick values.", out reason);
            if (initialSnapshot.TickDurationUs != 5_000)
                return Fail("Initial snapshot tick duration must match the pinned 5 ms profile.", out reason);
            if (!ValidateMatch(initialSnapshot.Match, _setup.Configuration, _setup.ConfigurationSha256, out reason)) return false;

            _snapshot = initialSnapshot;
            reason = string.Empty;
            return true;
        }
    }

    public BallStateResponse Query(BallStateRequest request)
    {
        lock (_gate)
        {
            if (_snapshot is null || _writer is null)
                return QueryFailure(request, QueryStatus.NotReady, "No committed snapshot is ready.");
            if (_paused)
                return QueryFailure(request, QueryStatus.Rejected, "Writer needs explicit reconciliation/resume.");
            if (request.GameSessionId != _writer.GameSessionId)
                return QueryFailure(request, QueryStatus.Rejected, "Wrong game session.");
            if (request.WriterEpoch != _writer.WriterEpoch)
                return QueryFailure(request, QueryStatus.Rejected, "Wrong writer epoch.");
            if (request.BatchId == 0 || !ValidWireId(request.BatchId))
                return QueryFailure(request, QueryStatus.Rejected, "Batch ID must be positive and within the wire range.");
            if (!ValidateQueries(request.Queries, out var validationReason))
                return QueryFailure(request, QueryStatus.Rejected, validationReason ?? "Invalid batch/query identity.");
            if (_setup is null || request.Queries.Any(query => !_setup.Configuration.Players.Any(player =>
                    player.ControllerId == query.Transaction.ControllerId && player.ControllerSessionId == query.Transaction.SessionId)))
                return QueryFailure(request, QueryStatus.Rejected, "Query contains a controller session outside the installed roster.");

            PurgeExpired();
            var key = new BatchKey(request.GameSessionId, request.WriterEpoch, request.BatchId);
            var fingerprint = Fingerprint(request);
            if (_batches.TryGetValue(key, out var prior))
            {
                if (!CryptographicOperations.FixedTimeEquals(prior.QueryFingerprint, fingerprint))
                    return QueryFailure(request, QueryStatus.Conflict, "Batch key was reused with different query content.");
                return prior.QueryResponse;
            }
            if (_batches.Count >= DedupCapacity)
                return QueryFailure(request, QueryStatus.Overloaded, "Deduplication retention is full; no unexpired result was evicted.");

            var response = new BallStateResponse(request.GameSessionId, request.WriterEpoch, request.BatchId,
                request.Queries, QueryStatus.Accepted, _snapshot, string.Empty);
            _batches.Add(key, new BatchRecord(_timeProvider.GetTimestamp(), fingerprint, response));
            return response;
        }
    }

    public StateCommitResult Commit(StateUpdate update, IReadOnlyCollection<TransactionKey>? rejectedQueries = null)
    {
        lock (_gate)
        {
            if (_writer is null || update.GameSessionId != _writer.GameSessionId)
                return Result(update, CommitStatus.WrongSession, "Wrong game session.");
            if (update.WriterEpoch != _writer.WriterEpoch)
                return Result(update, CommitStatus.WrongWriter, "Wrong writer epoch.");
            if (_paused)
                return Result(update, CommitStatus.Invalid, "Writer needs explicit reconciliation/resume.");
            if (!ValidWireId(update.BatchId) || !ValidWireId(update.BaseStateVersion) || !ValidWireId(update.TargetSimulationTick))
                return Result(update, CommitStatus.Invalid, "Batch, base version or target tick is outside the wire range.");
            if (update.Decisions.IsDefault || update.Decisions.Length > 8 || update.RuleEvents.IsDefault || update.RuleEvents.Length > 16 ||
                update.CandidateMatch is null || update.CandidateMatch.Avatars.IsDefault || update.CandidateMatch.Avatars.Length != 4 ||
                update.CandidateMatch.Score is null || update.CandidateMatch.Score.CompletedSets.IsDefault || update.CandidateMatch.Score.CompletedSets.Length > 5)
                return Result(update, CommitStatus.Invalid, "Update exceeds a bounded v0.3 candidate collection size.");

            PurgeExpired();
            var key = new BatchKey(update.GameSessionId, update.WriterEpoch, update.BatchId);
            if (!_batches.TryGetValue(key, out var batch))
                return Result(update, CommitStatus.Invalid, "Missing required query for this batch.");

            var rejected = rejectedQueries?.OrderBy(k => k.ControllerId).ThenBy(k => k.SessionId).ThenBy(k => k.RequestSequence).ToArray()
                ?? Array.Empty<TransactionKey>();
            var fingerprint = Fingerprint(new { Update = update, RejectedQueries = rejected });
            if (batch.CommitResult is not null)
            {
                if (!CryptographicOperations.FixedTimeEquals(batch.CommitFingerprint!, fingerprint))
                    return Result(update, CommitStatus.Invalid, "Batch key was reused with different update content.");
                return batch.CommitResult;
            }

            StateCommitResult result;
            if (_snapshot is null || _setup is null)
                result = Result(update, CommitStatus.Invalid, "Store is not initialized.");
            else if (batch.QueryResponse.Status != QueryStatus.Accepted || batch.QueryResponse.Snapshot?.StateVersion != update.BaseStateVersion)
                result = Result(update, CommitStatus.Conflict, "Update base version does not match the snapshot queried for this batch.");
            else if (_snapshot.StateVersion != update.BaseStateVersion)
                result = Result(update, CommitStatus.Conflict, "Update base version is stale.");
            else if (update.TargetSimulationTick <= _snapshot.SimulationTick ||
                     update.TargetSimulationTick > _snapshot.SimulationTick + 4)
                result = Result(update, CommitStatus.Invalid, "Target tick must advance by no more than four fixed steps.");
            else if (!ValidateUpdate(update, batch.QueryResponse, _setup, _snapshot, rejected, out var why))
                result = Result(update, CommitStatus.Invalid, why);
            else if (_snapshot.StateVersion >= MaxWireUint64)
                result = Result(update, CommitStatus.Overloaded, "State version reached the profile's uint64 limit.");
            else
            {
                var committed = new MatchSnapshot(update.GameSessionId, _snapshot.StateVersion + 1,
                    update.TargetSimulationTick, _snapshot.TickDurationUs, update.CandidateMatch);
                _snapshot = committed;
                result = new StateCommitResult(update.GameSessionId, update.WriterEpoch, update.BatchId,
                    CommitStatus.Accepted, committed.StateVersion, committed.SimulationTick, string.Empty);
            }

            batch.CommitFingerprint = fingerprint;
            batch.CommitResult = result;
            return result;
        }
    }

    public StateCommitResult GetCommitStatus(BatchKey key)
    {
        lock (_gate)
        {
            PurgeExpired();
            if (_batches.TryGetValue(key, out var record) && record.CommitResult is not null)
                return record.CommitResult;
            return new StateCommitResult(key.GameSessionId, key.WriterEpoch, key.BatchId,
                CommitStatus.UnknownBatch, _snapshot?.StateVersion ?? 0, _snapshot?.SimulationTick ?? 0,
                "No retained commit result is available; current state does not establish this batch's outcome.");
        }
    }

    private bool ValidateUpdate(StateUpdate update, BallStateResponse query, MatchSetup setup, MatchSnapshot current,
        IReadOnlyCollection<TransactionKey> rejectedQueries, out string reason)
    {
        if (query.BatchId != update.BatchId || query.GameSessionId != update.GameSessionId || query.WriterEpoch != update.WriterEpoch)
            return Fail("Update does not match its retained query envelope.", out reason);
        if (update.Decisions.IsDefault || update.Decisions.Length > 8 || update.RuleEvents.IsDefault || update.RuleEvents.Length > 16)
            return Fail("Decision/rule-event count exceeds the v0.3 bounds.", out reason);
        if (update.TrackingSessionId == Guid.Empty || update.TrackingSessionId != setup.Configuration.Players[0].TrackingSessionId)
            return Fail("Update tracking session does not match the installed roster.", out reason);

        var sourceKeys = query.Queries.Select(q => q.Transaction).ToArray();
        var sourceSet = sourceKeys.ToHashSet();
        var rejectedSet = rejectedQueries.ToHashSet();
        if (rejectedSet.Count != rejectedQueries.Count || !rejectedSet.IsSubsetOf(sourceSet))
            return Fail("Rejected-input accounting must uniquely name source query transactions.", out reason);
        var expectedKeys = sourceKeys.Where(key => !rejectedSet.Contains(key)).ToArray();
        var actualKeys = update.Decisions.Select(decision => decision.Transaction).ToArray();
        if (!actualKeys.SequenceEqual(expectedKeys))
            return Fail("Decisions must exactly preserve surviving source query order.", out reason);

        var perController = new Dictionary<uint, int>();
        for (var i = 0; i < update.Decisions.Length; i++)
        {
            var decision = update.Decisions[i];
            if (!ValidWireId(decision.Transaction.RequestSequence) || decision.Transaction.SessionId == Guid.Empty ||
                decision.CurrentSampleId == 0 || decision.PoseFrameId == 0 || decision.PersonId == 0 || decision.AssociationVersion == 0 ||
                !ValidWireId(decision.CurrentSampleId) || !ValidWireId(decision.PoseFrameId) || !ValidWireId(decision.ImuCaptureUs) ||
                !ValidWireId(decision.PoseCaptureUs) || !ValidWireId(decision.SwingId))
                return Fail("Decision transaction, sample, pose, person or timestamp identity is invalid.", out reason);
            if (decision.EventIndex != (uint)i || decision.Outcome is not (DecisionOutcome.Hit or DecisionOutcome.Miss))
                return Fail("Decisions must be ordered, contiguous and contain only HIT or MISS.", out reason);
            if (!setup.Configuration.Players.Any(p => p.ControllerId == decision.Transaction.ControllerId &&
                    p.ControllerSessionId == decision.Transaction.SessionId && p.PersonId == decision.PersonId &&
                    p.AssociationVersion == decision.AssociationVersion))
                return Fail("Decision controller/person/association does not match the installed roster.", out reason);
            perController[decision.Transaction.ControllerId] = perController.GetValueOrDefault(decision.Transaction.ControllerId) + 1;
            if (perController[decision.Transaction.ControllerId] > 2)
                return Fail("At most two decisions per controller are allowed in a batch.", out reason);
            if (decision.Cue is null || !float.IsFinite(decision.Cue.Intensity) || decision.Cue.Intensity is < 0 or > 1)
                return Fail("Decision cue must be present with finite intensity in [0,1].", out reason);
            if (decision.Cue.ElectricalEligible)
            {
                var expectedCue = decision.Outcome == DecisionOutcome.Hit ? CueKind.Hit : CueKind.Miss;
                if (decision.Cue.CueId == 0 || !ValidWireId(decision.Cue.CueId) || decision.Cue.DurationMs == 0 || decision.Cue.Kind != expectedCue)
                    return Fail("Eligible cue identity, duration or kind does not match the decision outcome.", out reason);
            }
            else if (decision.Cue.Kind != CueKind.None)
                return Fail("Ineligible cue must use CUE_NONE.", out reason);
        }

        ulong previousEventTick = 0;
        for (var i = 0; i < update.RuleEvents.Length; i++)
        {
            var ev = update.RuleEvents[i];
            if (ev.EventIndex != (uint)(8 + i) || !Enum.IsDefined(ev.Kind) || ev.Kind == 0 ||
                ev.SimulationTick < current.SimulationTick || ev.SimulationTick < previousEventTick ||
                ev.SimulationTick > update.TargetSimulationTick || !ValidWireId(ev.PointId) || ev.PointId == 0 ||
                ev.PlayerSlot > 4 || ev.ServeAttempt > 2 || (ev.Team is not null && !Enum.IsDefined(ev.Team.Value)))
                return Fail("Rule events must use contiguous indices 8..23 and valid ticks/kinds.", out reason);
            if (ev.HasCause && ev.CauseDecisionIndex >= update.Decisions.Length)
                return Fail("Rule-event cause must refer to a decision in this batch.", out reason);
            if (!ev.HasCause && ev.CauseDecisionIndex != 0)
                return Fail("Periodic rule events must not invent a decision cause.", out reason);
            if ((ev.Kind is RuleEventKind.PointAwarded or RuleEventKind.GameAwarded or RuleEventKind.SetAwarded or RuleEventKind.MatchAwarded) &&
                ev.Team is not (Team.A or Team.B))
                return Fail("Sporting award events require a winning team.", out reason);
            if (ev.Kind == RuleEventKind.TechnicalPause && ev.Team is not null)
                return Fail("Technical pause events cannot award a team.", out reason);
            previousEventTick = ev.SimulationTick;
        }

        if (!ValidateMatch(update.CandidateMatch, setup.Configuration, setup.ConfigurationSha256, out reason)) return false;
        if (update.CandidateMatch.PiReferenceSessionId != current.Match.PiReferenceSessionId ||
            update.CandidateMatch.PiReferenceClockId != current.Match.PiReferenceClockId)
            return Fail("Pi reference clock cannot change inside a live writer epoch.", out reason);
        if (update.CandidateMatch.StateTimePiUs < current.Match.StateTimePiUs)
            return Fail("Pi reference state time must not move backwards.", out reason);
        if (update.CandidateMatch.PointId < current.Match.PointId)
            return Fail("Point identity must not move backwards.", out reason);
        if (!Fingerprint(update.CandidateMatch.Score).SequenceEqual(Fingerprint(current.Match.Score)) &&
            !update.RuleEvents.Any(ev => ev.Kind is RuleEventKind.PointAwarded or RuleEventKind.GameAwarded or RuleEventKind.SetAwarded or RuleEventKind.MatchAwarded))
            return Fail("Score changes require an explicit sporting award event.", out reason);
        if (update.RuleEvents.Any(ev => ev.Kind == RuleEventKind.TechnicalPause) && update.CandidateMatch.Phase != MatchPhase.Paused)
            return Fail("Technical pause event requires a paused match state.", out reason);
        reason = string.Empty;
        return true;
    }

    private static bool ValidateQueries(ImmutableArray<QueryEntry> queries, out string? reason)
    {
        if (queries.IsDefault || queries.Length > 8) return Fail("Query count exceeds the v0.3 batch bound.", out reason);
        var ids = new HashSet<ulong>();
        var txs = new HashSet<TransactionKey>();
        var perController = new Dictionary<uint, int>();
        foreach (var query in queries)
        {
            if (!ValidWireId(query.QueryId) || query.QueryId == 0 || !ids.Add(query.QueryId) ||
                query.Transaction.ControllerId == 0 || query.Transaction.SessionId == Guid.Empty ||
                !ValidWireId(query.Transaction.RequestSequence) || query.Transaction.RequestSequence == 0 || !txs.Add(query.Transaction))
                return Fail("Query IDs and transaction identities must be nonzero, bounded and unique.", out reason);
            perController[query.Transaction.ControllerId] = perController.GetValueOrDefault(query.Transaction.ControllerId) + 1;
            if (perController[query.Transaction.ControllerId] > 2)
                return Fail("At most two query entries per controller are allowed.", out reason);
        }
        reason = null;
        return true;
    }

    private static bool ValidateConfiguration(MatchConfiguration configuration, out string reason)
    {
        if (configuration.RosterRevision == 0 || configuration.RulesRevision == 0 || configuration.Players.IsDefault || configuration.Players.Length != 4)
            return Fail("Configuration requires positive revisions and exactly four player bindings.", out reason);
        var rules = configuration.Rules;
        if (rules is null || rules.SetsToWin is < 1 or > 3 ||
            (rules.GamesToWinSet, rules.MinimumGameLead, rules.TiebreakAtGames, rules.TiebreakPointsToWin, rules.TiebreakMinimumLead) != (6U, 2U, 6U, 7U, 2U) ||
            !rules.FinalSetTiebreak || rules.TuningSha256.IsDefault || rules.TuningSha256.Length != 32)
            return Fail("Match rules do not match the pinned doubles scoring profile.", out reason);
        var trackingSessions = configuration.Players.Select(p => p.TrackingSessionId).Distinct().Count();
        if (trackingSessions != 1 || !configuration.Players.Select(p => p.PlayerSlot).SequenceEqual(new uint[] { 1, 2, 3, 4 }))
            return Fail("Bindings must be in canonical slot order 1..4 in one tracking session.", out reason);
        if (configuration.Players.Any(p => p.ControllerId == 0 || p.ControllerSessionId == Guid.Empty || p.TrackingSessionId == Guid.Empty ||
                p.PersonId == 0 || p.AvatarId == 0 || p.AssociationVersion == 0 || p.CalibrationId == 0 || p.PhysicalStationId == 0))
            return Fail("Every player binding must have nonzero identity and revision fields.", out reason);
        if (configuration.Players.Select(p => p.ControllerId).Distinct().Count() != 4 ||
            configuration.Players.Select(p => (p.TrackingSessionId, p.PersonId)).Distinct().Count() != 4 ||
            configuration.Players.Select(p => p.AvatarId).Distinct().Count() != 4 ||
            configuration.Players.Select(p => p.PhysicalStationId).Distinct().Count() != 4)
            return Fail("Controller, session, person, avatar and station bindings must be unique.", out reason);
        reason = string.Empty;
        return true;
    }

    private static bool ValidateMatch(MatchState match, MatchConfiguration configuration, ImmutableArray<byte> digest, out string reason)
    {
        if (match.Ball is null || match.Serve is null || match.Rally is null || match.Score is null ||
            match.Avatars.IsDefault || match.Avatars.Length != 4 || match.Score.CompletedSets.IsDefault || match.Score.CompletedSets.Length > 5)
            return Fail("Match candidate is missing required state or exceeds collection bounds.", out reason);
        if (match.RosterRevision != configuration.RosterRevision || match.RulesRevision != configuration.RulesRevision ||
            !match.ConfigurationSha256.AsSpan().SequenceEqual(digest.AsSpan()))
            return Fail("Candidate configuration digest or revisions do not match installed setup.", out reason);
        if (!ValidVector(match.Ball.PositionM) || !ValidVector(match.Ball.VelocityMps) || !float.IsFinite(match.Ball.RadiusM) || match.Ball.RadiusM <= 0 ||
            match.Ball.CalibrationId == 0 || match.PiReferenceSessionId == Guid.Empty || match.PiReferenceClockId == 0 || !ValidWireId(match.StateTimePiUs))
            return Fail("Ball geometry or Pi clock metadata is invalid.", out reason);
        if (!Enum.IsDefined(match.Phase) || match.Phase == 0 || !Enum.IsDefined(match.Serve.Phase) || match.Serve.Phase == 0 ||
            !Enum.IsDefined(match.Serve.Side) || match.Serve.Side == 0 || !Enum.IsDefined(match.Score.PointsA) || !Enum.IsDefined(match.Score.PointsB))
            return Fail("Match contains an unspecified or unknown phase/score enum.", out reason);
        if (!match.Avatars.Select(a => a.PlayerSlot).SequenceEqual(new uint[] { 1, 2, 3, 4 }))
            return Fail("Candidate must contain avatars in canonical slot order 1..4.", out reason);
        foreach (var avatar in match.Avatars)
        {
            if (!ValidVector(avatar.PositionM) || !ValidVector(avatar.VelocityMps) || !ValidVector(avatar.TargetM) || !float.IsFinite(avatar.HeadingRad) ||
                !Enum.IsDefined(avatar.Movement) || avatar.Movement == 0 || avatar.CoverageOwnerSlot > 4 ||
                (avatar.CoverageOwnerSlot != 0 && TeamForSlot(avatar.CoverageOwnerSlot) != TeamForSlot(avatar.PlayerSlot)))
                return Fail("Avatar state contains nonfinite geometry or invalid movement/coverage data.", out reason);
        }
        if (match.Serve.ServerSlot is < 1 or > 4 || match.Serve.ReceiverSlot is < 1 or > 4 || match.Serve.ServerSlot == match.Serve.ReceiverSlot ||
            match.Serve.Attempt is < 1 or > 2 || !ValidServiceOrder(match.Serve.ServiceOrder) || !ValidReceivingOrder(match.Serve.ReceivingOrder) ||
            match.Serve.ServiceTurnIndex > 3 || match.Serve.ServerSlot != match.Serve.ServiceOrder[(int)match.Serve.ServiceTurnIndex] ||
            TeamForSlot(match.Serve.ServerSlot) == TeamForSlot(match.Serve.ReceiverSlot) ||
            (match.Serve.TiebreakFirstServerSlot > 4) ||
            (match.Score.Tiebreak && match.Serve.TiebreakFirstServerSlot == 0) ||
            (match.Serve.TossId > MaxWireUint64))
            return Fail("Serve state contains an invalid player, attempt or order.", out reason);
        var receivingIndex = (TeamForSlot(match.Serve.ServerSlot) == Team.A ? 2 : 0) + (match.Serve.Side == ServiceSide.Ad ? 1 : 0);
        if (match.Serve.ReceiverSlot != match.Serve.ReceivingOrder[receivingIndex] ||
            ((match.Serve.Phase is ServePhase.Toss or ServePhase.Contact or ServePhase.InFlight) && match.Serve.TossId == 0))
            return Fail("Serve receiver/order or toss identity is inconsistent.", out reason);
        if (match.Rally.LastHitterSlot > 4 || match.Rally.BounceCount > 2 || match.Rally.ContactSlotMask > 15 ||
            (match.Rally.LastBounceTeam is not null && !Enum.IsDefined(match.Rally.LastBounceTeam.Value)) ||
            (match.Rally.PointWinner is not null && !Enum.IsDefined(match.Rally.PointWinner.Value)))
            return Fail("Rally state contains an invalid slot, team or bounce count.", out reason);
        if (!ValidWireId(match.Rally.RallyId) || !ValidWireId(match.Rally.ContactId) || !ValidWireId(match.Rally.BounceId) ||
            !ValidWireId(match.PointId) || match.PointId == 0 || match.PiReferenceSessionId == Guid.Empty || match.PiReferenceClockId == 0)
            return Fail("Match event or clock identity is missing or outside the wire range.", out reason);
        if (match.Score.PointsA == PointScore.Advantage && match.Score.PointsB != PointScore.Forty ||
            match.Score.PointsB == PointScore.Advantage && match.Score.PointsA != PointScore.Forty)
            return Fail("Advantage requires the opposing side to remain at forty.", out reason);
        if (match.Score.CompletedSets.Length > 2 * configuration.Rules.SetsToWin - 1 ||
            match.Score.GamesA > 7 || match.Score.GamesB > 7 ||
            match.Score.TiebreakPointsA > 99 || match.Score.TiebreakPointsB > 99)
            return Fail("Score values exceed the pinned match bounds.", out reason);
        var setWinsA = 0U;
        var setWinsB = 0U;
        foreach (var completed in match.Score.CompletedSets)
        {
            if (setWinsA >= configuration.Rules.SetsToWin || setWinsB >= configuration.Rules.SetsToWin)
                return Fail("A set is recorded after match completion.", out reason);
            var high = Math.Max(completed.GamesA, completed.GamesB);
            var low = Math.Min(completed.GamesA, completed.GamesB);
            if (!((high == 6 && low <= 4) || (high == 7 && low is 5 or 6)) || completed.HadTiebreak != (low == 6))
                return Fail("Completed set score or tiebreak marker is invalid.", out reason);
            if (completed.HadTiebreak)
            {
                var highPoints = Math.Max(completed.TiebreakPointsA, completed.TiebreakPointsB);
                var lowPoints = Math.Min(completed.TiebreakPointsA, completed.TiebreakPointsB);
                if (highPoints != Math.Max(7UL, lowPoints + 2) ||
                    (completed.GamesA > completed.GamesB) != (completed.TiebreakPointsA > completed.TiebreakPointsB))
                    return Fail("Completed tiebreak score or winner is invalid.", out reason);
            }
            else if (completed.TiebreakPointsA != 0 || completed.TiebreakPointsB != 0)
                return Fail("A non-tiebreak set cannot carry tiebreak points.", out reason);
            if (completed.GamesA > completed.GamesB) setWinsA++; else setWinsB++;
        }
        if (match.Score.SetsA != setWinsA || match.Score.SetsB != setWinsB)
            return Fail("Set totals disagree with completed set history.", out reason);
        if (match.Score.Tiebreak)
        {
            if (match.Score.GamesA != 6 || match.Score.GamesB != 6 ||
                match.Score.PointsA != PointScore.Love || match.Score.PointsB != PointScore.Love)
                return Fail("Tiebreak requires 6-all games and LOVE standard points.", out reason);
        }
        else if (match.Score.TiebreakPointsA != 0 || match.Score.TiebreakPointsB != 0)
            return Fail("Standard game state cannot carry tiebreak points.", out reason);
        var completedWinner = match.Score.SetsA == configuration.Rules.SetsToWin ? Team.A :
            match.Score.SetsB == configuration.Rules.SetsToWin ? Team.B : (Team?)null;
        if ((match.Phase == MatchPhase.Finished) != (completedWinner is not null) || match.Winner != completedWinner)
            return Fail("Match completion/winner disagrees with completed set totals.", out reason);
        reason = string.Empty;
        return true;
    }

    private static bool ValidOrder(ImmutableArray<uint> order) => !order.IsDefault && order.Length == 4 && order.Order().SequenceEqual(new uint[] { 1, 2, 3, 4 });
    private static bool ValidServiceOrder(ImmutableArray<uint> order) => ValidOrder(order) &&
        Enumerable.Range(0, 4).All(i => TeamForSlot(order[i]) != TeamForSlot(order[(i + 1) % 4]));
    private static bool ValidReceivingOrder(ImmutableArray<uint> order) => !order.IsDefault && order.Length == 4 &&
        order.Take(2).Order().SequenceEqual(new uint[] { 1, 2 }) && order.Skip(2).Order().SequenceEqual(new uint[] { 3, 4 });
    private static Team TeamForSlot(uint slot) => slot is 1 or 2 ? Team.A : Team.B;
    private static bool ValidVector(Vector3f value) => float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);
    private static bool ValidWireId(ulong value) => value <= MaxWireUint64;
    private static bool Fail(string message, out string reason) { reason = message; return false; }
    private static byte[] Fingerprint<T>(T value) => SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(value, FingerprintOptions));
    private static BallStateResponse QueryFailure(BallStateRequest request, QueryStatus status, string reason) =>
        new(request.GameSessionId, request.WriterEpoch, request.BatchId, request.Queries.IsDefault ? ImmutableArray<QueryEntry>.Empty : request.Queries, status, null, BoundReason(reason));
    private StateCommitResult Result(StateUpdate update, CommitStatus status, string reason) =>
        new(update.GameSessionId, update.WriterEpoch, update.BatchId, status,
            _snapshot?.StateVersion ?? 0, _snapshot?.SimulationTick ?? 0, BoundReason(reason));

    private static string BoundReason(string reason)
    {
        if (System.Text.Encoding.UTF8.GetByteCount(reason) <= 128) return reason;
        var chars = reason.AsSpan();
        var length = chars.Length;
        while (length > 0 && System.Text.Encoding.UTF8.GetByteCount(chars[..length]) > 125) length--;
        return string.Concat(chars[..length], "...");
    }

    private void PurgeExpired()
    {
        var now = _timeProvider.GetTimestamp();
        foreach (var key in _batches.Where(pair => _timeProvider.GetElapsedTime(pair.Value.CreatedAt, now) >= DedupRetention).Select(pair => pair.Key).ToArray())
            _batches.Remove(key);
    }

    private sealed class BatchRecord(long createdAt, byte[] queryFingerprint, BallStateResponse queryResponse)
    {
        public long CreatedAt { get; } = createdAt;
        public byte[] QueryFingerprint { get; } = queryFingerprint;
        public BallStateResponse QueryResponse { get; } = queryResponse;
        public byte[]? CommitFingerprint { get; set; }
        public StateCommitResult? CommitResult { get; set; }
    }
}
