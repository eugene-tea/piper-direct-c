using LuckyRobots.Piper.Pika;

namespace LuckyRobots.Piper.Plugin;

/// <summary>Physical progress follows a closed continuous phase, independently
/// of the source renderer's cadence. Endpoint barriers order every jaw event.</summary>
public sealed class OrderedSimulationExecution
{
    /// <summary>Lost contact must settle at the unchanged close command within this time.</summary>
    private const double ContactLossDeadlineSeconds = .250;
    /// <summary>How long a jaw reading must stay outside its allowed band before following stops.
    /// Bad Pika bursts of ~10–90 ms were observed (an open jaw reading exactly 0 mm); 250 ms matches the
    /// contact-loss and source-lease windows, so a real slip still stops within a quarter second.</summary>
    internal const double JawGlitchToleranceSeconds = .250;
    private double jawViolationBegan = double.NaN;
    public OrderedSimulationPath Path { get; }
    public OrderedSimulationPhase? ActivePhase { get; private set; }
    public OrderedSimulationTarget Target { get; private set; }
    public ulong CompletedCursor { get; private set; }
    public double ElapsedSeconds { get; private set; }
    private double phaseBegan, settledBegan = double.NaN, settledFeedback;
    private double lastNow = double.NaN;
    private double expectedOpening;
    private bool settledUnconfirmedCandidate;
    private ulong completedHomeCursor;
    private string completedHomeEpisode = "";
    private double? lastClosingCommand, completedOpeningCommand;
    internal JawReference jaw = JawReference.None;
    public bool QualifyingLostContact => jaw.Kind == JawReferenceKind.LosingContact;
    public bool PhysicalGraspMissed { get; private set; }
    public bool MissedGrasp => PhysicalGraspMissed || Path.SourceMissedGrasp;
    public double? QualifiedContactOpeningMm => jaw.Kind is JawReferenceKind.Contact or JawReferenceKind.LosingContact ? jaw.Opening : null;
    public double ExpectedMeasuredOpeningMm => jaw.Resolved ? jaw.Opening : (ActivePhase is { } phase && Opening(phase)
        ? phase.End.CommandOpeningMm : ActivePhase?.ExpectedMeasuredOpeningMm ?? expectedOpening);
    public bool RequiresPhysicalContactCandidate => ActivePhase is { } phase && ClosingContact(phase);
    public double OpeningArrivalToleranceMm => RequiresPhysicalContactCandidate ? 5 : 2;
    private (long Request, long Sequence)? native; // Null until the first scoped reference.
    private readonly string limits;

    public OrderedSimulationExecution(SimulationFollowSample first, SimulationFollowHardware hardware, double selectedSpeedFraction)
    {
        Path = new(hardware.JointLimits, selectedSpeedFraction);
        Target = new(Array.AsReadOnly(first.CommandDegrees.ToArray()), first.CommandOpeningMm);
        expectedOpening = first.MeasuredOpeningMm;
        limits = Signature(hardware);
    }

