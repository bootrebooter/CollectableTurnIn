// SPDX-License-Identifier: AGPL-3.0-only
using System.Text.Json;
using Dalamud.Plugin.Ipc;

namespace CollectableTurnIn;

public sealed unsafe partial class Plugin
{
    private readonly OperationTracker operations = new();
    private readonly List<ICallGateProvider> ipcFunctions = new();
    private ICallGateProvider<string, object>? completedEvent;
    private string statusJson = "{}";

    private void RegisterIpc()
    {
        try
        {
            Register("CollectableTurnIn.GetApiVersion", () => OperationTracker.ApiVersion);
            Register("CollectableTurnIn.GetStatus", () => Volatile.Read(ref statusJson));
            Register("CollectableTurnIn.StartTurnIn", () => RequestOperation(false));
            Register("CollectableTurnIn.SwitchToScripNpc", () => RequestOperation(true));
            Register("CollectableTurnIn.Stop", StopIpc);
            completedEvent = PI.GetIpcProvider<string, object>("CollectableTurnIn.OnCompleted");
            PublishSnapshot();
        }
        catch
        {
            UnregisterIpc();
            throw;
        }
    }

    private void Register<T>(string name, Func<T> callback)
    {
        var provider = PI.GetIpcProvider<T>(name);
        provider.RegisterFunc(callback);
        ipcFunctions.Add(provider);
    }

    private void UnregisterIpc()
    {
        foreach (var provider in ipcFunctions) provider.UnregisterFunc();
        ipcFunctions.Clear();
        completedEvent = null;
    }

    private string RequestOperation(bool switchNpc)
    {
        if (disposed) return OperationTracker.Reply(false, "Unavailable");
        // Never access game pointers on arbitrary IPC caller threads.
        if (!Framework.IsInFrameworkUpdateThread) return OperationTracker.Reply(false, "WrongThread");
        if (operations.Active || Busy || startRequested || switchRequested) return OperationTracker.Reply(false, "Busy");
        try
        {
            if (!Client.IsLoggedIn || Objects.LocalPlayer == null) return OperationTracker.Reply(false, "NotLoggedIn");
            var shop = Addon("CollectablesShop");
            if (!Visible(shop)) return OperationTracker.Reply(false, "ShopNotOpen");
            if (HasDialog()) return OperationTracker.Reply(false, "DialogOpen");
            if (switchNpc)
            {
                if (FindScripNpc() == null) return OperationTracker.Reply(false, "NpcNotFound");
            }
            else
            {
                if (handIn == null) return OperationTracker.Reply(false, "UnsupportedGameVersion");
                if (Count(shop) <= 0) return OperationTracker.Reply(false, "NoItems");
                if (!CanHandIn(shop)) return OperationTracker.Reply(false, "NotReady");
            }
            batch.Reset();
            var id = operations.Begin(switchNpc ? "SwitchToScripNpc" : "TurnIn");
            territory = Client.TerritoryType;
            startRequested = !switchNpc;
            switchRequested = switchNpc;
            status = "操作を受け付けました";
            PublishSnapshot();
            return OperationTracker.Reply(true, "Accepted", id);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "IPC request failed");
            if (operations.Active) StopAll("IPC操作の開始に失敗しました", "Error");
            return OperationTracker.Reply(false, "Error");
        }
    }

    private string StopIpc()
    {
        if (disposed) return OperationTracker.Reply(false, "Unavailable");
        if (!Framework.IsInFrameworkUpdateThread) return OperationTracker.Reply(false, "WrongThread");
        if (!operations.Active && !Busy && !startRequested && !switchRequested)
            return OperationTracker.Reply(false, "NotRunning");
        var id = operations.Id;
        StopAll("IPCから停止しました", "Cancelled");
        return OperationTracker.Reply(true, "Stopped", id);
    }

    private void PublishSnapshot()
    {
        var state = disposed ? "Unavailable" : startRequested ? "PendingTurnIn" : switchRequested ? "PendingSwitch"
            : batch.Running ? "TurningIn" : phase != SwitchPhase.None ? "SwitchingNpc" : "Idle";
        Volatile.Write(ref statusJson, JsonSerializer.Serialize(new
        {
            apiVersion = OperationTracker.ApiVersion,
            state,
            busy = operations.Active || Busy || startRequested || switchRequested,
            operationId = operations.Id,
            operation = operations.Operation,
            completedCount = operations.Operation == "TurnIn" && !startRequested ? batch.Completed : 0,
            message = status,
            turnInSupported = handIn != null,
            lastResult = operations.LastResult,
        }, OperationTracker.JsonOptions));
    }

    private void FinishOperation(string code)
    {
        operations.Finish(code, status, batch.Completed);
        PublishSnapshot();
    }

    private void SendCompletionEvents()
    {
        // Snapshot the outbox: subscriber re-entry cannot cause an unbounded dispatch loop.
        foreach (var payload in operations.TakeCompletions())
        {
            try { completedEvent?.SendMessage(payload); }
            catch (Exception ex) { Log.Error(ex, "IPC completion subscriber failed"); }
        }
    }
}
