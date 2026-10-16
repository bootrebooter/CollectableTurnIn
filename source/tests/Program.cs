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
Console.WriteLine($"{passed} checks passed.");
