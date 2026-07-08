using Microsoft.Windows.AppNotifications;
using Microsoft.Windows.AppNotifications.Builder;

namespace DeskBox.Services;

public sealed class NativeAppNotificationService : IDisposable
{
    private readonly Action<string> _activated;
    private bool _isRegistered;
    private bool _isDisposed;

    public NativeAppNotificationService(Action<string> activated)
    {
        _activated = activated;
    }

    public bool Register()
    {
        if (_isDisposed)
        {
            return false;
        }

        if (_isRegistered)
        {
            return true;
        }

        try
        {
            AppNotificationManager.Default.NotificationInvoked += OnNotificationInvoked;
            AppNotificationManager.Default.Register();
            _isRegistered = true;
            App.Log("[Notification] Native app notification registered");
            return true;
        }
        catch (Exception ex)
        {
            AppNotificationManager.Default.NotificationInvoked -= OnNotificationInvoked;
            App.Log($"[Notification] Native app notification registration failed: {ex}");
            return false;
        }
    }

    public bool TryShow(string title, string message, IReadOnlyDictionary<string, string>? arguments = null)
    {
        if (_isDisposed || !Register())
        {
            return false;
        }

        try
        {
            var builder = new AppNotificationBuilder()
                .AddText(title)
                .AddText(message);

            if (arguments is not null)
            {
                foreach (var (key, value) in arguments)
                {
                    builder.AddArgument(key, value);
                }
            }

            AppNotificationManager.Default.Show(builder.BuildNotification());
            return true;
        }
        catch (Exception ex)
        {
            App.Log($"[Notification] Native app notification show failed: {ex}");
            return false;
        }
    }

    public void Dispose()
    {
        if (_isDisposed)
        {
            return;
        }

        _isDisposed = true;
        try
        {
            AppNotificationManager.Default.NotificationInvoked -= OnNotificationInvoked;
            if (_isRegistered)
            {
                AppNotificationManager.Default.Unregister();
            }
        }
        catch (Exception ex)
        {
            App.Log($"[Notification] Native app notification unregister failed: {ex.Message}");
        }

        _isRegistered = false;
    }

    private void OnNotificationInvoked(AppNotificationManager sender, AppNotificationActivatedEventArgs args)
    {
        _activated(args.Argument);
    }
}