    public OrderedSimulationTarget Step(double nowSeconds, SimulationFollowHardware hardware, bool armStationary,
        double gripperSpeed, double gripperFeedbackTime, bool startNextPhase = true)
    {
        if (!double.IsFinite(nowSeconds) || !double.IsFinite(gripperSpeed) || !double.IsFinite(gripperFeedbackTime) ||
            !double.IsNaN(lastNow) && nowSeconds < lastNow || Signature(hardware) != limits || !hardware.FeedbackFresh || !hardware.GripperFresh || !hardware.LimitsFresh ||
            hardware.JointDegrees.Count != 6 || hardware.JointDegrees.Any(q => !double.IsFinite(q)) || !double.IsFinite(hardware.GripperOpeningMm))
            throw new InvalidOperationException("The physical follower clock, connection or reported limits changed.");
        var reference = hardware.NativeReference;
        if (reference is null || reference.DeviceId != hardware.DeviceId || reference.Generation != hardware.Generation || reference.RequestId <= 0 ||
            native is { } seen && (reference.RequestId != seen.Request || reference.Sequence < seen.Sequence) || reference.Sequence < 0 ||
            reference.JointDegrees.Count != 6 || reference.JointDegrees.Where((q, joint) => !double.IsFinite(q) ||
                q < hardware.JointLimits[joint].MinimumDegrees || q > hardware.JointLimits[joint].MaximumDegrees).Any())
            throw new InvalidOperationException("The scoped native trajectory reference is unavailable or changed; following stopped.");
        native = (reference.RequestId, reference.Sequence);
        lastNow = nowSeconds;
        if (startNextPhase && ActivePhase is null && Path.Current is { } next)
        {
            if (QualifyingLostContact && (next.Kind == OrderedSimulationPhaseKind.Gripper || next.Frames.Any(frame => frame.CommandOpeningMm != jaw.Command)))
                throw new InvalidOperationException("The jaw command changed before lost contact could be verified; following stopped.");
            // A contact baseline belongs to exactly one closed jaw command.
            // A new jaw operation must qualify its own measured endpoint.
            if (next.Kind == OrderedSimulationPhaseKind.Gripper || jaw.Resolved && next.Frames.Any(frame => frame.CommandOpeningMm != jaw.Command))
                jaw = JawReference.None;
            if (next.Kind == OrderedSimulationPhaseKind.Gripper)
            {
                completedOpeningCommand = null;
                if (next.End.CommandOpeningMm < next.Frames[0].CommandOpeningMm) lastClosingCommand = next.End.CommandOpeningMm;
            }
            ActivePhase = next; phaseBegan = nowSeconds; settledBegan = double.NaN;
        }
        var phase = ActivePhase;
        if (phase is not null) { ElapsedSeconds = nowSeconds - phaseBegan; Target = phase.At(ElapsedSeconds); }
        // A changed command retires its reference; while losing contact,
        // UpdateContactLoss rejects the change instead.
        if (jaw.Resolved && jaw.Kind != JawReferenceKind.LosingContact && Target.OpeningMm != jaw.Command) jaw = JawReference.None;
        for (int joint = 0; joint < 6; joint++)
            if (Math.Abs(reference.JointDegrees[joint] - hardware.JointDegrees[joint]) > SimulationFollowPolicy.TrackingJointToleranceDegrees)
                throw new InvalidOperationException($"Real J{joint + 1} is more than {SimulationFollowPolicy.TrackingJointToleranceDegrees:0}° from the controller's transmitted reference. Following stopped.");
        UpdateContactLoss(nowSeconds, hardware.GripperOpeningMm, gripperSpeed, gripperFeedbackTime);
        double expected = ExpectedMeasuredOpeningMm;
        double sourceExpected = phase?.ExpectedMeasuredOpeningMm ?? expectedOpening;
        double low = sourceExpected, high = sourceExpected;
        if (phase is { Kind: OrderedSimulationPhaseKind.Gripper })
        {
            low = phase.Frames.Min(f => Math.Min(f.MeasuredOpeningMm, f.CommandOpeningMm));
            high = phase.Frames.Max(f => Math.Max(f.MeasuredOpeningMm, f.CommandOpeningMm));
        }
        else if (QualifyingLostContact)
        {
            low = jaw.Command - PikaProtocol.ArrivalToleranceMm;
            high = jaw.Opening;
        }
        else if (MissedGrasp && jaw.Resolved && jaw.Command == Target.OpeningMm)
        {
            // An explicitly missed trial can have empty real jaws while the
            // simulation still has contact, or the reverse. Its settled,
            // unchanged physical jaw reference remains authoritative while
            // finishing the original path. The stricter 2mm continuity guard
            // below still detects any actual change from that reference.
            low = high = jaw.Opening;
        }
        bool outsidePhase = hardware.GripperOpeningMm < low - SimulationFollowPolicy.TrackingGripperToleranceMm ||
            hardware.GripperOpeningMm > high + SimulationFollowPolicy.TrackingGripperToleranceMm;
        bool leftReference = jaw.Resolved && !QualifyingLostContact && Math.Abs(hardware.GripperOpeningMm - jaw.Opening) > 2;
        if (outsidePhase || leftReference)
        {
            // A Pika can report a single burst of impossible readings (observed: an idle open jaw
            // reading 79 mm, then exactly 0 mm for ~10 ms, then 79 mm again). Only a jaw reading that
            // stays wrong for JawGlitchToleranceSeconds stops following; until then the sample is
            // not used for anything, so it can neither complete a phase nor set a jaw reference.
            if (double.IsNaN(jawViolationBegan)) jawViolationBegan = nowSeconds;
            if (nowSeconds - jawViolationBegan < JawGlitchToleranceSeconds) { settledBegan = double.NaN; return Target; }
            string persisted = $" Jaw stayed wrong for {(nowSeconds - jawViolationBegan) * 1000:0} ms.";
            if (outsidePhase)
                throw new InvalidOperationException($"Real Pika is more than {SimulationFollowPolicy.TrackingGripperToleranceMm:0} mm from its retimed jaw phase. Following stopped." +
                    $" Jaw {hardware.GripperOpeningMm:0.0} mm, simulation range {low:0.0}–{high:0.0} mm, commanded {Target.OpeningMm:0.0} mm" +
                    (phase is null ? "." : $", phase \"{phase.End.Phase}\".") + persisted);
            throw new InvalidOperationException("Pika's measured opening changed more than 2 mm from its settled jaw reference. Following stopped." +
                $" Jaw {hardware.GripperOpeningMm:0.0} mm, reference {jaw.Opening:0.0} mm." + persisted);
        }
        jawViolationBegan = double.NaN;
        if (phase is null) return Target;
        // A closing command intentionally can lead its measured contact opening.
        // Opening resolves to its command; neither aperture check proves a grasp.
        bool unconfirmedClose = ClosingContact(phase) && UnconfirmedOpening(hardware.GripperOpeningMm, phase.End.CommandOpeningMm);
        bool arrived = !QualifyingLostContact && ElapsedSeconds >= phase.DurationSeconds && armStationary &&
            Target.JointDegrees.Zip(hardware.JointDegrees, (target, actual) => Math.Abs(target - actual))
                .Select((error, joint) => error <= SimulationFollowPolicy.ArrivalJointToleranceDegrees(joint)).All(within => within) &&
            Math.Abs(gripperSpeed) <= PikaProtocol.StationarySpeedRadiansPerSecond && (unconfirmedClose ||
                Math.Abs(hardware.GripperOpeningMm - expected) <= OpeningArrivalToleranceMm &&
                (!RequiresPhysicalContactCandidate || hardware.GripperOpeningMm - phase.End.CommandOpeningMm > 2));
        if (!arrived)
        {
            settledBegan = double.NaN;
            lastUnsettled = UnsettledReason(phase, hardware, armStationary, gripperSpeed, expected, unconfirmedClose);
        }
        else if (double.IsNaN(settledBegan) || settledUnconfirmedCandidate != unconfirmedClose)
        { settledBegan = nowSeconds; settledFeedback = gripperFeedbackTime; settledUnconfirmedCandidate = unconfirmedClose; }
        else if (nowSeconds - settledBegan >= .1 && gripperFeedbackTime > settledFeedback)
        {
            if (unconfirmedClose)
            {
                // The same > 2mm separation required to qualify contact leaves
                // every smaller gap unconfirmed, including 1–2mm servo error.
                // Keep the existing source cycle to home without claiming a
                // grasp or demanding an unreachable exact close endpoint.
                PhysicalGraspMissed = true;
                jaw = new(JawReferenceKind.Unconfirmed, hardware.GripperOpeningMm, phase.End.CommandOpeningMm);
            }
            else if (ClosingContact(phase))
            {
                // A bounded 5mm initial model-agreement window accommodates soft
                // contact differences; both source and actual aperture must be
                // strictly>command+2mm. Fresh settled samples still apply.
                // Preserve actual aperture for this exact command;
                // variation in soft simulated contact must not demand squeezing
                // the real object further. Aperture alone does not prove a grasp.
                jaw = new(JawReferenceKind.Contact, hardware.GripperOpeningMm, phase.End.CommandOpeningMm);
            }
            else if (Opening(phase))
            {
                // An opening controller must arrive at its commanded endpoint,
                // not remain at transient simulated servo lag. This is a jaw
                // reference only, never contact or secure-grasp evidence.
                jaw = new(JawReferenceKind.Opened, phase.End.CommandOpeningMm, phase.End.CommandOpeningMm);
                completedOpeningCommand = phase.End.CommandOpeningMm;
            }
            if (OrderedSimulationPath.PhaseKind(phase.End.Phase) == "home")
            { completedHomeCursor = phase.End.Cursor; completedHomeEpisode = phase.End.EpisodeId; }
            Path.Complete(phase); CompletedCursor = phase.End.Cursor; expectedOpening = phase.ExpectedMeasuredOpeningMm; ActivePhase = null;
        }
        if (ElapsedSeconds > phase.DurationSeconds + 15)
            throw new InvalidOperationException("The real arm or Pika did not settle at its ordered phase endpoint; the next phase was not started." +
                (lastUnsettled.Length > 0 ? $" Phase \"{phase.End.Phase}\": {lastUnsettled}." : ""));
        return Target;
    }

