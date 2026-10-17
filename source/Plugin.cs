// SPDX-License-Identifier: AGPL-3.0-only
// Adapted from DailyRoutines.ModulesPublic/Interface/AutoCollectableExchange.cs.
using System.Numerics;
using System.Runtime.InteropServices;
using Dalamud.Bindings.ImGui;
using Dalamud.Configuration;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Game.Command;
using Dalamud.IoC;
using Dalamud.Hooking;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Client.UI.Misc;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Lumina.Excel.Sheets;
using NativeGameObject = FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject;

namespace CollectableTurnIn;

[Serializable]
public sealed class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 1;
    public int IntervalMs = 600;
    public bool Notify = true;
    public bool SuppressInsufficientItemMessage = true;
}

public sealed unsafe partial class Plugin : IDalamudPlugin
{
    [PluginService] internal static IDalamudPluginInterface PI { get; private set; } = null!;
    [PluginService] internal static IFramework Framework { get; private set; } = null!;
    [PluginService] internal static IGameGui Gui { get; private set; } = null!;
    [PluginService] internal static ICommandManager Commands { get; private set; } = null!;
    [PluginService] internal static ISigScanner Scanner { get; private set; } = null!;
    [PluginService] internal static IObjectTable Objects { get; private set; } = null!;
    [PluginService] internal static IDataManager Data { get; private set; } = null!;
    [PluginService] internal static ICondition Conditions { get; private set; } = null!;
    [PluginService] internal static IClientState Client { get; private set; } = null!;
    [PluginService] internal static IChatGui Chat { get; private set; } = null!;
    [PluginService] internal static IToastGui Toast { get; private set; } = null!;
    [PluginService] internal static IPluginLog Log { get; private set; } = null!;
    [PluginService] internal static IGameInteropProvider Interop { get; private set; } = null!;

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate nint HandInDelegate(AgentInterface* agent);
    private const string HandInSignature = "48 89 6C 24 ?? 48 89 74 24 ?? 57 41 56 41 57 48 81 EC ?? ?? ?? ?? 48 8B 05 ?? ?? ?? ?? 48 33 C4 48 89 84 24 ?? ?? ?? ?? 48 8B F1 48 8B 49";
    private readonly HandInDelegate? handIn;
    private delegate void LogUpdateDelegate(RaptureLogModule* module);
    private Hook<LogUpdateDelegate>? logHook;
    private bool insufficientItems;
    private readonly Configuration config;
    private readonly BatchController batch = new();
    private string? bindingError;
    private string status = "収集品納品画面で対象を選び、一括納品を押してください。";
    private bool pinned;
    private bool disposed;
    private bool startRequested;
    private bool switchRequested;
    private nint batchAddon;
    private ushort batchAddonId;
    private uint territory;
    private SwitchPhase phase;
    private ulong npcId;
    private long switchDeadline;
    private enum SwitchPhase { None, Closing, Opening }
    private bool Busy => batch.Running || phase != SwitchPhase.None;

    public Plugin()
    {
        config = PI.GetPluginConfig() as Configuration ?? new Configuration();
        config.IntervalMs = Math.Clamp(config.IntervalMs, 300, 2000);
        try
        {
            if (Scanner.TryScanText(HandInSignature, out var address))
                handIn = Marshal.GetDelegateForFunctionPointer<HandInDelegate>(address);
            else bindingError = "このゲーム版では納品処理を特定できません。一括納品は無効です。";
        }
        catch (Exception ex) { bindingError = "納品処理の初期化に失敗しました。"; Log.Error(ex, bindingError); }
        try
        {
            logHook = Interop.HookFromAddress<LogUpdateDelegate>((nint)RaptureLogModule.MemberFunctionPointers.Update, FilterLog);
            logHook.Enable();
        }
        catch (Exception ex)
        {
            logHook?.Dispose();
            logHook = null;
            Log.Warning(ex, "Optional insufficient-item message filter unavailable");
        }
        Commands.AddHandler("/cti", new CommandInfo(OnCommand) { HelpMessage = "収集品一括納品の操作画面。/cti stop で停止。" });
        PI.UiBuilder.Draw += Draw;
        PI.UiBuilder.OpenMainUi += Open;
        PI.UiBuilder.OpenConfigUi += Open;
        Framework.Update += Update;
        try { RegisterIpc(); }
        catch { Dispose(); throw; }
    }

