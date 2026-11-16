using ClanGuardBot.Services;

namespace ClanGuardBot.Tests;

public class InviteAttributionOutcomeTests
{
    private const ulong DisboardId = 302050872383242240;
    private const ulong MemberId = 1400000000000000001;

    private static bool IsDisboard(ulong id) => id == DisboardId;

    [Fact]
    public void DisboardInviteJoinIsRecordedWithoutReferrer()
    {
        var outcome = InviteAttributionService.ResolveOutcome(
            [("disb123", DisboardId)], vanityIncreased: false, IsDisboard);

        Assert.Equal("disb123", outcome.Code);
        Assert.Null(outcome.InviterId);
        Assert.Equal(InviteAttributionService.LabelDisboard, outcome.Label);
        Assert.False(outcome.Ambiguous);
    }

    [Fact]
    public void MemberInviteJoinCreditsTheMember()
    {
        var outcome = InviteAttributionService.ResolveOutcome(
            [("abc", MemberId)], vanityIncreased: false, IsDisboard);

        Assert.Equal("abc", outcome.Code);
        Assert.Equal(MemberId, outcome.InviterId);
        Assert.Equal(InviteAttributionService.LabelUnknown, outcome.Label);
    }

    [Fact]
    public void VanityJoin()
    {
        var outcome = InviteAttributionService.ResolveOutcome([], vanityIncreased: true, IsDisboard);

        Assert.Null(outcome.Code);
        Assert.Equal(InviteAttributionService.LabelVanity, outcome.Label);
    }

    [Fact]
    public void NoCounterMovedIsUnattributed()
    {
        var outcome = InviteAttributionService.ResolveOutcome([], vanityIncreased: false, IsDisboard);

        Assert.Null(outcome.Code);
        Assert.Equal(InviteAttributionService.LabelUnattributed, outcome.Label);
        Assert.False(outcome.Ambiguous);
    }

    [Fact]
    public void SeveralCountersMovedIsAmbiguous()
    {
        var outcome = InviteAttributionService.ResolveOutcome(
            [("disb123", DisboardId)], vanityIncreased: true, IsDisboard);

        Assert.Null(outcome.Code);
        Assert.Null(outcome.InviterId);
        Assert.Equal(InviteAttributionService.LabelAmbiguous, outcome.Label);
        Assert.True(outcome.Ambiguous);
    }
}
