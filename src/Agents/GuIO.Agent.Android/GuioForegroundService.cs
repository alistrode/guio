using Android.App;
using Android.Content;
using Android.OS;
using Android.Util;
using GuIO.Driver.Android;

namespace GuIO.Agent.Android;

/// <summary>GuIO 前台服务：常驻保活，维持无障碍连接与本地 RPC。</summary>
[Service(Exported = false, ForegroundServiceType = global::Android.Content.PM.ForegroundService.TypeSpecialUse)]
public sealed class GuioForegroundService : global::Android.App.Service
{
    private const string ChannelId = "guio_agent";
    private const int NotificationId = 1001;

    public override IBinder? OnBind(Intent? intent) => null;

    public override StartCommandResult OnStartCommand(Intent? intent, StartCommandFlags flags, int startId)
    {
        CreateChannel();
        var notification = BuildNotification();
        if (Build.VERSION.SdkInt >= BuildVersionCodes.Q)
            StartForeground(NotificationId, notification,
                global::Android.Content.PM.ForegroundService.TypeSpecialUse);
        else
            StartForeground(NotificationId, notification);

        GuioRuntime.StartLocalServer(this);
        return StartCommandResult.Sticky;
    }

    private void CreateChannel()
    {
        if (Build.VERSION.SdkInt < BuildVersionCodes.O) return;
        var mgr = (NotificationManager?)GetSystemService(NotificationService);
        if (mgr is null) return;
        if (mgr.GetNotificationChannel(ChannelId) is not null) return;
        var ch = new NotificationChannel(ChannelId, "GuIO 自动化服务", NotificationImportance.Low)
        {
            Description = "保持 GuIO 自动化能力常驻运行",
        };
        mgr.CreateNotificationChannel(ch);
    }

    private Notification BuildNotification()
    {
        var builder = new Notification.Builder(this, ChannelId)
            .SetContentTitle("GuIO 运行中")
            .SetContentText("自动化服务已就绪")
            .SetSmallIcon(global::Android.Resource.Drawable.IcDialogInfo)
            .SetOngoing(true);
        if (Build.VERSION.SdkInt >= BuildVersionCodes.O) return builder.Build()!;
        return builder.Build()!;
    }
}
