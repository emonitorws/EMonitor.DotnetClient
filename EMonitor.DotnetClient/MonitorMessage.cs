using System.Collections.Generic;
using System.Runtime.Serialization;

namespace EMonitor.DotnetClient
{
    public class MonitorMessage
    {
        [DataMember(Name = "ts")]
        public long ts { get; set; }
        [DataMember(Name = "data")]
        public List<MonitorInfo> data { get; set; }
    }
}
