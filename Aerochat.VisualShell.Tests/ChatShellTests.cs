using Aerochat.Presentation;
using Aerochat.Windows;

namespace Aerochat.VisualShell.Tests;

public sealed class ChatShellTests
{
    [Test]
    public void Chat_constructs_with_sample_messages_without_network_client()
    {
        WpfTestHost.Run(() =>
        {
            PresentationState state = DemoData.Create();
            ConversationPresentation conversation = state.Conversations[0];
            var chat = new Chat(state, conversation, new WindowNavigator(state));

            Assert.That(chat.DataContext, Is.SameAs(conversation));
            Assert.That(conversation.Messages, Is.Not.Empty);

            chat.Close();
        });
    }

    [Test]
    public void Navigator_creates_chat_from_a_conversation_payload()
    {
        WpfTestHost.Run(() =>
        {
            PresentationState state = DemoData.Create();
            ConversationPresentation conversation = state.Conversations[0];
            var navigator = new WindowNavigator(state);

            var chat = (Chat)navigator.Create(ShellRoute.Chat, conversation);

            Assert.That(chat.State, Is.SameAs(state));
            Assert.That(chat.Navigator, Is.SameAs(navigator));
            Assert.That(chat.Conversation, Is.SameAs(conversation));
            Assert.That(chat.DataContext, Is.SameAs(conversation));

            chat.Close();
        });
    }

    [Test]
    public void Navigator_creates_chat_from_a_conversation_id_lookup()
    {
        WpfTestHost.Run(() =>
        {
            PresentationState state = DemoData.Create();
            ConversationPresentation conversation = state.Conversations[0];
            var navigator = new WindowNavigator(state);

            var chat = (Chat)navigator.Create(ShellRoute.Chat, conversation.Id);

            Assert.That(chat.Conversation, Is.SameAs(conversation));
            Assert.That(chat.DataContext, Is.SameAs(conversation));

            chat.Close();
        });
    }

    [Test]
    public void Reply_and_edit_change_only_local_conversation_state()
    {
        PresentationState state = DemoData.Create();
        ConversationPresentation conversation = state.Conversations[0];
        MessagePresentation target = conversation.Messages[0];

        state.BeginReply(conversation, target);
        Assert.That(conversation.TargetMode, Is.EqualTo(MessageTargetMode.Reply));

        conversation.Draft = "Reply text";
        MessagePresentation? reply = state.SendDraft(
            conversation,
            new DateTimeOffset(2026, 8, 24, 12, 5, 0, TimeSpan.Zero));

        Assert.That(reply, Is.Not.Null);
        Assert.That(reply!.ReplyTo, Is.SameAs(target));

        state.BeginEdit(conversation, reply);
        conversation.Draft = "Edited locally";
        state.CommitEdit(conversation);

        Assert.That(reply.Body, Is.EqualTo("Edited locally"));
        Assert.That(conversation.TargetMode, Is.EqualTo(MessageTargetMode.None));
    }
}
