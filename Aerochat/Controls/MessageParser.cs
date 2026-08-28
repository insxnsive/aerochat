using System.Collections;
using System.ComponentModel;
using System.Globalization;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Aerochat.Presentation;

namespace Aerochat.Controls
{
    /// <summary>
    /// Renders message-shaped data without owning message services or navigation.
    /// The bound value is intentionally object-shaped so existing bindings can pass
    /// their message model while this control only reads presentation properties.
    /// </summary>
    public class MessageParser : UserControl, IDisposable
    {
        private INotifyPropertyChanged? _observedMessage;
        private bool _disposed;
        private bool _isUnloaded;

        private static readonly IReadOnlyDictionary<string, string> UnicodeEmojiMap =
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["😀"] = "Smile.png",
                ["😄"] = "Grin.png",
                ["😉"] = "Wink.png",
                ["😮"] = "Surprise.png",
                ["😛"] = "Tongue.png",
                ["😎"] = "Sunglasses.png",
                ["😡"] = "Anger.png",
                ["😭"] = "Sob.png",
                ["😕"] = "Confused.png",
                ["🙏"] = "HighFive.png",
                ["🤔"] = "Thinking.png",
                ["👍"] = "ThumbsUp.png",
                ["👎"] = "ThumbsDown.png",
                ["❤️"] = "Heart.png",
            };

