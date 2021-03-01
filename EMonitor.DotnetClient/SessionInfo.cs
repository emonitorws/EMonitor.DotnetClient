namespace EMonitor.DotnetClient
{
    /// <summary>
    /// 每个程序启动一次代表为一个Session(共享同样的ServerName,Environment)
    /// </summary>
    public class SessionInfo
    {
        public string ResourceId { get; set; }
        public string ResourceSecret { get; set; }
        public string ServerName { get; set; }
        public string AppName { get; set; }
        public string RegionCode { get; set; }
        public string Environment { get; set; }
    }
}
