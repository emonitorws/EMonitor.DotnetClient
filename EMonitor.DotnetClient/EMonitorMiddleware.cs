using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace EMonitor.DotnetClient
{
    public class EMonitorMiddleware
    {
        //private const string EMWS_HOST = "https://localhost:5003";
        private string EMWS_HOST = "https://pushgw.emonitor.ws";
        private readonly RequestDelegate _next;
        private readonly ILogger _logger;
        private readonly IHostApplicationLifetime _hostApplicationLifetime;
        private readonly IHostEnvironment _hostEnvironment;
        private HubConnection _connection;
        private const int REQUEST_BUFFER_SIZE = 1000;
        private readonly HttpClientHandler _httpClientHandler;

        private ConcurrentQueue<MonitorInfo> _perSecondStore = new ConcurrentQueue<MonitorInfo>();
        private ConcurrentQueue<MonitorMessage> _messageStore = new ConcurrentQueue<MonitorMessage>();

        public EMonitorMiddleware(RequestDelegate next, ILoggerFactory loggerFactory, IHostApplicationLifetime hostApplicationLifetime, IHostEnvironment hostEnvironment)
        {
            if (loggerFactory is null)
            {
                throw new ArgumentNullException(nameof(loggerFactory));
            }

            _next = next ?? throw new ArgumentNullException(nameof(next));
            var envHost = Environment.GetEnvironmentVariable("EMWS_HOST");
            if (!string.IsNullOrWhiteSpace(envHost))
            {
                EMWS_HOST = envHost;
            }

            _logger = loggerFactory.CreateLogger("EMWS");
            _hostApplicationLifetime = hostApplicationLifetime ?? throw new ArgumentNullException(nameof(hostApplicationLifetime));
            _hostEnvironment = hostEnvironment ?? throw new ArgumentNullException(nameof(hostEnvironment));
            _httpClientHandler = new HttpClientHandler();
            Task.Factory.StartNew(async () => await CreateRemoteConnection(_hostApplicationLifetime.ApplicationStopping), hostApplicationLifetime.ApplicationStopping, TaskCreationOptions.LongRunning, TaskScheduler.Default);
            Task.Factory.StartNew(async () => await StaticitsSecondDataTask(_hostApplicationLifetime.ApplicationStopping), hostApplicationLifetime.ApplicationStopping, TaskCreationOptions.LongRunning, TaskScheduler.Default);
            Task.Factory.StartNew(async () => await StaticitsBufferClearTask(_hostApplicationLifetime.ApplicationStopping), hostApplicationLifetime.ApplicationStopping, TaskCreationOptions.LongRunning, TaskScheduler.Default);
            Task.Factory.StartNew(async () => await PushData(_hostApplicationLifetime.ApplicationStopping), hostApplicationLifetime.ApplicationStopping, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        }

        private async Task PushData(CancellationToken applicationStopping)
        {
            _logger.LogInformation("Push Data Task Started");
            var staticitsPeriod = TimeSpan.FromSeconds(1);
            while (!applicationStopping.IsCancellationRequested)
            {
                if (DateTime.Now.Second % 5 == 0)
                {
                    _logger.LogDebug(_connection == null ? "remote server not connected" : "Try PushData");
                    try
                    {
                        while (_connection.State == HubConnectionState.Connected && _messageStore.TryDequeue(out var message))
                        {
                            var messageJson = JsonConvert.SerializeObject(message);
                            var transferType = messageJson.Length >= 800 ? TransferType.GZip : TransferType.Plain;
                            //_logger.LogInformation($"SendMessage: {messageBody}");                        
                            await _connection.InvokeAsync("PushMessage", transferType, transferType == TransferType.GZip ? GzipCompressUtil.Compress(messageJson) : messageJson, applicationStopping);
                        }
                    }
                    catch (Exception)
                    {
                        _logger.LogError("PushData to Remote Error");
                    }
                }

                Thread.Sleep(staticitsPeriod);
            }
            await Task.CompletedTask;
            _logger.LogInformation("Push Data Task End");
        }

        #region Build Websocket Connection
        private async Task CreateRemoteConnection(CancellationToken applicationStopping)
        {
            _connection = new HubConnectionBuilder()
                  .WithUrl($"{EMWS_HOST}/signalr/resource_push_hub", options =>
                  {
                      options.SkipNegotiation = true;
                      options.Transports = Microsoft.AspNetCore.Http.Connections.HttpTransportType.WebSockets;
                      options.DefaultTransferFormat = Microsoft.AspNetCore.Connections.TransferFormat.Binary;
                      options.CloseTimeout = TimeSpan.FromSeconds(5);
                      options.AccessTokenProvider = async () =>
                      {
                          return await Login(EMWS_HOST);
                      };
                  })
                  .WithAutomaticReconnect(new RandomRetryPolicy())
                  .Build();

            //_connection.StartAsync()
            #region snippet_ClosedRestart
            _connection.Closed += async (error) =>
            {
                _logger.LogInformation("Remote Connection Disconnected, Retry Connecting...");
                await Task.Delay(new Random().Next(0, 5) * 1000);
                await _connection.StartAsync();
            };
            #endregion

            _connection.Reconnecting += error =>
            {
                _logger.LogWarning($"Reconnecting: {error.Message}");
                //Debug.Assert(connection.State == HubConnectionState.Reconnecting);

                // Notify users the connection was lost and the client is reconnecting.
                // Start queuing or dropping messages.

                return Task.CompletedTask;
            };
            _connection.Reconnected += msg =>
            {
                _logger.LogWarning($"Reconnected: {msg}");
                //Debug.Assert(connection.State == HubConnectionState.Reconnecting);

                // Notify users the connection was lost and the client is reconnecting.
                // Start queuing or dropping messages.

                return Task.CompletedTask;
            };
            #region snippet_ConnectionOn
            _connection.On<string, string>("ReceiveMessage", (user, message) =>
            {
                _logger.LogInformation($"ReceiveMessage: {user}: {message}");
            });
            #endregion

            await ConnectWithRetryAsync(_connection, applicationStopping);

            //await connection.InvokeAsync("SendMessage",JsonConvert.SerializeObject());
            await Task.CompletedTask;
            //await ConnectAsync("https://pushgw.emonitor.ws/signalr/resource_push_hub", applicationStopping);
        }

        private async Task<string> Login(string host)
        {
            SessionInfo info = new SessionInfo()
            {
                AppName = Assembly.GetEntryAssembly().GetName().Name,
                Environment = _hostEnvironment.EnvironmentName,
                ServerName = Environment.MachineName,
                ResourceId = Environment.GetEnvironmentVariable("EMWS_CLIENT_ID"),
                ResourceSecret = Environment.GetEnvironmentVariable("EMWS_SECRET"),
                RegionCode = Environment.GetEnvironmentVariable("EMWS_REGION"),
            };

            if (info.ResourceId.IsNullOrWhitespace())
            {
                throw new ArgumentNullException("EMWS_CLIENT_ID");
            }
            if (info.ResourceSecret.IsNullOrWhitespace())
            {
                throw new ArgumentNullException("EMWS_SECRET");
            }

            var proxyUrl = string.Empty;
            if (!string.IsNullOrWhiteSpace(proxyUrl))
            {
                _httpClientHandler.UseProxy = true;
                _httpClientHandler.Proxy = new WebProxy(proxyUrl);
            }

            var httpClient = new HttpClient(_httpClientHandler);

            httpClient.DefaultRequestHeaders.Add("auth-clientid", info.ResourceId);
            httpClient.DefaultRequestHeaders.Add("auth-secret", info.ResourceSecret);
            var loginRsp = await httpClient.PostAsync($"{host}/login", new StringContent(JsonConvert.SerializeObject(info), encoding: Encoding.UTF8));
            var token = await loginRsp.Content.ReadAsStringAsync();
            loginRsp.EnsureSuccessStatusCode();
            return token;
        }

        private async Task<bool> ConnectWithRetryAsync(HubConnection connection, CancellationToken token)
        {
            // Keep trying to until we can start or the token is canceled.
            while (true)
            {
                try
                {
                    await connection.StartAsync(token);
                    Debug.Assert(connection.State == HubConnectionState.Connected);
                    _logger.LogInformation("Remote Connected");
                    return true;
                }
                catch when (token.IsCancellationRequested)
                {
                    return false;
                }
                catch (Exception ex)
                {
                    _logger.LogError($"host {EMWS_HOST} connect error, {ex.Message}");
                    // Failed to connect, trying again in 5000 ms.
                    Debug.Assert(connection.State == HubConnectionState.Disconnected);
                    await Task.Delay(5000);
                }
            }
        }
        #endregion

        /// <summary>
        /// 统计每秒的数据
        /// </summary>
        private async Task StaticitsSecondDataTask(CancellationToken applicationStopping)
        {
            _logger.LogInformation("StaticitsSecondData Task Started");
            while (!applicationStopping.IsCancellationRequested)
            {
                var lastPeriodData = Interlocked.Exchange(ref _perSecondStore, new ConcurrentQueue<MonitorInfo>());
                _messageStore.Enqueue(new MonitorMessage() { ts = DateTime.Now.GetEpochMilliseconds(), data = lastPeriodData.ToList() });
                Thread.Sleep(1000);
            }
            await Task.CompletedTask;
            _logger.LogInformation("StaticitsSecondData Task End");
        }

        private async Task StaticitsBufferClearTask(CancellationToken applicationStopping)
        {
            _logger.LogInformation("StaticitsBufferClearTask Started");
            var staticitsPeriod = TimeSpan.FromSeconds(5);
            while (!applicationStopping.IsCancellationRequested)
            {
                Thread.Sleep(staticitsPeriod);
                if (_messageStore.Count > REQUEST_BUFFER_SIZE)
                {
                    for (int i = 0; i < _messageStore.Count - REQUEST_BUFFER_SIZE; i++)
                    {
                        _messageStore.TryDequeue(out var _);
                    }
                }
            }
            await Task.CompletedTask;
            _logger.LogInformation("StaticitsBufferClearTask Task End");
        }

        public async Task InvokeAsync(HttpContext context)
        {
            var durationSw = Stopwatch.StartNew();
            var request = context.Request;
            MonitorInfo monitorInfo = new MonitorInfo()
            {
                Host = request.Host.Value,
                Path = request.Path.Value,
                QueryString = request.QueryString.Value,
                Lang = request.Headers.GetHeaderValueAs<string>("Accept-Language"),
                Referer = request.Headers.GetHeaderValueAs<string>("Referer"),
                UserAgent = request.Headers.GetHeaderValueAs<string>("User-Agent"),
                Ip = context.GetClientIp()
            };
            await _next(context);

            monitorInfo.StatusCode = context.Response.StatusCode;
            monitorInfo.Duration = durationSw.ElapsedMilliseconds;
            durationSw.Stop();
            _perSecondStore.Enqueue(monitorInfo);
        }
    }
}