    private void Open() => pinned = true;
    private void OnCommand(string command, string args)
    {
        if (args.Trim().Equals("stop", StringComparison.OrdinalIgnoreCase)) StopAll("手動で停止しました");
        else pinned = !pinned;
    }
    private static AtkUnitBase* Addon(string name) => (AtkUnitBase*)Gui.GetAddonByName(name).Address;
    private static bool Visible(AtkUnitBase* addon) => addon != null && addon->IsReady && addon->IsVisible && addon->RootNode != null;
    private static bool HasDialog() => Visible(Addon("SelectYesno")) || Visible(Addon("SelectOk"));
    private static int Count(AtkUnitBase* addon)
    {
        if (!Visible(addon)) return -1;
        var node = addon->GetComponentNodeById(31);
        if (node == null) return -1;
        var list = node->GetAsAtkComponentList();
        return list == null ? -1 : list->ListLength;
    }
    private static bool CanHandIn(AtkUnitBase* addon)
    {
        if (!Visible(addon)) return false;
        var button = addon->GetComponentButtonById(51);
        return button != null && button->IsEnabled;
    }

    private void Draw()
    {
        if (disposed) return;
        var shop = Addon("CollectablesShop");
        if (!Visible(shop) && !pinned && !Busy) return;
        // Separate overlay avoids replacing the game's own button events.
        if (Visible(shop))
        {
            var viewport = ImGui.GetMainViewport();
            var x = viewport.Pos.X + shop->X + shop->RootNode->Width * shop->Scale + 8;
            var y = viewport.Pos.Y + shop->Y;
            ImGui.SetNextWindowPos(new Vector2(Math.Clamp(x, viewport.WorkPos.X, Math.Max(viewport.WorkPos.X, viewport.WorkPos.X + viewport.WorkSize.X - 410)),
                Math.Clamp(y, viewport.WorkPos.Y, Math.Max(viewport.WorkPos.Y, viewport.WorkPos.Y + viewport.WorkSize.Y - 280))), ImGuiCond.Always);
        }
        ImGui.SetNextWindowSize(new Vector2(390, 0), ImGuiCond.FirstUseEver);
        if (ImGui.Begin("収集品一括納品###CollectableTurnIn", ImGuiWindowFlags.AlwaysAutoResize))
        {
            ImGui.TextWrapped("現在の納品一覧を連続処理します。実行中は対象やカテゴリを変更しないでください。");
            if (bindingError != null) ImGui.TextWrapped(bindingError);
            ImGui.BeginDisabled(Busy || handIn == null || !CanHandIn(shop) || HasDialog());
            if (ImGui.Button("一括納品")) _ = Framework.RunOnFrameworkThread(() => RequestOperation(false));
            ImGui.EndDisabled();
            ImGui.SameLine();
            ImGui.BeginDisabled(!Busy && !startRequested && !switchRequested);
            if (ImGui.Button("停止")) StopAll("手動で停止しました");
            ImGui.EndDisabled();
            ImGui.BeginDisabled(Busy || HasDialog() || !Visible(shop));
            if (ImGui.Button("スクリップ交換NPCへ")) _ = Framework.RunOnFrameworkThread(() => RequestOperation(true));
            ImGui.EndDisabled();
            ImGui.TextWrapped(status);
            ImGui.Text($"確認済み納品数: {batch.Completed}");
            if (ImGui.CollapsingHeader("設定"))
            {
                if (ImGui.SliderInt("納品間隔 (ms)", ref config.IntervalMs, 300, 2000)) PI.SavePluginConfig(config);
                if (ImGui.Checkbox("終了時にチャット・画面通知", ref config.Notify)) PI.SavePluginConfig(config);
                if (ImGui.Checkbox("連続納品中の素材不足メッセージをまとめる", ref config.SuppressInsufficientItemMessage)) PI.SavePluginConfig(config);
                if (logHook == null) ImGui.TextWrapped("素材不足メッセージの整理は、この環境では利用できません。");
                ImGui.Checkbox("納品画面を閉じても表示", ref pinned);
                ImGui.TextWrapped("近くのスクリップ取引窓口に話しかけます。移動や交換品の購入は行いません。");
            }
        }
        ImGui.End();
    }