    private string lastUnsettled = "";

    /// <summary>Names the first settle condition that is not met, with the measured
    /// and required values, so a timeout says what to fix. Diagnostic text only.</summary>
    private string UnsettledReason(OrderedSimulationPhase phase, SimulationFollowHardware hardware, bool armStationary,
        double gripperSpeed, double expected, bool unconfirmedClose)
    {
        var reasons = new List<string>();
        if (QualifyingLostContact) reasons.Add("Pika contact was changing");
        if (ElapsedSeconds < phase.DurationSeconds) return "";
        if (!armStationary) reasons.Add("arm still moving");
        for (int joint = 0; joint < 6; joint++)
        {
            double error = Math.Abs(Target.JointDegrees[joint] - hardware.JointDegrees[joint]);
            double tolerance = SimulationFollowPolicy.ArrivalJointToleranceDegrees(joint);
            if (error > tolerance)
                reasons.Add($"J{joint + 1} {hardware.JointDegrees[joint]:0.00}° vs target {Target.JointDegrees[joint]:0.00}° (off {error:0.00}°, allowed {tolerance:0.##}°)");
        }
        if (Math.Abs(gripperSpeed) > PikaProtocol.StationarySpeedRadiansPerSecond)
            reasons.Add($"Pika still moving ({gripperSpeed:0.000} rad/s, allowed {PikaProtocol.StationarySpeedRadiansPerSecond:0.00})");
        if (!unconfirmedClose)
        {
            double opening = hardware.GripperOpeningMm;
            if (Math.Abs(opening - expected) > OpeningArrivalToleranceMm)
                reasons.Add($"jaw {opening:0.0} mm vs simulation {expected:0.0} mm (allowed ±{OpeningArrivalToleranceMm:0} mm)");
            if (RequiresPhysicalContactCandidate && opening - phase.End.CommandOpeningMm <= 2)
                reasons.Add($"jaw {opening:0.0} mm not above close command {phase.End.CommandOpeningMm:0.0} mm + 2 mm");
        }
        return string.Join("; ", reasons);
    }

