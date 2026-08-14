namespace XiaopacaiWeb.Services;

/// <summary>
/// [SEC-P1] Ticket 定时清理后台任务：每 5 分钟清理过期/已消费的一次性 Ticket，
/// 控制内存占用（登录/重置 Ticket 均存于内存 TicketStore）
/// </summary>
public class TicketCleanupService : BackgroundService
{
    private readonly TicketStore _tickets;

    public TicketCleanupService(TicketStore tickets)
    {
        _tickets = tickets;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(5));
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                _tickets.CleanupExpired();
            }
        }
        catch (OperationCanceledException)
        {
            // 停机信号，正常退出
        }
    }
}
