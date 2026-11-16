namespace NoTunes.Music;

public sealed record SyncResult(SyncPlan Plan, bool DryRun, int VerifiedBatches);
