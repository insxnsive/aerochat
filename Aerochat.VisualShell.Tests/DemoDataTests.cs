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
    public void Create_returns_fresh_independent_object_graphs()
    {
        PresentationState first = DemoData.Create();
        PresentationState second = DemoData.Create();

        Assert.Multiple(() =>
        {
            Assert.That(first, Is.Not.SameAs(second));
            Assert.That(first.CurrentUser, Is.Not.SameAs(second.CurrentUser));
            Assert.That(first.CurrentUser.Presence, Is.Not.SameAs(second.CurrentUser.Presence));
            Assert.That(first.Settings, Is.Not.SameAs(second.Settings));
            Assert.That(first.CurrentScene, Is.Not.SameAs(second.CurrentScene));
            Assert.That(first.ContactGroups[0], Is.Not.SameAs(second.ContactGroups[0]));
            Assert.That(first.ContactGroups[0].Items[0].Person,
                Is.Not.SameAs(second.ContactGroups[0].Items[0].Person));
            Assert.That(first.Conversations[0], Is.Not.SameAs(second.Conversations[0]));
            Assert.That(first.Conversations[0].Messages[0],
                Is.Not.SameAs(second.Conversations[0].Messages[0]));
        });

        first.CurrentUser.Presence.CustomStatus = "Changed";
        first.Settings.ShowAds = false;
        first.ContactGroups[0].Items.Clear();
        first.Conversations[0].Messages[0].Body = "Changed";

        Assert.Multiple(() =>
        {
            Assert.That(second.CurrentUser.Presence.CustomStatus, Is.EqualTo("Available"));
            Assert.That(second.Settings.ShowAds, Is.True);
            Assert.That(second.ContactGroups[0].Items, Has.Count.EqualTo(2));
            Assert.That(second.Conversations[0].Messages[0].Body,
                Is.EqualTo("The glass header is landing nicely."));
        });
    }

    [Test]
    public void Create_exposes_resolvable_packaged_resource_uris()
    {
        PresentationState state = DemoData.Create();
        string[] resourceUris = state.ContactGroups
            .SelectMany(group => group.Items)
            .Select(item => item.Person.Avatar)
            .Append(state.CurrentUser.Avatar)
            .Concat(state.Scenes.Select(scene => scene.File))
            .Concat(state.Ads.Select(ad => ad.ImageUri))
            .Concat(state.PreviewImages.Select(image => image.SourceUri))
            .Concat(state.Conversations.SelectMany(conversation => conversation.Messages)
                .Select(message => message.AttachmentUri)
                .Where(uri => uri is not null)
                .Cast<string>())
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        Assert.That(resourceUris, Is.Not.Empty);
        foreach (string resourceUri in resourceUris)
        {
            Assert.That(resourceUri, Does.StartWith("/Aerochat;component/"));
            System.Windows.Resources.StreamResourceInfo? resource =
                System.Windows.Application.GetResourceStream(new Uri(resourceUri, UriKind.Relative));
            Assert.That(resource, Is.Not.Null, $"Packaged resource does not resolve: {resourceUri}");
            resource?.Stream.Dispose();
        }
    }

    [Test]
    public void ApplySearch_filters_contacts_by_searchable_person_fields()
    {
        PresentationState state = DemoData.Create();

        state.ApplySearch("  SCENE CONCEPTS  ");

        Assert.Multiple(() =>
        {
            Assert.That(state.FilteredContactGroups.Select(group => group.Name),
                Is.EqualTo(new[] { "Favorites" }));
            Assert.That(state.FilteredContactGroups.Single().Items.Select(item => item.Person.Name),
                Is.EqualTo(new[] { "Maya Chen" }));
        });
    }

    [Test]
    public void ApplySearch_preserves_canonical_groups_and_uses_filtered_wrappers()
    {
        PresentationState state = DemoData.Create();
        ContactGroupPresentation[] canonicalGroups = state.ContactGroups.ToArray();
        ContactPresentation[][] canonicalItems = canonicalGroups
            .Select(group => group.Items.ToArray())
            .ToArray();

        state.ApplySearch("maya");

        Assert.Multiple(() =>
        {
            Assert.That(state.ContactGroups, Is.EqualTo(canonicalGroups));
            Assert.That(state.ContactGroups.Select(group => group.Items.ToArray()),
                Is.EqualTo(canonicalItems));
            Assert.That(state.FilteredContactGroups,
                Has.All.Matches<ContactGroupPresentation>(filtered =>
                    canonicalGroups.All(canonical => !ReferenceEquals(canonical, filtered))));
        });

        state.ApplySearch("");

        Assert.Multiple(() =>
        {
            Assert.That(state.FilteredContactGroups.Select(group => group.Name),
                Is.EqualTo(canonicalGroups.Select(group => group.Name)));
            Assert.That(state.FilteredContactGroups.Select(group => group.Items.Count),
                Is.EqualTo(canonicalGroups.Select(group => group.Items.Count)));
        });
    }

    [Test]
    public void SendDraft_appends_one_local_outgoing_message_and_clears_the_draft()
    {
        PresentationState state = DemoData.Create();
        ConversationPresentation conversation = state.Conversations[0];
        int before = conversation.Messages.Count;
        DateTimeOffset sentAt = new(2026, 8, 24, 12, 0, 0, TimeSpan.Zero);
        conversation.Draft = "Local shell message";

        MessagePresentation? sent = state.SendDraft(conversation, sentAt);

        Assert.That(sent, Is.Not.Null);
        Assert.Multiple(() =>
        {
            Assert.That(conversation.Messages.Count, Is.EqualTo(before + 1));
            Assert.That(conversation.Messages[^1], Is.SameAs(sent));
            Assert.That(sent!.Body, Is.EqualTo("Local shell message"));
            Assert.That(sent.Author, Is.SameAs(state.CurrentUser));
            Assert.That(sent.SentAt, Is.EqualTo(sentAt));
            Assert.That(sent.IsOutgoing, Is.True);
            Assert.That(conversation.Draft, Is.Empty);
        });
    }

    [Test]
    public void SendDraft_trims_non_whitespace_body()
    {
        PresentationState state = DemoData.Create();
        ConversationPresentation conversation = state.Conversations[0];
        conversation.Draft = "  Local shell message  ";

        MessagePresentation? sent = state.SendDraft(
            conversation, new DateTimeOffset(2026, 8, 24, 12, 0, 0, TimeSpan.Zero));

        Assert.That(sent, Is.Not.Null);
        Assert.That(sent!.Body, Is.EqualTo("Local shell message"));
    }

    [Test]
    public void SendDraft_returns_null_for_whitespace_without_mutating_messages()
    {
        PresentationState state = DemoData.Create();
        ConversationPresentation conversation = state.Conversations[0];
        int before = conversation.Messages.Count;
        conversation.Draft = " \t\r\n ";

        MessagePresentation? sent = state.SendDraft(
            conversation, new DateTimeOffset(2026, 8, 24, 12, 0, 0, TimeSpan.Zero));

        Assert.Multiple(() =>
        {
            Assert.That(sent, Is.Null);
            Assert.That(conversation.Messages, Has.Count.EqualTo(before));
            Assert.That(conversation.Draft, Is.EqualTo(" \t\r\n "));
        });
    }

    [Test]
    public void SendDraft_carries_reply_target_and_clears_target_state()
    {
        PresentationState state = DemoData.Create();
        ConversationPresentation conversation = state.Conversations[0];
        MessagePresentation replyTarget = conversation.Messages[0];
        state.BeginReply(conversation, replyTarget);
        conversation.Draft = "Reply body";

        MessagePresentation? sent = state.SendDraft(
            conversation, new DateTimeOffset(2026, 8, 24, 12, 0, 0, TimeSpan.Zero));

        Assert.That(sent, Is.Not.Null);
        Assert.Multiple(() =>
        {
            Assert.That(sent!.ReplyTo, Is.SameAs(replyTarget));
            Assert.That(conversation.TargetMessage, Is.Null);
            Assert.That(conversation.TargetMode, Is.EqualTo(MessageTargetMode.None));
            Assert.That(conversation.Draft, Is.Empty);
        });
    }

    [Test]
    public void BeginReply_ignores_a_message_from_another_conversation()
    {
        PresentationState state = DemoData.Create();
        ConversationPresentation conversation = state.Conversations[0];
        MessagePresentation foreignMessage = state.Conversations[1].Messages[0];

        state.BeginReply(conversation, foreignMessage);

        Assert.Multiple(() =>
        {
            Assert.That(conversation.TargetMessage, Is.Null);
            Assert.That(conversation.TargetMode, Is.EqualTo(MessageTargetMode.None));
        });
    }

    [Test]
    public void BeginEdit_rejects_incoming_messages()
    {
        PresentationState state = DemoData.Create();
        ConversationPresentation conversation = state.Conversations[0];
        MessagePresentation incoming = conversation.Messages.First(message => !message.IsOutgoing);
        conversation.Draft = "Existing draft";

        state.BeginEdit(conversation, incoming);

        Assert.Multiple(() =>
        {
            Assert.That(conversation.TargetMessage, Is.Null);
            Assert.That(conversation.TargetMode, Is.EqualTo(MessageTargetMode.None));
            Assert.That(conversation.Draft, Is.EqualTo("Existing draft"));
        });
    }

    [Test]
    public void BeginEdit_rejects_local_messages_from_another_conversation()
    {
        PresentationState state = DemoData.Create();
        ConversationPresentation conversation = state.Conversations[0];
        MessagePresentation foreignLocalMessage = state.Conversations[1].Messages
            .Single(message => message.IsOutgoing);
        conversation.Draft = "Existing draft";

        state.BeginEdit(conversation, foreignLocalMessage);

        Assert.Multiple(() =>
        {
            Assert.That(conversation.TargetMessage, Is.Null);
            Assert.That(conversation.TargetMode, Is.EqualTo(MessageTargetMode.None));
            Assert.That(conversation.Draft, Is.EqualTo("Existing draft"));
        });
    }

    [Test]
    public void CommitEdit_updates_only_the_selected_local_message_and_clears_edit_state()
    {
        PresentationState state = DemoData.Create();
        ConversationPresentation conversation = state.Conversations[0];
        MessagePresentation target = conversation.Messages.Single(message => message.IsOutgoing);
        MessagePresentation untouched = conversation.Messages.First(message => !message.IsOutgoing);
        string untouchedBody = untouched.Body;
        int before = conversation.Messages.Count;
        state.BeginEdit(conversation, target);
        conversation.Draft = "  Updated local body  ";

        state.CommitEdit(conversation);

        Assert.Multiple(() =>
        {
            Assert.That(target.Body, Is.EqualTo("Updated local body"));
            Assert.That(untouched.Body, Is.EqualTo(untouchedBody));
            Assert.That(conversation.Messages, Has.Count.EqualTo(before));
            Assert.That(conversation.Draft, Is.Empty);
            Assert.That(conversation.TargetMessage, Is.Null);
            Assert.That(conversation.TargetMode, Is.EqualTo(MessageTargetMode.None));
        });
    }

    [Test]
    public void CommitEdit_preserves_the_message_body_for_whitespace_and_clears_edit_state()
    {
        PresentationState state = DemoData.Create();
        ConversationPresentation conversation = state.Conversations[0];
        MessagePresentation target = conversation.Messages.Single(message => message.IsOutgoing);
        string originalBody = target.Body;
        state.BeginEdit(conversation, target);
        conversation.Draft = " \t ";

        state.CommitEdit(conversation);

        Assert.Multiple(() =>
        {
            Assert.That(target.Body, Is.EqualTo(originalBody));
            Assert.That(conversation.Draft, Is.Empty);
            Assert.That(conversation.TargetMessage, Is.Null);
            Assert.That(conversation.TargetMode, Is.EqualTo(MessageTargetMode.None));
        });
    }

    [Test]
    public void CommitEdit_rechecks_local_message_authorization()
    {
        PresentationState state = DemoData.Create();
        ConversationPresentation conversation = state.Conversations[0];
        MessagePresentation incoming = conversation.Messages.First(message => !message.IsOutgoing);
        string originalBody = incoming.Body;
        conversation.TargetMessage = incoming;
        conversation.TargetMode = MessageTargetMode.Edit;
        conversation.Draft = "Unauthorized update";

        state.CommitEdit(conversation);

        Assert.Multiple(() =>
        {
            Assert.That(incoming.Body, Is.EqualTo(originalBody));
            Assert.That(conversation.Draft, Is.Empty);
            Assert.That(conversation.TargetMessage, Is.Null);
            Assert.That(conversation.TargetMode, Is.EqualTo(MessageTargetMode.None));
        });
    }

    [Test]
    public void BeginReply_after_BeginEdit_discards_the_edit_buffer()
    {
        PresentationState state = DemoData.Create();
        ConversationPresentation conversation = state.Conversations[0];
        MessagePresentation localMessage = conversation.Messages.Single(message => message.IsOutgoing);
        MessagePresentation replyTarget = conversation.Messages.First(message => !message.IsOutgoing);
        int before = conversation.Messages.Count;
        state.BeginEdit(conversation, localMessage);

        state.BeginReply(conversation, replyTarget);
        MessagePresentation? sent = state.SendDraft(
            conversation, new DateTimeOffset(2026, 8, 24, 12, 0, 0, TimeSpan.Zero));

        Assert.Multiple(() =>
        {
            Assert.That(conversation.Draft, Is.Empty);
            Assert.That(conversation.TargetMessage, Is.SameAs(replyTarget));
            Assert.That(conversation.TargetMode, Is.EqualTo(MessageTargetMode.Reply));
            Assert.That(sent, Is.Null);
            Assert.That(conversation.Messages, Has.Count.EqualTo(before));
        });
    }

    [Test]
    public void SelectScene_updates_CurrentScene_and_raises_its_property_notification()
    {
        PresentationState state = DemoData.Create();
        ScenePresentation selected = state.Scenes[1];
        List<string?> propertyNames = [];
        state.PropertyChanged += (_, args) => propertyNames.Add(args.PropertyName);

        state.SelectScene(selected);

        Assert.Multiple(() =>
        {
            Assert.That(state.CurrentScene, Is.SameAs(selected));
            Assert.That(propertyNames, Is.EqualTo(new[] { nameof(PresentationState.CurrentScene) }));
        });
    }
}
