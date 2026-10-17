// SPDX-License-Identifier: AGPL-3.0-only
namespace CollectableTurnIn;

// No native calls: wait for the old conversation to release before interacting.
public sealed class SwitchController
{
    public enum Step { Wait, Interact, Completed, TimedOut }
    public bool Running { get; private set; }
    public int Attempts { get; private set; }
    private long deadline;
    private long idleSince = -1;
    private long nextAttempt;
    public void Start(long now)
    {
        Running = true;
        Attempts = 0;
        deadline = now + 10_000;
        idleSince = -1;
        nextAttempt = now;
    }
    public void Stop() => Running = false;
    public Step Tick(long now, bool oldShopVisible, bool occupied, bool targetMenuOpen)
    {
        if (!Running) return Step.Wait;
        if (Attempts > 0 && !oldShopVisible && targetMenuOpen)
        { Running = false; return Step.Completed; }
        if (now >= deadline) { Running = false; return Step.TimedOut; }
        if (oldShopVisible || occupied) { idleSince = -1; return Step.Wait; }
        if (idleSince < 0) idleSince = now;
        // Do not race the conversation's closing frame; never retry while occupied.
        if (now - idleSince < 250 || now < nextAttempt || Attempts >= 3) return Step.Wait;
        Attempts++;
        nextAttempt = now + 1_000;
        return Step.Interact;
    }
}