    private void UpdateContactLoss(double now, double opening, double speed, double feedbackTime)
    {
        if (!QualifyingLostContact)
        {
            if (jaw.Kind != JawReferenceKind.Contact || jaw.Command != Target.OpeningMm || jaw.Command >= jaw.Opening ||
                opening >= jaw.Opening - 2 || opening < jaw.Command - PikaProtocol.ArrivalToleranceMm)
                return;
            jaw = jaw with { Kind = JawReferenceKind.LosingContact, LossBegan = now, LossMinimum = opening, LossSettledBegan = double.NaN };
        }
        if (Target.OpeningMm != jaw.Command)
            throw new InvalidOperationException("The jaw command changed before lost contact could be verified; following stopped.");
        if (now - jaw.LossBegan >= ContactLossDeadlineSeconds)
            throw new InvalidOperationException("Pika did not settle at its unchanged close command within 250ms after contact changed; following stopped.");
        if (speed > PikaProtocol.StationarySpeedRadiansPerSecond || opening > jaw.LossMinimum + 2 || opening < jaw.Command - PikaProtocol.ArrivalToleranceMm)
            throw new InvalidOperationException("Pika reversed away from its close endpoint or exceeded that endpoint while checking lost contact; following stopped.");
        jaw = jaw with { LossMinimum = Math.Min(jaw.LossMinimum, opening) };
        bool settled = UnconfirmedOpening(opening, jaw.Command) && Math.Abs(speed) <= PikaProtocol.StationarySpeedRadiansPerSecond;
        if (!settled) jaw = jaw with { LossSettledBegan = double.NaN };
        else if (double.IsNaN(jaw.LossSettledBegan)) jaw = jaw with { LossSettledBegan = now, LossSettledFeedback = feedbackTime };
        else if (now - jaw.LossSettledBegan >= .1 && feedbackTime > jaw.LossSettledFeedback)
        {
            PhysicalGraspMissed = true;
            jaw = new(JawReferenceKind.Unconfirmed, opening, jaw.Command);
        }
    }

