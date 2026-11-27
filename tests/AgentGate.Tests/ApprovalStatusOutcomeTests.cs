using AgentGate.Approval;
using Xunit;

namespace AgentGate.Tests;

public sealed partial class HomeAssistantApprovalChannelTests
{
    [Fact]
    public async Task ApprovedStatusIsNotAFinalOutcomeAndCannotConsumeTheNotification()
    {
        await using var harness = new Standalone();
        await harness.StartAsync();
        var notice = Notice();
        await harness.Channel.SendRequestAsync(notice, default);
        await Assert.ThrowsAsync<InvalidOperationException>(() => harness.Channel.SendOutcomeAsync(new(notice.Id, ApprovalState.Approved, null), default));
        await harness.Channel.SendOutcomeAsync(new(notice.Id, ApprovalState.Executed, null), default);
    }
}
