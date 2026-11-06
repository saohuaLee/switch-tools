using NetOps.Domain;

namespace NetOps.Desktop;

public sealed partial class MainViewModel
{
    private int? networkSelection;
    private int networkMode;
    private string networkNumber = "", networkName = "", networkError = "";
    public VlanState? WorkVlan { get => Vlans.FirstOrDefault(v => v.Id == networkSelection); set { if (Busy || ReviewOpen || value?.Id == networkSelection) return; networkSelection = value?.Id; networkMode = 0; networkError = ""; Changed(); NotifyNetworkWorkspace(); } }
    public string NetworkHeading => networkMode == 1 ? "创建一个网络" : WorkVlan is { } v ? $"网络 {v.Id} · {v.Name}" : "选择网络，或点击创建";
    public bool NetworkCreating => networkMode == 1;
    public bool NetworkNaming => networkMode is 1 or 2;
    public bool NetworkDeleting => networkMode == 3;
    public bool NetworkHasSelection => WorkVlan is not null;
    public bool NetworkEditing => networkMode > 0;
    public bool NetworkCanEdit => CanConfigure && WorkCanSelect;
    public string NetworkNumber { get => networkNumber; set => Set(ref networkNumber, value); }
    public string NetworkName { get => networkName; set => Set(ref networkName, value); }
    public string NetworkError => networkError;
    public string NetworkUsage => WorkVlan is { } v ? $"当前有 {Ports.Count(p => p.State.Pvid == v.Id || p.State.TaggedVlans.Contains(v.Id))} 个端口使用这个网络。" : "网络使用 VLAN 编号区分；创建 VLAN 不会自动配置终端 IP、网关或 DHCP。";
    public ActionCommand CreateWorkNetwork { get; private set; } = null!;
    public ActionCommand RenameWorkNetwork { get; private set; } = null!;
    public ActionCommand DeleteWorkNetwork { get; private set; } = null!;
    public ActionCommand ReviewWorkNetwork { get; private set; } = null!;
    private void InitializeNetworkWorkspace()
    {
        CreateWorkNetwork = new(_ => { networkMode = 1; NetworkNumber = ""; NetworkName = ""; networkError = ""; NotifyNetworkWorkspace(); }, () => NetworkCanEdit);
        RenameWorkNetwork = new(_ => { networkMode = 2; NetworkName = WorkVlan!.Name; networkError = ""; NotifyNetworkWorkspace(); }, () => NetworkCanEdit && NetworkHasSelection);
        DeleteWorkNetwork = new(_ => { networkMode = 3; networkError = ""; NotifyNetworkWorkspace(); }, () => NetworkCanEdit && NetworkHasSelection);
        ReviewWorkNetwork = new(_ => PreviewNetworkForm(), () => NetworkCanEdit && NetworkEditing);
    }
    private void PreviewNetworkForm()
    {
        try
        {
            var request = networkMode switch
            {
                1 => new ChangeRequest(CreateVlanId: ParseInt(NetworkNumber, "网络编号"), VlanName: string.IsNullOrWhiteSpace(NetworkName) ? $"VLAN{ParseInt(NetworkNumber, "网络编号")}" : NetworkName.Trim()),
                2 when WorkVlan is not null => new(RenameVlanId: WorkVlan.Id, VlanName: NetworkName.Trim()),
                3 when WorkVlan is not null => new(DeleteVlanId: WorkVlan.Id),
                _ => throw new ArgumentException("先选择要创建、改名或删除的网络。")
            };
            plan = engine.Preview(RequireDevice(), request); WorkReviewSummary = networkMode switch { 1 => $"创建网络 {request.CreateVlanId}。", 2 => $"将网络 {request.RenameVlanId} 改名为 {request.VlanName}。", _ => $"删除网络 {request.DeleteVlanId}。" };
            WorkReviewRows = plan.Differences.Select(d => new EasyReviewRow(d.Target.Replace("VLAN", "网络"), d.Before, d.After)).ToArray();
            workReview = true; workReviewIsPort = false; reviewedDraftIds.Clear(); reviewIncludesCurrent = true; LastResult = ""; networkError = ""; saveAfterVerify = true;
            PlanConfirmed = false; LiveRiskAccepted = false; NotifyPlan(); ReviewOpen = true; NotifyPortWorkspace();
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or NotSupportedException) { networkError = ex.Message; NotifyNetworkWorkspace(); }
    }
    private void NotifyNetworkWorkspace()
    {
        foreach (var name in new[] { nameof(WorkVlan), nameof(NetworkHeading), nameof(NetworkCreating), nameof(NetworkNaming), nameof(NetworkDeleting), nameof(NetworkHasSelection), nameof(NetworkEditing), nameof(NetworkCanEdit), nameof(NetworkError), nameof(NetworkUsage) }) Changed(name);
        foreach (var command in new[] { CreateWorkNetwork, RenameWorkNetwork, DeleteWorkNetwork, ReviewWorkNetwork }) command?.Refresh();
    }
}
