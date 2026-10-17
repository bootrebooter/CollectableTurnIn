using CollectableTurnIn;

int passed = 0;
void Check(bool value, string description)
{
    if (!value) throw new Exception(description);
    Console.WriteLine("PASS: " + description);
    passed++;
}
BatchController Started(int count = 2)
{
    var b = new BatchController();
    b.Start(count, 0);
    return b;
}
var b = Started();
Check(b.Tick(2, true, false, 0, 600), "First request may be sent");
Check(!b.Tick(2, true, false, 800, 600) && b.Completed == 0, "No duplicate before server acknowledgement");
Check(!b.Tick(1, true, false, 900, 600) && b.Completed == 1, "Acknowledgement counts exactly one and starts cooldown");
Check(!b.Tick(1, true, false, 1499, 600), "Cooldown enforced");
Check(b.Tick(1, true, false, 1500, 600), "Next request after cooldown");
Check(!b.Tick(0, false, false, 1600, 600) && !b.Running && b.Completed == 2, "Final acknowledgement completes the batch");
b = Started(); b.Tick(2, true, false, 0, 600);
Check(!b.Tick(2, true, true, 1, 600) && !b.Running, "Confirmation dialog halts without further request");
b = Started(); b.Tick(2, true, false, 0, 600);
Check(!b.Tick(-1, false, false, 1, 600) && !b.Running, "Closed shop halts");
b = Started(); b.Tick(2, true, false, 0, 600);
Check(!b.Tick(2, true, false, 10000, 600) && !b.Running && b.Completed == 0, "No progress times out without retrying");
b = Started();
Check(!b.Tick(2, false, false, 0, 600) && !b.Tick(2, false, false, 10000, 600) && !b.Running, "Disabled hand-in button times out");
b = Started(); b.Stop("manual");
Check(!b.Tick(2, true, false, 1, 600), "Manual stop prevents further request");
b = Started(); b.Tick(2, true, false, 0, 600);
Check(!b.Tick(3, true, false, 1, 600) && !b.Running, "Growing list halts");
b = Started(3); b.Tick(3, true, false, 0, 600);
Check(!b.Tick(1, true, false, 1, 600) && !b.Running && b.Completed == 0, "Unexpected multi-item disappearance is not counted");
b = Started(0);
Check(!b.Running && !b.Tick(0, true, false, 0, 600), "Empty list never starts");
b.Start(1, 100);
Check(b.Completed == 0 && b.Tick(1, true, false, 100, 600), "Restart resets state");


b = Started(1); b.Tick(1, true, false, 0, 600); b.Tick(0, true, false, 1, 600);
Check(b.Reason == "Completed", "Successful batch exposes terminal code");
b.Reset();
Check(b.Completed == 0 && !b.Running, "Accepted pending operation cannot inherit old count");
var tracker = new OperationTracker();
var first = tracker.Begin("TurnIn");
var rejected = false;
try { tracker.Begin("TurnIn"); } catch (InvalidOperationException) { rejected = true; }
Check(rejected && tracker.Id == first, "Busy operation retains identity");
tracker.Finish("Completed", "done", 2);
tracker.Finish("Cancelled", "duplicate", 9);
var events = tracker.TakeCompletions();
Check(events.Length == 1 && !tracker.Active, "Terminal event emitted once and active state cleared");
using (var json = System.Text.Json.JsonDocument.Parse(events[0]))
{
    var r = json.RootElement;
    Check(r.GetProperty("operationId").GetString() == first && r.GetProperty("success").GetBoolean()
        && r.GetProperty("completedCount").GetInt32() == 2, "Completion JSON preserves ID, success and count");
}
Check(tracker.TakeCompletions().Length == 0, "Drained events are never resent");
var second = tracker.Begin("SwitchToScripNpc");
Check(second != first, "Each accepted operation receives a new identity");
tracker.Finish("TimedOut", "timeout", 99);
Check(tracker.LastResult is { Success: false, CompletedCount: 0, Code: "TimedOut" }, "NPC failure never reports turn-in count");
var drained = tracker.TakeCompletions();
tracker.Begin("TurnIn"); tracker.Finish("Cancelled", "stop before start", b.Completed);
Check(drained.Length == 1 && tracker.TakeCompletions().Length == 1, "Reentrant completion is deferred to next drain");
Check(tracker.LastResult is { CompletedCount: 0, Success: false }, "Pending cancellation has zero completed items");
tracker.Begin("TurnIn"); tracker.Finish("Unloaded", "unload", 1);
Check(tracker.LastResult is { Code: "Unloaded", Success: false }, "Unload terminates accepted operation");
using (var reply = System.Text.Json.JsonDocument.Parse(OperationTracker.Reply(false, "WrongThread")))
{
    Check(!reply.RootElement.GetProperty("accepted").GetBoolean()
        && reply.RootElement.GetProperty("operationId").ValueKind == System.Text.Json.JsonValueKind.Null,
        "Rejected reply has no operation identity");
}
Console.WriteLine($"{passed} checks passed.");