    private static bool UnconfirmedOpening(double opening, double command) =>
        opening >= command - PikaProtocol.ArrivalToleranceMm && opening <= command + 2;

    public bool ReturnedHome(SimulationFollowHardware hardware, bool armStationary, double gripperSpeed) =>
        Path.Drained && CompletedCursor == Path.LastCursor && OrderedSimulationPath.PhaseKind(Path.LastSource?.Phase ?? "") == "finish" &&
        completedHomeCursor > 0 && completedHomeEpisode == Path.LastSource?.EpisodeId &&
        hardware.FeedbackFresh && hardware.GripperFresh && armStationary && double.IsFinite(gripperSpeed) && Math.Abs(gripperSpeed) <= PikaProtocol.StationarySpeedRadiansPerSecond &&
        Target.JointDegrees.All(q => Math.Abs(q) <= .25) &&
        // Measured home gets the same per-joint allowance as any phase endpoint: a roll joint leaves a small target unexecuted.
        hardware.JointDegrees.Select((q, joint) => Math.Abs(q) <= SimulationFollowPolicy.ArrivalJointToleranceDegrees(joint)).All(within => within) &&
        completedOpeningCommand is { } opened && lastClosingCommand is { } closed && opened > closed && Target.OpeningMm == opened &&
        jaw.Resolved && jaw.Opening == opened && Math.Abs(hardware.GripperOpeningMm - opened) <= 2;

    private static bool Opening(OrderedSimulationPhase phase) => phase.Kind == OrderedSimulationPhaseKind.Gripper &&
        phase.End.CommandOpeningMm > phase.Frames[0].CommandOpeningMm;
    private static bool ClosingContact(OrderedSimulationPhase phase) => phase.Kind == OrderedSimulationPhaseKind.Gripper &&
        phase.End.CommandOpeningMm < phase.Frames[0].CommandOpeningMm && phase.ExpectedMeasuredOpeningMm - phase.End.CommandOpeningMm > 2;

    private static string Signature(SimulationFollowHardware value) => value.DeviceId + "/" + value.Generation + "/" + string.Join(";",
        value.JointLimits.OrderBy(j => j.JointNumber).Select(j => FormattableString.Invariant($"{j.JointNumber},{j.MinimumDegrees:R},{j.MaximumDegrees:R},{j.MaximumDegreesPerSecond:R},{j.MaximumDegreesPerSecondSquared:R},{j.AccelerationFresh}")));
}

