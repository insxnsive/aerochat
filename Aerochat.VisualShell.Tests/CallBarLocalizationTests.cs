using System.Globalization;
using Aerochat.Localization;
namespace Aerochat.VisualShell.Tests;
public sealed class CallBarLocalizationTests
{
    [TestCase("CallStartButton", "Start Call")]
    [TestCase("CallAcceptButton", "Accept Call")]
    [TestCase("CallMute", "Mute")]
    [TestCase("CallUnmute", "Unmute")]
    [TestCase("CallStatusStarting", "Starting call…")]
    [TestCase("CallStatusRinging", "Calling {0}…")]
    [TestCase("CallStatusIncoming", "Incoming call from {0}")]
    [TestCase("CallStatusWaiting", "Waiting for {0}…")]
    [TestCase("CallStatusConnecting", "Connecting…")]
    [TestCase("CallStatusConnectedMuted", "Connected · Muted")]
    [TestCase("CallStatusConnected", "Connected")]
    [TestCase("CallStatusFailed", "Call failed")]
    [TestCase("CallStatusEnded", "Call ended")]
    [TestCase("CallStatusIdle", "Voice call")]
    public void Call_bar_key_preserves_existing_wording_and_English_fallback(string key, string expected)
    {
        var localization = new LocalizationManager();
        foreach (string language in new[] { "en", "en-US", "fr" })
        {
            localization.LoadLanguage(language);
            Assert.That(localization.Get(key), Is.EqualTo(expected), $"{language}: {key}");
            if (expected.Contains("{0}", StringComparison.Ordinal))
            {
                string name = "Example {room}";
                Assert.That(string.Format(CultureInfo.InvariantCulture, localization.Get(key), name),
                    Is.EqualTo(expected.Replace("{0}", name, StringComparison.Ordinal)));
            }
        }
    }
}
