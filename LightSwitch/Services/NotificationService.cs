using Windows.Data.Xml.Dom;
using Windows.UI.Notifications;

namespace LightSwitch.Services;

// Toast notifications (works in packaged apps; no special capability needed).
public static class NotificationService
{
    public static void ShowThemeChanged(bool isLight)
    {
        Show("LightSwitch", isLight ? "已切换到浅色模式" : "已切换到深色模式");
    }

    public static void ShowError(string message)
    {
        Show("LightSwitch", message);
    }

    private static void Show(string title, string message)
    {
        try
        {
            var xml = ToastNotificationManager.GetTemplateContent(ToastTemplateType.ToastText02);
            var texts = xml.GetElementsByTagName("text");
            texts[0].AppendChild(xml.CreateTextNode(title));
            texts[1].AppendChild(xml.CreateTextNode(message));

            var toast = new ToastNotification(xml)
            {
                ExpirationTime = DateTime.Now.AddSeconds(5),
            };
            ToastNotificationManager.CreateToastNotifier().Show(toast);
        }
        catch (Exception e)
        {
            Logger.Error("[Notify] Failed to show toast: " + e.Message);
        }
    }
}
