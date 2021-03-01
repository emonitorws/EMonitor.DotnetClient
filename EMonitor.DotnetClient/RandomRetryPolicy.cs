using Microsoft.AspNetCore.SignalR.Client;
using System;
using System.Security.Cryptography;

namespace EMonitor.DotnetClient
{
    public class RandomRetryPolicy : IRetryPolicy
    {
        public TimeSpan? NextRetryDelay(RetryContext retryContext)
        {
            return TimeSpan.FromSeconds(RandomNumberGenerator.GetInt32(1, 10));
        }
    }
}
