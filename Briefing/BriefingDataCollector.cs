using Microsoft.Extensions.Logging;

namespace ClanGuardBot.Briefing;

public interface IBriefingDataCollector
{
    Task<BriefingContext> CollectAsync(
        DateTime weekStart,
        DateTime weekEnd,
        CancellationToken ct = default);
}

/// <summary>
/// Aggregates and caps the weekly snapshot. THIS is where the cost optimization lives.
///
/// Rule of thumb: anything that could grow with roster size (members, attendance rows,
/// rank events) gets filtered/sorted/Take(N) here. The prompt should never see raw
/// EF Core query results.
///
/// TODO: Wire constructor to your existing services. Suggested mapping:
///   IRosterService                  → ActiveMemberCount, NewRecruits
///   IAwolDetectionService           → AwolRisks (already computed; just reshape)
///   EventAttendanceSnapshotService  → AverageAttendancePercent, NotableEvents
///   IPromotionEvaluationService     → PromotionCandidates (already computed for /promote dry-runs)
/// </summary>
public sealed class BriefingDataCollector(
    // Inject your existing services here. Stubs below return empty — the wiring is yours.
    ILogger<BriefingDataCollector> logger) : IBriefingDataCollector
{
    // Caps — tune to taste. These are the levers that bound input token cost.
    private const int MaxAwolRisks = 8;
    private const int MaxPromotionCandidates = 8;
    private const int MaxNotableEvents = 5;
    private const int MaxAnomalies = 5;

    public async Task<BriefingContext> CollectAsync(
        DateTime weekStart,
        DateTime weekEnd,
        CancellationToken ct = default)
    {
        logger.LogInformation(
            "Collecting briefing data for {Start:yyyy-MM-dd} → {End:yyyy-MM-dd}",
            weekStart, weekEnd);

        // Run independent fetches concurrently. Replace stubs with real service calls.
        var awolTask = GetAwolRisksAsync(ct);
        var promoTask = GetPromotionCandidatesAsync(weekStart, weekEnd, ct);
        var eventsTask = GetNotableEventsAsync(weekStart, weekEnd, ct);
        var topLineTask = GetTopLineNumbersAsync(weekStart, weekEnd, ct);
        var anomaliesTask = GetAnomaliesAsync(weekStart, weekEnd, ct);

        await Task.WhenAll(awolTask, promoTask, eventsTask, topLineTask, anomaliesTask);

        var awolRisks = (await awolTask)
            .OrderByDescending(r => r.DaysSinceLastActivity)
            .Take(MaxAwolRisks)
            .ToList();

        var promotionCandidates = (await promoTask)
            .OrderByDescending(c => c.EventsAttendedLast30Days)
            .ThenByDescending(c => c.DaysInRank)
            .Take(MaxPromotionCandidates)
            .ToList();

        var notableEvents = (await eventsTask)
            .OrderByDescending(e => e.Attendance)
            .Take(MaxNotableEvents)
            .ToList();

        var anomalies = (await anomaliesTask).Take(MaxAnomalies).ToList();
        var topLine = await topLineTask;

        var context = new BriefingContext
        {
            WeekStart = weekStart,
            WeekEnd = weekEnd,
            ActiveMemberCount = topLine.ActiveMemberCount,
            EventsHeld = topLine.EventsHeld,
            AverageAttendancePercent = topLine.AverageAttendancePercent,
            NewRecruits = topLine.NewRecruits,
            AwolRisks = awolRisks,
            PromotionCandidates = promotionCandidates,
            NotableEvents = notableEvents,
            Anomalies = anomalies
        };

        logger.LogInformation(
            "Briefing context: {Awol} AWOL risks, {Promo} promotion candidates, {Events} events, {Anomaly} anomalies",
            awolRisks.Count, promotionCandidates.Count, notableEvents.Count, anomalies.Count);

        return context;
    }

    // --- Service-call seams (replace with real wiring) ----------------------

    private Task<IReadOnlyList<AwolRiskItem>> GetAwolRisksAsync(CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<AwolRiskItem>>([]);

    private Task<IReadOnlyList<PromotionCandidate>> GetPromotionCandidatesAsync(
        DateTime weekStart, DateTime weekEnd, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<PromotionCandidate>>([]);

    private Task<IReadOnlyList<NotableEvent>> GetNotableEventsAsync(
        DateTime weekStart, DateTime weekEnd, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<NotableEvent>>([]);

    private Task<IReadOnlyList<string>> GetAnomaliesAsync(
        DateTime weekStart, DateTime weekEnd, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<string>>([]);

    private Task<TopLineNumbers> GetTopLineNumbersAsync(
        DateTime weekStart, DateTime weekEnd, CancellationToken ct) =>
        Task.FromResult(new TopLineNumbers(0, 0, 0d, 0));

    private sealed record TopLineNumbers(
        int ActiveMemberCount,
        int EventsHeld,
        double AverageAttendancePercent,
        int NewRecruits);
}