/// <summary>Plugin-E: the measured jaw reference for the current jaw command.
/// <code>
///   None --opening phase settles--> Opened(command)
///   None --closing phase settles more than 2 mm above its command--> Contact(opening, command)
///   None --closing phase settles within 2 mm of its command--> Unconfirmed(opening, command) and grasp missed
///   Contact --opening falls more than 2 mm toward the unchanged command--> LosingContact(+ loss timing)
///   LosingContact --settles within 2 mm--> Unconfirmed and grasp missed; reversal, timeout or command change faults
///   any other --new jaw command--> None
/// </code>
/// Opening is the resolved measured reference (the contact opening for Contact and LosingContact).</summary>
internal enum JawReferenceKind { None, Opened, Contact, LosingContact, Unconfirmed }

internal sealed record JawReference(JawReferenceKind Kind, double Opening = double.NaN, double Command = double.NaN,
    double LossBegan = double.NaN, double LossMinimum = double.NaN, double LossSettledBegan = double.NaN, double LossSettledFeedback = double.NaN)
{
    public static readonly JawReference None = new(JawReferenceKind.None);
    public bool Resolved => Kind != JawReferenceKind.None;
}

/// <summary>Strict source-envelope ordering without a wall-poll derivative.
/// Physics tape nodes, rather than RPC sampling intervals, define the path.</summary>
public sealed class OrderedSimulationObservation
{
    private readonly SimulationFollowSample first;
    private long sequence, captured, scene;
    private double physics;
    private long lastProgressCapture;
    private string episode = "";
    public OrderedSimulationObservation(SimulationFollowSample initial)
    {
        first = initial; sequence = initial.Sequence; captured = lastProgressCapture = initial.CapturedMonotonicNs;
        scene = initial.SimulationTimeNs; physics = initial.SimTimeSeconds;
    }
    public void Validate(SimulationFollowSample sample, long receivedAtUnixMilliseconds, double roundTripMilliseconds,
        bool sourcePaused, bool sourceComplete, string episodeId, bool previousEpisodeAcknowledged)
    {
        double age = receivedAtUnixMilliseconds - sample.CapturedAtUnixMicroseconds / 1000d;
        if (!sample.BindsTo(first) || sample.SceneId != sample.RunId || sample.Mode is not ("play" or "pause") || sample.Mode == "pause" && !sourcePaused ||
            !double.IsFinite(roundTripMilliseconds) || roundTripMilliseconds < 0 || roundTripMilliseconds > SimulationFollowPolicy.MaximumRequestMilliseconds ||
            age < -SimulationFollowPolicy.MaximumSourceLeadMilliseconds || age > SimulationFollowPolicy.MaximumSourceAgeMilliseconds ||
            sample.Sequence <= sequence || sample.CapturedMonotonicNs <= captured || sample.TimeScale <= 0 || sample.TimeScale > 1.000001)
            throw new InvalidOperationException("The ordered simulation envelope is stale, paused manually, out of order or changed scope.");
        bool newEpisode = episodeId != episode;
        bool permittedReset = newEpisode && (episode.Length == 0 || previousEpisodeAcknowledged);
        if ((sample.SimulationTimeNs < scene || sample.SimTimeSeconds < physics) && !permittedReset)
            throw new InvalidOperationException("The ordered simulation clock reset without a completed episode acknowledgement.");
        if (sample.SimTimeSeconds != physics || permittedReset) lastProgressCapture = sample.CapturedMonotonicNs;
        else if (!sourcePaused && !sourceComplete &&
            sample.CapturedMonotonicNs - lastProgressCapture > SimulationFollowPolicy.MaximumSourceAgeMilliseconds * 1_000_000)
            throw new InvalidOperationException("Ordered source physics did not advance within 250 ms.");
        sequence = sample.Sequence; captured = sample.CapturedMonotonicNs; scene = sample.SimulationTimeNs; physics = sample.SimTimeSeconds; episode = episodeId;
    }
}