        private static readonly IReadOnlyDictionary<string, string> ShortcodeEmojiMap =
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [":)"] = "Smile.png",
                [":D"] = "Grin.png",
                [":("] = "Frown.png",
                [":P"] = "Tongue.png",
                [";)"] = "Wink.png",
                [":O"] = "Surprise.png",
            };

        public static readonly DependencyProperty MessageProperty =
            DependencyProperty.Register(nameof(Message), typeof(object), typeof(MessageParser), new PropertyMetadata(null, OnMessageChanged));

        public object? Message
        {
            get => GetValue(MessageProperty);
            set => SetValue(MessageProperty, value);
        }

        public event EventHandler<HyperlinkClickedEventArgs>? HyperlinkClicked;
        public event EventHandler<ContextMenuEventArgs>? TextBlockContextMenuOpening;

        public WrapPanel MainPanel { get; set; }

        public MessageParser()
        {
            MainPanel = new WrapPanel();
            Content = MainPanel;
            Loaded += MessageParser_Loaded;
            Unloaded += MessageParser_Unloaded;
        }

        private static void OnMessageChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            var parser = (MessageParser)d;
            if (parser._disposed)
            {
                if (parser.ReadLocalValue(MessageProperty) != DependencyProperty.UnsetValue)
                    parser.ClearValue(MessageProperty);

                return;
            }

            if (parser._isUnloaded)
                return;

            parser.DetachMessageNotifications();
            parser.AttachMessageNotifications(e.NewValue);
            parser.RenderMessage();
        }

        private void MessageParser_Loaded(object sender, RoutedEventArgs e)
        {
            if (_disposed)
                return;

            _isUnloaded = false;
            AttachMessageNotifications(Message);
            RenderMessage();
        }

        private void MessageParser_Unloaded(object sender, RoutedEventArgs e)
        {
            _isUnloaded = true;
            DetachMessageNotifications();
        }

        private void AttachMessageNotifications(object? message)
        {
            if (_disposed || message is not INotifyPropertyChanged notify)
                return;

            if (ReferenceEquals(_observedMessage, message))
                return;

            DetachMessageNotifications();
            _observedMessage = notify;
            notify.PropertyChanged += Message_PropertyChanged;
        }

        private void DetachMessageNotifications()
        {
            if (_observedMessage is null)
                return;

            _observedMessage.PropertyChanged -= Message_PropertyChanged;
            _observedMessage = null;
        }

        private void Message_PropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (_disposed || _isUnloaded || !ReferenceEquals(sender, _observedMessage))
                return;

            if (Dispatcher.CheckAccess())
                RenderMessage();
            else
                _ = Dispatcher.BeginInvoke(RenderMessageIfActive);
        }

        private void RenderMessageIfActive()
        {
            if (_disposed || _isUnloaded)
                return;

            RenderMessage();
        }

        public void Dispose()
        {
            if (_disposed)
                return;

            _disposed = true;
            DetachMessageNotifications();
            ClearValue(MessageProperty);
            MainPanel.Children.Clear();
            Loaded -= MessageParser_Loaded;
            Unloaded -= MessageParser_Unloaded;
        }

        private void RenderMessage()
        {
            MainPanel.Children.Clear();

            if (Message is null)
                return;

            if (string.Equals(ReadString(Message, "Kind"), "sticker", StringComparison.Ordinal)
                && StickerCatalog.TryReadResourceName(ReadString(Message, "RefPayloadJson"), out string resourceName)
                && StickerCatalog.TryGet(resourceName, out StickerPresentation stickerPresentation))
            {
                var sticker = new Image
                {
                    Source = new BitmapImage(new Uri(stickerPresentation.ResourceUri, UriKind.Absolute)),
                    Width = 160,
                    Height = 160,
                    MaxWidth = 220,
                    MaxHeight = 220,
                    Stretch = Stretch.Uniform,
                    ToolTip = "Sticker"
                };
                MainPanel.Children.Add(sticker);
                return;
            }

#if FEATURE_SELECTABLE_MESSAGE_TEXT
            var textBlock = new SelectableTextBlock();
#else
            var textBlock = new TextBlock();
#endif
            textBlock.TextWrapping = TextWrapping.Wrap;

            string content = ReadString(Message, "Content")
                ?? ReadString(Message, "Body")
                ?? Message as string
                ?? string.Empty;
            AppendContent(textBlock, content, Message);
            MainPanel.Children.Add(FormatFullText(textBlock));
        }

        private void AppendContent(TextBlock textBlock, string content, object message)
        {
            foreach (Match tokenMatch in Regex.Matches(content, @"\s+|\S+"))
            {
                string token = tokenMatch.Value;

                if (string.IsNullOrWhiteSpace(token))
                {
                    textBlock.Inlines.Add(new Run(token));
                    continue;
                }

                if (TryCreateMention(token, message, out Hyperlink? mention))
                {
                    textBlock.Inlines.Add(mention);
                    continue;
                }

                if (TryCreatePackagedEmoji(token, out InlineUIContainer? customEmoji))
                {
                    textBlock.Inlines.Add(customEmoji);
                    continue;
                }

                if (TryCreateWebLink(token, out Hyperlink? webLink))
                {
                    textBlock.Inlines.Add(webLink);
                    continue;
                }

                AppendLocalText(textBlock, token);
            }
        }

        private bool TryCreateMention(string token, object message, out Hyperlink? link)
        {
            link = null;
            if (token.Length < 4 || token[0] != '<' || token[^1] != '>')
                return false;

            string mention = token[1..^1];
            HyperlinkType type;
            string prefix;
            string collectionName;
            string unknownText;

            if (mention.StartsWith("@&", StringComparison.Ordinal))
            {
                type = HyperlinkType.Role;
                prefix = "@";
                collectionName = "MentionedRoles";
                unknownText = "@unknown-role";
                mention = mention[2..];
            }
            else if (mention.StartsWith("@", StringComparison.Ordinal))
            {
                type = HyperlinkType.User;
                prefix = "@";
                collectionName = "MentionedUsers";
                unknownText = "@unknown-user";
                mention = mention[1..];
            }
            else if (mention.StartsWith("#", StringComparison.Ordinal))
            {
                type = HyperlinkType.Channel;
                prefix = "#";
                collectionName = "MentionedChannels";
                unknownText = "#unknown-channel";
                mention = mention[1..];
            }
            else
            {
                return false;
            }

            if (!ulong.TryParse(mention, NumberStyles.None, CultureInfo.InvariantCulture, out ulong id))
                return false;

            object? associatedObject = FindEntity(message, collectionName, id);
            if (type == HyperlinkType.Channel && associatedObject is null)
                return false;

            string? name = ReadString(associatedObject, type == HyperlinkType.User ? "DisplayName" : "Name");
            if (string.IsNullOrWhiteSpace(name))
                name = ReadString(associatedObject, "Username");

            string label = name is null ? unknownText : prefix + name;
            link = new Hyperlink(new Run(label));
            object payload = associatedObject ?? token;
            link.Click += (_, _) => OnHyperlinkClicked(type, payload);
            return true;
        }

        private static object? FindEntity(object message, string collectionName, ulong id)
        {
            object? collection = ReadProperty(message, collectionName);
            if (collection is not IEnumerable items)
                return null;

            foreach (object? item in items)
            {
                if (item is not null && ReadUInt64(item, "Id") == id)
                    return item;
            }

            return null;
        }

        private bool TryCreateWebLink(string token, out Hyperlink? link)
        {
            link = null;
            if (!token.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                && !token.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
                && !token.StartsWith("ftp://", StringComparison.OrdinalIgnoreCase)
                && !token.StartsWith("gopher://", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            if (!Uri.TryCreate(token, UriKind.Absolute, out Uri? uri))
                return false;

            string value = uri.ToString();
            link = new Hyperlink(new Run(value));
            link.Click += (_, _) => OnHyperlinkClicked(HyperlinkType.WebLink, value);
            return true;
        }

        private static void AppendLocalText(TextBlock textBlock, string text)
        {
            StringInfo info = new(text);
            var currentRun = new Run();
            var inlines = new List<Inline>();

            for (int i = 0; i < info.LengthInTextElements; i++)
            {
                string element = info.SubstringByTextElements(i, 1);
                if (!TryGetPackagedEmoji(element, out string? fileName))
                {
                    currentRun.Text += element;
                    continue;
                }

                if (currentRun.Text.Length > 0)
                {
                    inlines.Add(currentRun);
                    currentRun = new Run();
                }

                inlines.Add(CreatePackagedEmoji(fileName!, element));
            }

            if (currentRun.Text.Length > 0 || inlines.Count == 0)
                inlines.Add(currentRun);

            foreach (Inline inline in inlines)
                textBlock.Inlines.Add(inline);
        }

        private static bool TryCreatePackagedEmoji(string token, out InlineUIContainer? emoji)
        {
            emoji = null;
            if (!TryGetPackagedEmoji(token, out string? fileName))
                return false;

            emoji = CreatePackagedEmoji(fileName!, token);
            return true;
        }

        private static bool TryGetPackagedEmoji(string text, out string? fileName)
        {
            fileName = null;
            if (UnicodeEmojiMap.TryGetValue(text, out string? unicodeFile))
            {
                fileName = unicodeFile;
                return true;
            }

            if (ShortcodeEmojiMap.TryGetValue(text, out string? shortcodeFile))
            {
                fileName = shortcodeFile;
                return true;
            }

            if (text.Length < 3 || text[0] != ':' || text[^1] != ':')
                return false;

            string name = text[1..^1];
            if (!EmojiDictionary.Map.TryGetValue(name, out string? mappedFile))
                return false;

            fileName = mappedFile;
            return true;
        }

        private static InlineUIContainer CreatePackagedEmoji(string fileName, string toolTip)
        {
            var source = new BitmapImage(new Uri($"pack://application:,,,/Aerochat;component/Resources/Emoji/{fileName}"));
            source.Freeze();

            var image = new Image
            {
                Source = source,
                Width = 19,
                Height = 19,
                VerticalAlignment = VerticalAlignment.Center,
                ToolTip = toolTip
            };

            return new InlineUIContainer(image);
        }

        public TextBlock FormatFullText(TextBlock sourceTextBlock)
        {
#if FEATURE_SELECTABLE_MESSAGE_TEXT
            var newTextBlock = new SelectableTextBlock
#else
            var newTextBlock = new TextBlock
#endif
            {
                TextWrapping = sourceTextBlock.TextWrapping,
                Foreground = Foreground,
                TextAlignment = sourceTextBlock.TextAlignment
            };

            var inlinesCopy = sourceTextBlock.Inlines.ToList();
            sourceTextBlock.Inlines.Clear();
            newTextBlock.ContextMenuOpening += TextBlock_ContextMenuOpening;

            for (int i = 0; i < inlinesCopy.Count; i++)
            {
                Inline inline = inlinesCopy[i];
                if (inline is not Run currentRun)
                {
                    newTextBlock.Inlines.Add(inline);
                    continue;
                }

                var combinedText = new StringBuilder(currentRun.Text);
                int nextIndex = i + 1;
                while (nextIndex < inlinesCopy.Count && inlinesCopy[nextIndex] is Run nextRun)
                {
                    combinedText.Append(nextRun.Text);
                    i = nextIndex;
                    nextIndex++;
                }

                AppendFormattedRuns(newTextBlock, combinedText.ToString());
            }

            return newTextBlock;
        }

        private static void AppendFormattedRuns(TextBlock target, string input)
        {
            const string pattern =
                @"(?<bold>\*\*(?<boldText>.+?)\*\*|\[b\](?<boldBb>.+?)\[/b\])"
                + @"|(?<underline>__(?<underlineText>.+?)__|\[u\](?<underlineBb>.+?)\[/u\])"
                + @"|(?<italic>(?:\*|_)(?<italicText>.+?)(?:\*|_)|\[i\](?<italicBb>.+?)\[/i\])"
                + @"|(?<strike>~~(?<strikeText>.+?)~~|\[s\](?<strikeBb>.+?)\[/s\])"
                + @"|(?m)^(?:\*|-)\s+(?<list>.+)"
                + @"|(?m)^>\s+(?<quote>.+)"
                + @"|(?m)^(?<header>#{1,6})\s+(?<headerText>.+)";

            var inlines = new List<Inline>();
            int position = 0;
            foreach (Match match in Regex.Matches(input, pattern))
            {
                if (match.Index > position)
                    inlines.Add(new Run(input.Substring(position, match.Index - position)));

                if (match.Groups["bold"].Success)
                {
                    string value = match.Groups["boldText"].Success
                        ? match.Groups["boldText"].Value
                        : match.Groups["boldBb"].Value;
                    var run = new Run(value) { FontWeight = FontWeights.Bold };
                    inlines.Add(run);
                }
                else if (match.Groups["underline"].Success)
                {
                    string value = match.Groups["underlineText"].Success
                        ? match.Groups["underlineText"].Value
                        : match.Groups["underlineBb"].Value;
                    var run = new Run(value) { TextDecorations = TextDecorations.Underline };
                    inlines.Add(run);
                }
                else if (match.Groups["italic"].Success)
                {
                    string value = match.Groups["italicText"].Success
                        ? match.Groups["italicText"].Value
                        : match.Groups["italicBb"].Value;
                    var run = new Run(value) { FontStyle = FontStyles.Italic };
                    inlines.Add(run);
                }
                else if (match.Groups["strike"].Success)
                {
                    string value = match.Groups["strikeText"].Success
                        ? match.Groups["strikeText"].Value
                        : match.Groups["strikeBb"].Value;
                    var span = new Span(new Run(value)) { TextDecorations = TextDecorations.Strikethrough };
                    inlines.Add(span);
                }
                else if (match.Groups["list"].Success)
                {
                    inlines.Add(new Run("• " + match.Groups["list"].Value));
                }
                else if (match.Groups["quote"].Success)
                {
                    var run = new Run("“" + match.Groups["quote"].Value.Trim() + "”")
                    {
                        FontStyle = FontStyles.Italic,
                        Foreground = Brushes.DimGray
                    };
                    inlines.Add(run);
                }
                else if (match.Groups["header"].Success)
                {
                    var run = new Run(match.Groups["headerText"].Value.Trim())
                    {
                        FontSize = match.Groups["header"].Value.Length switch
                        {
                            1 => 24,
                            2 => 20,
                            3 => 18,
                            _ => 16
                        },
                        FontWeight = FontWeights.Bold
                    };
                    inlines.Add(run);
                    inlines.Add(new LineBreak());
                }

                position = match.Index + match.Length;
            }

            if (position < input.Length)
                inlines.Add(new Run(input[position..]));

            foreach (Inline inline in inlines)
                target.Inlines.Add(inline);
        }

        private void TextBlock_ContextMenuOpening(object sender, ContextMenuEventArgs e)
        {
            TextBlockContextMenuOpening?.Invoke(this, e);
        }

        private void OnHyperlinkClicked(HyperlinkType type, object associatedObject)
        {
            HyperlinkClicked?.Invoke(this, new HyperlinkClickedEventArgs(type, associatedObject));
        }

        private static object? ReadProperty(object? instance, string name)
        {
            if (instance is null)
                return null;

            PropertyInfo? property = instance.GetType().GetProperty(name, BindingFlags.Instance | BindingFlags.Public);
            return property?.GetValue(instance);
        }

        private static string? ReadString(object? instance, string name)
        {
            return ReadProperty(instance, name) as string;
        }

        private static ulong ReadUInt64(object instance, string name)
        {
            object? value = ReadProperty(instance, name);
            return value is null
                ? 0
                : ulong.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture), NumberStyles.None,
                    CultureInfo.InvariantCulture, out ulong result) ? result : 0;
        }
    }

    public enum HyperlinkType
    {
        Channel,
        Role,
        User,
        WebLink,
        ServerEmoji,
    }

    public class HyperlinkClickedEventArgs : EventArgs
    {
        public HyperlinkType Type { get; }
        public object AssociatedObject { get; }

        public HyperlinkClickedEventArgs(HyperlinkType type, object associatedObject)
        {
            Type = type;
            AssociatedObject = associatedObject;
        }
    }
}
