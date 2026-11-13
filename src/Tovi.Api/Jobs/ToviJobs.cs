namespace Tovi.Api.Jobs;

public sealed class ToviJobs(IServiceScopeFactory scopes)
{
    public Task ExamTransitions() => Run(s => s.GetRequiredService<Services.LearningWork>().TransitionExamsAsync(CancellationToken.None));
    public Task Subscriptions() => Run(s => s.GetRequiredService<Services.CommerceWork>().ExpireAndConsumeAsync(CancellationToken.None));
    public Task Payments() => Run(s => s.GetRequiredService<Services.CommerceWork>().ReconcilePaymentsAsync(CancellationToken.None));
    public Task Notifications() => Run(s => s.GetRequiredService<Services.NotifyWork>().DispatchDueAsync(CancellationToken.None));
    public Task Trials() => Run(s => s.GetRequiredService<Services.NotifyWork>().TrialNoticesAsync(CancellationToken.None));
    public Task SessionReminders() => Run(s => s.GetRequiredService<Services.NotifyWork>().SessionRemindersAsync(CancellationToken.None));

    async Task Run(Func<IServiceProvider, Task> work)
    {
        using var scope = scopes.CreateScope();
        await work(scope.ServiceProvider);
    }
}
