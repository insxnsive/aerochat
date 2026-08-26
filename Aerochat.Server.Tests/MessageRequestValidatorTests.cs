using Aerochat.Server.Hardening;

namespace Aerochat.Server.Tests;

public sealed class MessageRequestValidatorTests
{
    [Test]
    public void Accepts_at_most_2000_characters()
    {
        Assert.Multiple(() =>
        {
            Assert.That(MessageRequestValidator.IsBodyWithinLimit(new string('x', 2000)), Is.True);
            Assert.That(MessageRequestValidator.IsBodyWithinLimit(new string('x', 2001)), Is.False);
        });
    }
}
