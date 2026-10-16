// SPDX-License-Identifier: AGPL-3.0-only
namespace CollectableTurnIn;

// No game memory access here: one request at a time, confirmed by a list decrease.
public sealed class BatchController
{
    public bool Running { get; private set; }
    public int Completed { get; private set; }
    public string Status { get; private set; } = "待機中";
    private int previousCount;
    private bool awaiting;
    private long deadline;
    private long nextRequest;
    public void Start(int count, long now)
    {
        Completed = 0;
        previousCount = count;
        awaiting = false;
        nextRequest = now;
        deadline = now + 10_000;
        Running = count > 0;
        Status = Running ? "納品中" : "納品対象がありません";
    }
    public void Stop(string reason) { Running = false; awaiting = false; Status = reason; }
    public bool Tick(int count, bool ready, bool dialog, long now, int interval)
    {
        if (!Running) return false;
        if (dialog) { Stop("確認画面が表示されたため停止しました"); return false; }
        if (count < 0) { Stop("納品画面が閉じられたため停止しました"); return false; }
        if (count > previousCount) { Stop("対象一覧が変化したため停止しました"); return false; }
        if (awaiting && count < previousCount)
        {
            // More than one disappearance cannot be attributed to our one request.
            if (count != previousCount - 1) { Stop("対象一覧が大きく変化したため停止しました"); return false; }
            Completed++;
            previousCount = count;
            awaiting = false;
            nextRequest = now + interval;
            deadline = now + 10_000;
        }
        else if (!awaiting && count != previousCount)
        { Stop("操作待ちの間に対象一覧が変化したため停止しました"); return false; }
        if (count == 0) { Stop("納品が完了しました"); return false; }
        if (now >= deadline) { Stop("10秒間進まなかったため停止しました"); return false; }
        if (awaiting || !ready || now < nextRequest) return false;
        awaiting = true;
        deadline = now + 10_000;
        return true;
    }
}
