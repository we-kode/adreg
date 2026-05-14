using Quartz;
using Shared.Data;

namespace AdminApp.Jobs
{
    public class CleanupJob(AppDbContext db, ILogger<CleanupJob> logger) : IJob
    {
        public Task Execute(IJobExecutionContext context)
        {
            logger.LogInformation("Bereinigung von ungültigen Links gestartet: {Time}", DateTimeOffset.Now);

            CleanUpLinks();

            logger.LogInformation("Bereinigung von ungültigen Links erfolgreich beendet.");
            return Task.CompletedTask;
        }

        private void CleanUpLinks()
        {
            // Cleanup registration links, which are used or expired.
            db.Links.RemoveRange(db.Links.Where(l => l.ValidUntil < DateTime.UtcNow || (l.IsSingleUse && l.IsUsed)));

            // Cleanup reset password links, which are used or expired.
            db.PasswordResetLinks.RemoveRange(db.PasswordResetLinks.Where(l => l.ValidUntil < DateTime.UtcNow || l.IsUsed));
        }
    }
}
