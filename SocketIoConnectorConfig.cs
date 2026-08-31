using System;

namespace Resto.Front.Api.HorecaControlPlugin;

public class SocketIoConnectorConfig
{
    public string Login { get; set; }
    public string Password { get; set; }
    public Guid PluginId { get; set; }
    public string PluginName { get; set; }
    public Guid GroupId { get; set; }
    public string GroupName { get; set; }
    public Guid DepartmentId { get; set; }
    public string DepartmentName { get; set; }
    public string CurrencyCode { get; set; }
    public string ServerUrl { get; set; }
    public string Version { get; set; }
}