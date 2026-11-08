using System.ComponentModel;
using System.Windows.Input;

namespace Atlas.App;

/// <summary>
/// 周期刷新与用户交互之间的协调器。
///
/// 背景：DataGrid 每轮轮询都要更新转速 / PWM，这本身没问题；但「隐藏 0 RPM」用的
/// <see cref="ICollectionView.Refresh"/> 会重建整个表格的行容器——如果每秒执行一次，
/// 用户正在展开的模式 / 温度源下拉列表会被立刻收起，正在拖动的滑块、正在编辑的单元格也会被打断。
///
/// 处理策略：
/// 1) 只有可见行集合真的变化时才请求刷新（由调用方判断）；
/// 2) 下拉列表展开、单元格编辑、鼠标捕获（拖拽）期间只登记「待刷新」，
///    等交互结束后调用 <see cref="FlushPending"/> 再一次性重建视图。
/// </summary>
internal sealed class ViewRefreshGate
{
    private readonly ICollectionView _view;
    private readonly Func<bool> _isDropDownOpen;
    private bool _editing;
    private bool _pending;

    /// <param name="view">需要重建的表格视图。</param>
    /// <param name="isDropDownOpen">返回当前是否有下拉列表处于展开状态（由调用方扫描表格得到）。</param>
    public ViewRefreshGate(ICollectionView view, Func<bool> isDropDownOpen)
    {
        _view = view;
        _isDropDownOpen = isDropDownOpen;
    }

    /// <summary>用户此刻是否正在与表格交互（下拉展开 / 单元格编辑 / 拖拽中）。</summary>
    public bool IsUserInteracting =>
        _isDropDownOpen() || _editing || Mouse.Captured is not null;

    public bool HasPendingRefresh => _pending;

    /// <summary>请求重建视图；用户正在交互时先登记，等交互结束后再执行。</summary>
    public void RequestRefresh()
    {
        if (IsUserInteracting || !TryRefresh())
        {
            _pending = true;
            return;
        }

        _pending = false;
    }

    /// <summary>交互结束后调用：若仍有待刷新且用户已空闲，则立即执行。</summary>
    public void FlushPending()
    {
        if (!_pending || IsUserInteracting) return;

        _pending = false;
        if (!TryRefresh()) _pending = true;
    }

    public void NotifyEditStarted() => _editing = true;

    public void NotifyEditEnded() => _editing = false;

    private bool TryRefresh()
    {
        try
        {
            _view.Refresh();
            return true;
        }
        catch (InvalidOperationException)
        {
            // DataGrid 正在编辑单元格时不允许 Refresh；登记为待刷新，等编辑结束再执行
            return false;
        }
    }
}