    private void Update(IFramework framework)
    {
        if (disposed) return;
        try
        {
            if (!Client.IsLoggedIn || Objects.LocalPlayer == null)
            {
                if (operations.Active || Busy || startRequested || switchRequested) StopAll("ログアウトのため停止しました", "LoggedOut");
                return;
            }
            if (operations.Active && Client.TerritoryType != territory) { StopAll("エリアが変わったため停止しました", "TerritoryChanged"); return; }
            if (insufficientItems)
            {
                insufficientItems = false;
                if (batch.Running) { StopAll("納品可能なアイテムが不足しているため停止しました", "InsufficientItems"); return; }
            }
            var now = Environment.TickCount64;
            var shop = Addon("CollectablesShop");
            if (startRequested)
            {
                startRequested = false;
                if (!Busy && handIn != null && CanHandIn(shop) && !HasDialog())
                {
                    territory = Client.TerritoryType;
                    batchAddon = (nint)shop;
                    batchAddonId = shop->Id;
                    batch.Start(Count(shop), now);
                    status = batch.Status;
                    if (!batch.Running) FinishOperation("NoItems");
                }
                else StopAll("開始前に納品画面の状態が変わりました", "PreconditionsChanged");
            }
            if (switchRequested)
            {
                switchRequested = false;
                if (!Busy && Visible(shop) && !HasDialog()) BeginSwitch(shop, now);
                else StopAll("切り替え前に納品画面の状態が変わりました", "PreconditionsChanged");
            }
            if (batch.Running)
            {
                if (!Visible(shop) || (nint)shop != batchAddon || shop->Id != batchAddonId)
                { StopAll("納品画面が閉じられたため停止しました", "ShopClosed"); return; }
                if (batch.Tick(Count(shop), CanHandIn(shop), HasDialog(), now, config.IntervalMs))
                {
                    var module = AgentModule.Instance();
                    var agent = module == null ? null : module->GetAgentByInternalId(AgentId.CollectablesShop);
                    if (agent == null) { StopAll("納品処理を取得できませんでした", "Error"); return; }
                    handIn!(agent);
                }
                status = batch.Status;
                if (!batch.Running)
                {
                    FinishOperation(batch.Reason);
                    Notify($"{status}（確認済み {batch.Completed} 件）");
                }
            }
            if (phase != SwitchPhase.None) UpdateSwitch(now);
        }
        catch (Exception ex) { Log.Error(ex, "Collectable Turn In failed"); StopAll("エラーのため停止しました。/xllog を確認してください。", "Error"); }
        finally { PublishSnapshot(); SendCompletionEvents(); }
    }

