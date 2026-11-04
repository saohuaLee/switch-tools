using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using NetOps.Domain;
using NetOps.Infrastructure;

namespace NetOps.Desktop;

public sealed class TopologyCanvas : FrameworkElement
{
    private MainViewModel? vm;
    private TopologyNodeView? dragging;
    private Point down, origin;
    private bool moved;
    private readonly List<(Rect Bounds, TopologyNodeView Node)> hitNodes = [];
    private readonly List<(Point A, Point B, TopologyLink Link)> hitEdges = [];
    public TopologyCanvas()
    {
        Width = 1800; Height = 1200; Focusable = true;
        DataContextChanged += (_, _) => { if (vm is not null) vm.TopologyChanged -= Redraw; vm = DataContext as MainViewModel; if (vm is not null) vm.TopologyChanged += Redraw; Redraw(); };
        Unloaded += (_, _) => { if (vm is not null) vm.TopologyChanged -= Redraw; };
        Loaded += (_, _) => { if (vm is not null) { vm.TopologyChanged -= Redraw; vm.TopologyChanged += Redraw; } };
    }
    private void Redraw() => Dispatcher.Invoke(() => { Width = Math.Max(1000, (vm?.TopologyNodes.Max(n => (double?)n.X) ?? 0) + 330); Height = Math.Max(750, (vm?.TopologyNodes.Max(n => (double?)n.Y) ?? 0) + 250); if (dragging is not null && vm?.TopologyNodes.FirstOrDefault(n => n.Id == dragging.Id) is { } fresh) { fresh.X = dragging.X; fresh.Y = dragging.Y; dragging = fresh; } InvalidateVisual(); });
    private static Brush Brush(string color) => (Brush)new BrushConverter().ConvertFromString(color)!;
    protected override void OnRender(DrawingContext dc)
    {
        dc.DrawRectangle(Brush("#F8FAFD"), null, new Rect(0, 0, ActualWidth, ActualHeight));
        for (var x = 12; x < ActualWidth; x += 24) for (var y = 12; y < ActualHeight; y += 24) dc.DrawEllipse(Brush("#DFE6F0"), null, new(x, y), 1, 1);
        hitNodes.Clear(); hitEdges.Clear(); if (vm is null) return;
        void Text(string value, Point p, string color, double size = 13, double maxWidth = 232)
        { var text = new FormattedText(value, CultureInfo.GetCultureInfo("zh-CN"), FlowDirection.LeftToRight, new Typeface("Microsoft YaHei UI"), size, Brush(color), VisualTreeHelper.GetDpi(this).PixelsPerDip) { MaxTextWidth = maxWidth, MaxLineCount = 1, Trimming = TextTrimming.CharacterEllipsis }; dc.DrawText(text, p); }
        foreach (var member in vm.TopologyNodes.Where(n => n.OwnerId is not null))
        {
            var parent = vm.TopologyNodes.FirstOrDefault(n => n.Id == member.OwnerId); if (parent is null) continue;
            dc.DrawLine(new Pen(Brush("#CAD5E4"), 1.5) { DashStyle = DashStyles.Dot }, new(parent.X + 135, parent.Y + 124), new(member.X + 135, member.Y));
            Text("成员关系", new(member.X + 145, member.Y - 30), "#94A3B8", 10);
        }
        foreach (var link in vm.TopologyEdges)
        {
            var a = vm.EndpointNode(link.LocalDeviceId, link.LocalPort); var b = link.RemoteDeviceId is Guid id ? vm.EndpointNode(id, link.RemotePort) : vm.TopologyNodes.FirstOrDefault(n => !n.IsKnown && n.Subtitle.Contains(link.RemoteIdentity));
            if (a is null || b is null) continue;
            var p1 = new Point(a.X + 135, a.Y + 124); var p2 = new Point(b.X + 135, b.Y); var stale = !link.Manual && DateTimeOffset.UtcNow - link.ObservedAt > TimeSpan.FromSeconds(link.TtlSeconds);
            var pen = new Pen(Brush(stale ? "#B6C0CF" : link.Manual ? "#94A3B8" : "#6B9CF1"), 2) { DashStyle = stale || link.Manual ? DashStyles.Dash : DashStyles.Solid };
            dc.DrawLine(pen, p1, p2); hitEdges.Add((p1, p2, link));
            var mid = new Point((p1.X + p2.X) / 2, (p1.Y + p2.Y) / 2); dc.DrawRoundedRectangle(Brush("White"), new Pen(Brush("#E3E8EF"), 1), new Rect(mid.X - 80, mid.Y - 13, 160, 26), 5, 5); Text(stale ? "邻居记录已过期" : link.Manual ? "手工连接 · 未校验" : link.Source, new(mid.X - 70, mid.Y - 8), "#64748B", 10, 145);
        }
        foreach (var node in vm.TopologyNodes)
        {
            var rect = new Rect(node.X, node.Y, 270, 124); hitNodes.Add((rect, node)); var selected = vm.SelectedTopologyNode?.Id == node.Id;
            dc.DrawRoundedRectangle(Brush("#EDF1F7"), null, new Rect(node.X, node.Y + 4, 270, 124), 12, 12);
            dc.DrawRoundedRectangle(Brush("White"), new Pen(Brush(selected ? "#3977E8" : "#DDE5EF"), selected ? 2 : 1), rect, 12, 12);
            dc.DrawRoundedRectangle(Brush(node.IsKnown ? "#EAF2FF" : "#F1F5F9"), null, new Rect(node.X + 16, node.Y + 16, 32, 32), 7, 7); Text(node.Members.Length > 1 ? "堆" : node.IsKnown ? "网" : "?", new(node.X + 23, node.Y + 23), "#3977E8", 13);
            Text(node.Name, new(node.X + 60, node.Y + 16), "#172B4D", 13, 192); Text(node.Subtitle, new(node.X + 60, node.Y + 36), "#94A3B8", 10, 192);
            var status = node.Health?.StatusLabel ?? "未纳管"; var color = node.Health?.StatusColor ?? "#94A3B8";
            dc.DrawEllipse(Brush(color), null, new(node.X + 22, node.Y + 72), 3, 3); Text(status + (node.Members.Length > 1 ? $" · {node.Members.Length} 个成员" : ""), new(node.X + 32, node.Y + 64), color, 11);
            Text($"CPU {node.Health?.Cpu ?? "—"}     内存 {node.Health?.Memory ?? "—"}", new(node.X + 16, node.Y + 94), "#64748B", 11);
        }
    }
    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e); if (vm is null) return; down = e.GetPosition(this); var hit = hitNodes.LastOrDefault(n => n.Bounds.Contains(down));
        if (hit.Node is not null) { dragging = hit.Node; origin = new(dragging.X, dragging.Y); moved = false; vm.SelectedTopologyNode = dragging; vm.SelectedTopologyLink = null; CaptureMouse(); if (e.ClickCount == 2 && dragging.Health is not null) vm.OpenDevice.Execute(dragging.Health.Device.Id); InvalidateVisual(); }
        else foreach (var edge in hitEdges) if (Distance(down, edge.A, edge.B) < 12) { vm.SelectedTopologyNode = null; vm.SelectedTopologyLink = edge.Link; break; }
    }
    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e); var p = e.GetPosition(this);
        if (dragging is not null && e.LeftButton == MouseButtonState.Pressed) { moved |= (p - down).Length > 3; dragging.X = Math.Clamp(origin.X + p.X - down.X, 20, 1500); dragging.Y = Math.Clamp(origin.Y + p.Y - down.Y, 20, 1000); InvalidateVisual(); }
        else { var hit = hitNodes.LastOrDefault(n => n.Bounds.Contains(p)); Cursor = hit.Node is not null ? Cursors.Hand : Cursors.Arrow; ToolTip = hit.Node is not null ? hit.Node.Name + "\n单击查看详情，双击进入设备，拖动保存布局。" : "拖动设备调整位置；链路表示已采集的邻居关系，状态以采集时间为准。"; }
    }
    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    { if (dragging is not null && moved) vm?.MoveNode(dragging, dragging.X, dragging.Y); dragging = null; ReleaseMouseCapture(); base.OnMouseLeftButtonUp(e); }
    private static double Distance(Point p, Point a, Point b) { var ab = b - a; var t = ab.LengthSquared == 0 ? 0 : Math.Clamp(Vector.Multiply(p - a, ab) / ab.LengthSquared, 0, 1); return (p - (a + ab * t)).Length; }
}
