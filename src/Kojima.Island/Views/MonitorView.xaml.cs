using System.Windows.Controls;
using Kojima.Core.Models;

namespace Kojima.Island.Views;

/// <summary>监控页：把采样数据交给自绘画布。</summary>
public partial class MonitorView : UserControl
{
    public MonitorView()
    {
        InitializeComponent();
    }

    public void Update(MonitorSample sample) => Surface.Update(sample);

    public void SetSort(string sort) => Surface.SetSort(sort);
}
