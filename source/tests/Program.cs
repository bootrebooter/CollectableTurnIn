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
Check(b.Tick(1, true, false, 900, 600) && b.Completed == 1, "Long acknowledgement overlaps minimum interval; no extra cooldown");
Check(!b.Tick(1, true, false, 1499, 600), "No duplicate while second request is pending");
Check(!b.Tick(1, true, false, 1500, 600), "Elapsed interval never bypasses acknowledgement");
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

b = Started(); b.Tick(2, true, false, 0, 100);
Check(!b.Tick(1, true, false, 50, 100), "Fast acknowledgement still respects send interval");
Check(!b.Tick(1, true, false, 99, 100) && b.Tick(1, true, false, 100, 100), "Next send at minimum interval from previous send");
b = Started(); b.Tick(2, true, false, 0, 0);
Check(!b.Tick(2, true, false, 1, 0), "Zero interval never duplicates unacknowledged request");
Check(b.Tick(1, true, false, 2, 0), "Zero interval proceeds immediately after acknowledgement");
b = Started(); b.Tick(2, true, false, 0, 100);
Check(!b.Tick(1, false, false, 500, 100) && b.Tick(1, true, false, 501, 100), "Disabled game button still blocks fast mode");
var sw = new SwitchController(); sw.Start(0);
Check(sw.Tick(0, true, false, false) == SwitchController.Step.Wait, "Old shop blocks NPC interaction");
Check(sw.Tick(100, false, true, false) == SwitchController.Step.Wait, "Conversation busy blocks NPC interaction");
Check(sw.Tick(200, false, false, false) == SwitchController.Step.Wait, "Closing frame does not immediately interact");
Check(sw.Tick(449, false, false, false) == SwitchController.Step.Wait
    && sw.Tick(450, false, false, false) == SwitchController.Step.Interact, "250ms stable idle permits first interaction");
Check(sw.Tick(1450, false, true, false) == SwitchController.Step.Wait && sw.Attempts == 1, "No retry while new conversation is active");
Check(sw.Tick(1500, false, true, true) == SwitchController.Step.Completed && !sw.Running, "Opened target menu completes while occupied");
Check(sw.Tick(2000, false, false, false) == SwitchController.Step.Wait, "Completed switch never interacts again");
sw.Start(0); sw.Tick(0, false, false, false); sw.Tick(250, false, false, false);
Check(sw.Tick(1249, false, false, false) == SwitchController.Step.Wait
    && sw.Tick(1250, false, false, false) == SwitchController.Step.Interact, "Failed interaction retry is rate limited");
Check(sw.Tick(2250, false, false, false) == SwitchController.Step.Interact
    && sw.Tick(3250, false, false, false) == SwitchController.Step.Wait && sw.Attempts == 3, "At most three interaction attempts");
Check(sw.Tick(10000, false, false, false) == SwitchController.Step.TimedOut && !sw.Running, "No response ends with bounded timeout");
sw.Start(0);
Check(sw.Tick(0, false, true, true) == SwitchController.Step.Wait, "Existing menu before first attempt is not success");
sw.Stop();
Check(sw.Tick(300, false, false, false) == SwitchController.Step.Wait, "Manual stop prevents NPC interaction");
sw.Start(0); sw.Tick(0, false, false, false); sw.Tick(200, false, true, false); sw.Tick(250, false, false, false);
Check(sw.Tick(499, false, false, false) == SwitchController.Step.Wait
    && sw.Tick(500, false, false, false) == SwitchController.Step.Interact, "Reappearing occupied condition resets settle time");
Console.WriteLine($"{passed} checks passed.");
