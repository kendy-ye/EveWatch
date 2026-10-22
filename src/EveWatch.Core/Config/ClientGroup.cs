namespace EveWatch.Core.Config;

/// <summary>客户端分组：组内账号按列表顺序循环切换。</summary>
public sealed class ClientGroup
{
    /// <summary>稳定 id，重命名不影响快捷键绑定。</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>显示名称。</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>组内账号名，顺序即循环顺序。</summary>
    public List<string> Accounts { get; set; } = [];
}
