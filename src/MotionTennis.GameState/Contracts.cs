using System.Collections.Immutable;

namespace MotionTennis.GameState;

public readonly record struct Vector3f(float X, float Y, float Z);

public enum MatchPhase { Ready = 1, Playing = 2, Paused = 3, Finished = 4 }
public enum MovementMode { Holding = 1, Serving = 2, Receiving = 3, Covering = 4, Intercepting = 5 }
public enum ServePhase { Ready = 1, Lift = 2, Toss = 3, Contact = 4, InFlight = 5, Rally = 6, Fault = 7, Let = 8, Interrupted = 9 }
public enum ServiceSide { Deuce = 1, Ad = 2 }
public enum PointScore { Love = 0, Fifteen = 1, Thirty = 2, Forty = 3, Advantage = 4 }
public enum Team { A = 1, B = 2 }
public enum DecisionOutcome { Hit = 1, Miss = 2 }
public enum CueKind { None = 0, Hit = 1, Miss = 2, Fault = 3 }
public enum RuleEventKind
{
    TossStarted = 1, TossCancelled = 2, ServeAttempt = 3, LegalContact = 4,
    BallBounce = 5, BallOut = 6, ServeFault = 7, ServeLet = 8,
    PointAwarded = 9, GameAwarded = 10, SetAwarded = 11, MatchAwarded = 12,
    EndsChanged = 13, TechnicalPause = 14, PlayResumed = 15
}

public sealed record PlayerBinding(
    uint PlayerSlot,
    uint ControllerId,
    Guid ControllerSessionId,
    Guid TrackingSessionId,
    uint PersonId,
    uint AvatarId,
    uint AssociationVersion,
    uint CalibrationId,
    bool RightHand,
    uint PhysicalStationId);

public sealed record MatchRules(
    uint SetsToWin,
    uint GamesToWinSet,
    uint MinimumGameLead,
    uint TiebreakAtGames,
    uint TiebreakPointsToWin,
    uint TiebreakMinimumLead,
    bool FinalSetTiebreak,
    ImmutableArray<byte> TuningSha256);

public sealed record MatchConfiguration(
    uint RosterRevision,
    uint RulesRevision,
    ImmutableArray<PlayerBinding> Players,
    MatchRules Rules);

public sealed record MatchSetup(
    ulong CommandId,
    Guid GameSessionId,
    ulong WriterEpoch,
    MatchConfiguration Configuration,
    ImmutableArray<byte> ConfigurationSha256,
    ImmutableArray<byte> CanonicalConfigurationBytes,
    bool Response = false,
    bool Accepted = false,
    string Reason = "");

public sealed record BallState(
    Vector3f PositionM,
    Vector3f VelocityMps,
    float RadiusM,
    uint CalibrationId);

public sealed record AvatarState(
    uint PlayerSlot,
    Vector3f PositionM,
    Vector3f VelocityMps,
    float HeadingRad,
    Vector3f TargetM,
    MovementMode Movement,
    uint CoverageOwnerSlot);

public sealed record ServeState(
    ServePhase Phase,
    uint ServerSlot,
    uint ReceiverSlot,
    ServiceSide Side,
    uint Attempt,
    bool StrokeAttempted,
    bool NetTouched,
    ulong TossId,
    ImmutableArray<uint> ServiceOrder,
    ImmutableArray<uint> ReceivingOrder,
    uint ServiceTurnIndex,
    uint TiebreakFirstServerSlot);

public sealed record RallyState(
    ulong RallyId,
    uint LastHitterSlot,
    Team? LastBounceTeam,
    uint BounceCount,
    bool NetTouched,
    bool ServiceReturnPending,
    uint ContactSlotMask,
    ulong ContactId,
    ulong BounceId,
    Team? PointWinner);

public sealed record CompletedSet(
    uint GamesA,
    uint GamesB,
    bool HadTiebreak,
    ulong TiebreakPointsA,
    ulong TiebreakPointsB);

public sealed record ScoreState(
    PointScore PointsA,
    PointScore PointsB,
    uint GamesA,
    uint GamesB,
    uint SetsA,
    uint SetsB,
    bool Tiebreak,
    ulong TiebreakPointsA,
    ulong TiebreakPointsB,
    ImmutableArray<CompletedSet> CompletedSets);

public sealed record MatchState(
    BallState Ball,
    uint RosterRevision,
    uint RulesRevision,
    ImmutableArray<byte> ConfigurationSha256,
    ImmutableArray<AvatarState> Avatars,
    ServeState Serve,
    RallyState Rally,
    ScoreState Score,
    bool TeamANearEnd,
    MatchPhase Phase,
    Team? Winner,
    ulong PointId,
    ulong StateTimePiUs,
    Guid PiReferenceSessionId,
    uint PiReferenceClockId);

public sealed record MatchSnapshot(Guid GameSessionId, ulong StateVersion, ulong SimulationTick, uint TickDurationUs, MatchState Match);

public sealed record WriterRegistration(Guid GameSessionId, Guid PhysicsControlSessionId, ulong WriterEpoch);

public readonly record struct TransactionKey(uint ControllerId, Guid SessionId, ulong RequestSequence);
public sealed record QueryEntry(ulong QueryId, TransactionKey Transaction);
public sealed record BallStateRequest(Guid GameSessionId, ulong WriterEpoch, ulong BatchId, ImmutableArray<QueryEntry> Queries);
public enum QueryStatus { Accepted = 1, Rejected = 2, Conflict = 3, Overloaded = 4, NotReady = 5 }
public sealed record BallStateResponse(
    Guid GameSessionId,
    ulong WriterEpoch,
    ulong BatchId,
    ImmutableArray<QueryEntry> Queries,
    QueryStatus Status,
    MatchSnapshot? Snapshot,
    string Reason);

public sealed record Decision(
    TransactionKey Transaction,
    ulong CurrentSampleId,
    ulong PoseFrameId,
    uint PersonId,
    uint AssociationVersion,
    ulong ImuCaptureUs,
    ulong PoseCaptureUs,
    DecisionOutcome Outcome,
    uint EventIndex,
    ulong SwingId,
    Cue Cue);

public sealed record Cue(CueKind Kind, ulong CueId, float Intensity, uint DurationMs, bool ElectricalEligible);

public sealed record RuleEvent(
    uint EventIndex,
    RuleEventKind Kind,
    ulong PointId,
    uint PlayerSlot,
    Team? Team,
    bool HasCause,
    uint CauseDecisionIndex,
    ulong SimulationTick,
    uint ServeAttempt);

public sealed record StateUpdate(
    Guid GameSessionId,
    ulong WriterEpoch,
    ulong BatchId,
    ulong BaseStateVersion,
    ulong TargetSimulationTick,
    Guid TrackingSessionId,
    ImmutableArray<Decision> Decisions,
    MatchState CandidateMatch,
    ImmutableArray<RuleEvent> RuleEvents);

public enum CommitStatus { Accepted = 1, Conflict = 2, Invalid = 3, WrongSession = 4, WrongWriter = 5, UnknownBatch = 6, Overloaded = 7 }
public sealed record StateCommitResult(
    Guid GameSessionId,
    ulong WriterEpoch,
    ulong BatchId,
    CommitStatus Status,
    ulong StateVersion,
    ulong SimulationTick,
    string Reason);

public readonly record struct BatchKey(Guid GameSessionId, ulong WriterEpoch, ulong BatchId);