    private IGameObject? FindScripNpc()
    {
        var player = Objects.LocalPlayer;
        if (player == null) return null;
        // Same localized NPC-name anchor as upstream; no hard-coded Japanese label.
        var expectedName = Data.GetExcelSheet<ENpcResident>().GetRow(1001617).Singular.ToString();
        if (string.IsNullOrWhiteSpace(expectedName)) return null;
        return Objects.Where(o => o.ObjectKind == ObjectKind.EventNpc && o.IsTargetable
                && string.Equals(o.Name.TextValue, expectedName, StringComparison.OrdinalIgnoreCase)
                && Vector3.Distance(player.Position, o.Position) <= 6f)
            .OrderBy(o => Vector3.DistanceSquared(player.Position, o.Position)).FirstOrDefault();
    }
    private void FilterLog(RaptureLogModule* module)
    {
        try
        {
            if (!disposed && batch.Running)
            {
                foreach (ref var item in module->LogMessageQueue)
                {
                    if (item.LogMessageId != 1941) continue;
                    insufficientItems = true;
                    if (config.SuppressInsufficientItemMessage) item.LogMessageId = 0;
                }
            }
        }
        catch (Exception ex) { Log.Error(ex, "Failed to inspect hand-in message"); }
        logHook!.Original(module);
    }
    private void BeginSwitch(AtkUnitBase* shop, long now)
    {
        var npc = FindScripNpc();
        if (npc == null) { StopAll("6m以内にスクリップ取引窓口が見つかりません。窓口に近づいてください。", "NpcNotFound"); return; }
        territory = Client.TerritoryType;
        npcId = npc.GameObjectId;
        phase = SwitchPhase.Closing;
        switchDeadline = now + 10_000;
        status = "納品画面を閉じ、スクリップ交換NPCに切り替えています…";
        shop->Close(true);
    }
    private void UpdateSwitch(long now)
    {
        if (HasDialog()) { StopAll("確認画面が表示されたため切り替えを停止しました", "DialogOpen"); return; }
        if (now >= switchDeadline) { StopAll("NPCへの切り替えが10秒以内に完了しませんでした", "TimedOut"); return; }
        if (phase == SwitchPhase.Opening)
        {
            if (Visible(Addon("InclusionShop")) || Visible(Addon("SelectIconString")) || Visible(Addon("SelectString")) || Visible(Addon("ShopExchangeCurrency")))
            { phase = SwitchPhase.None; status = "スクリップ交換NPCの画面が開きました。交換先を選んでください。"; FinishOperation("Completed"); }
            return;
        }
        if (Visible(Addon("CollectablesShop")) || Conditions[ConditionFlag.OccupiedInEvent]) return;
        var npc = Objects.FirstOrDefault(o => o.GameObjectId == npcId);
        var player = Objects.LocalPlayer;
        if (npc == null || player == null || !npc.IsTargetable || Vector3.Distance(player.Position, npc.Position) > 6f)
        { StopAll("交換NPCが操作可能な範囲にいないため停止しました", "NpcNotFound"); return; }
        var targetSystem = TargetSystem.Instance();
        if (targetSystem == null) { StopAll("NPC操作を初期化できませんでした", "Error"); return; }
        // Use the global client's normal interaction path (including line-of-sight checks).
        // Upstream sends EventStart packets; we do not need its CN-specific opcode resolver.
        targetSystem->InteractWithObject((NativeGameObject*)npc.Address, true);
        phase = SwitchPhase.Opening;
        switchDeadline = now + 10_000;
    }
    private void StopAll(string reason, string code = "Cancelled")
    {
        startRequested = switchRequested = false;
        insufficientItems = false;
        phase = SwitchPhase.None;
        batch.Stop(reason);
        status = reason;
        FinishOperation(code);
        Notify($"{reason}（確認済み {batch.Completed} 件）");
    }
    private void Notify(string message)
    {
        if (!config.Notify) return;
        Chat.Print($"[収集品一括納品] {message}");
        Toast.ShowNormal(message);
    }
    public void Dispose()
    {
        disposed = true;
        batch.Stop("無効化");
        startRequested = switchRequested = false;
        phase = SwitchPhase.None;
        status = "プラグインを無効化しました";
        FinishOperation("Unloaded");
        SendCompletionEvents();
        UnregisterIpc();
        logHook?.Dispose();
        logHook = null;
        Framework.Update -= Update;
        PI.UiBuilder.Draw -= Draw;
        PI.UiBuilder.OpenMainUi -= Open;
        PI.UiBuilder.OpenConfigUi -= Open;
        Commands.RemoveHandler("/cti");
    }
}
