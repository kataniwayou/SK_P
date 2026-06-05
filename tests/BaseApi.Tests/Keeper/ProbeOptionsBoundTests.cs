using Keeper;
using Xunit;

namespace BaseApi.Tests.Keeper;

public sealed class ProbeOptionsBoundTests
{
    // PROBE-01 (D-04 / Pitfall 6): the in-Consume awaited loop holds the delivery un-acked for
    // DelaySeconds x MaxAttempts; that product MUST stay under RabbitMQ's 30-min consumer_timeout.
    [Fact]
    public void ProbeOptions_Bound_Under_RabbitMq_ConsumerTimeout()
    {
        var opts = new ProbeOptions();   // defaults 5 x 12
        var holdSeconds = opts.DelaySeconds * opts.MaxAttempts;
        const int consumerTimeoutSeconds = 30 * 60;   // RabbitMQ default consumer_timeout = 30 min
        Assert.True(holdSeconds > 0);
        Assert.True(holdSeconds < consumerTimeoutSeconds,
            $"DelaySeconds x MaxAttempts = {holdSeconds}s must stay under the {consumerTimeoutSeconds}s consumer_timeout");
    }
}
