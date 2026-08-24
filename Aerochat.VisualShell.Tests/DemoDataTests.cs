using Aerochat.Presentation;

namespace Aerochat.VisualShell.Tests;

public sealed class DemoDataTests
{
    [Test]
    public void Create_populates_stable_visual_states()
    {
        PresentationState state = DemoData.Create();

        Assert.Multiple(() =>
        {
            Assert.That(state.CurrentUser.Name, Is.EqualTo("Nate Rivera"));
            Assert.That(state.ContactGroups.Select(group => group.Name),
                Is.EqualTo(new[] { "Favorites", "Conversations", "Servers" }));
            Assert.That(state.ContactGroups.SelectMany(group => group.Items)
                .Select(item => item.Person.Presence.Status).Distinct(),
                Is.SupersetOf(new[] { PresenceStatus.Online, PresenceStatus.Busy,
                    PresenceStatus.Away, PresenceStatus.Offline }));
            Assert.That(state.Conversations.Count, Is.GreaterThanOrEqualTo(2));
            Assert.That(state.Conversations.Any(conversation => conversation.IsGroup), Is.True);
            Assert.That(state.Conversations.Any(conversation => !conversation.IsGroup), Is.True);
            Assert.That(state.Conversations.All(conversation => conversation.Messages.Count >= 3), Is.True);
            Assert.That(state.Scenes.Count, Is.GreaterThanOrEqualTo(3));
            Assert.That(state.News.Count, Is.GreaterThanOrEqualTo(2));
            Assert.That(state.Notices, Is.Not.Empty);
        });
    }

    [Test]
    public void SendDraft_appends_one_local_outgoing_message_and_clears_the_draft()
    {
        PresentationState state = DemoData.Create();
        ConversationPresentation conversation = state.Conversations[0];
        int before = conversation.Messages.Count;
        conversation.Draft = "Local shell message";

        MessagePresentation? sent = state.SendDraft(
            conversation, new DateTimeOffset(2026, 8, 24, 12, 0, 0, TimeSpan.Zero));

        Assert.Multiple(() =>
        {
            Assert.That(sent, Is.Not.Null);
            Assert.That(conversation.Messages.Count, Is.EqualTo(before + 1));
            Assert.That(conversation.Messages[^1].Body, Is.EqualTo("Local shell message"));
            Assert.That(conversation.Messages[^1].IsOutgoing, Is.True);
            Assert.That(conversation.Draft, Is.Empty);
        });
    }
}
