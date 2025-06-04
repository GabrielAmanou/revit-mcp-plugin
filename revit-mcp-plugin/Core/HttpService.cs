using System;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;
using Autodesk.Revit.UI;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RevitMCPSDK.API.Models.JsonRPC;
using RevitMCPSDK.API.Interfaces;
using revit_mcp_plugin.Configuration;
using revit_mcp_plugin.Utils;

namespace revit_mcp_plugin.Core
{
    public class HttpService
    {
        private static HttpService _instance;
        private HttpListener _listener;
        private Thread _listenerThread;
        private bool _isRunning;
        private string _url = "http://localhost:8000/mcp/";
        private UIApplication _uiApp;
        private ICommandRegistry _commandRegistry;
        private ILogger _logger;
        private CommandExecutor _commandExecutor;

        public static HttpService Instance
        {
            get
            {
                if (_instance == null)
                    _instance = new HttpService();
                return _instance;
            }
        }

        private HttpService()
        {
            _commandRegistry = new RevitCommandRegistry();
            _logger = new Logger();
        }

        public bool IsRunning => _isRunning;

        public string Url
        {
            get => _url;
            set => _url = value.EndsWith("/") ? value : value + "/";
        }

        // 初始化
        public void Initialize(UIApplication uiApp)
        {
            _uiApp = uiApp;

            // 初始化事件管理器
            ExternalEventManager.Instance.Initialize(uiApp, _logger);

            // 记录当前 Revit 版本
            var versionAdapter = new RevitMCPSDK.API.Utils.RevitVersionAdapter(_uiApp.Application);
            string currentVersion = versionAdapter.GetRevitVersion();
            _logger.Info("当前 Revit 版本: {0}", currentVersion);

            // 创建命令执行器
            _commandExecutor = new CommandExecutor(_commandRegistry, _logger);

            // 加载配置并注册命令
            ConfigurationManager configManager = new ConfigurationManager(_logger);
            configManager.LoadConfiguration();

            CommandManager commandManager = new CommandManager(
                _commandRegistry, _logger, configManager, _uiApp);
            commandManager.LoadCommands();

            _logger.Info($"HTTP service initialized on {_url}");
        }

        public void Start()
        {
            if (_isRunning) return;

            try
            {
                _listener = new HttpListener();
                _listener.Prefixes.Add(_url);
                _listener.Start();

                _isRunning = true;
                _listenerThread = new Thread(Listen)
                {
                    IsBackground = true
                };
                _listenerThread.Start();
            }
            catch (Exception ex)
            {
                _logger.Error("Failed to start HTTP service: {0}", ex.Message);
                _isRunning = false;
            }
        }

        public void Stop()
        {
            if (!_isRunning) return;

            try
            {
                _isRunning = false;
                _listener?.Stop();
                _listener = null;

                if (_listenerThread != null && _listenerThread.IsAlive)
                {
                    _listenerThread.Join(1000);
                }
            }
            catch (Exception ex)
            {
                _logger.Error("Failed to stop HTTP service: {0}", ex.Message);
            }
        }

        private void Listen()
        {
            try
            {
                while (_isRunning && _listener != null)
                {
                    var context = _listener.GetContext();
                    ThreadPool.QueueUserWorkItem(o => HandleRequest(context));
                }
            }
            catch (HttpListenerException)
            {
            }
            catch (Exception ex)
            {
                _logger.Error("HTTP service error: {0}", ex.Message);
            }
        }

        private void HandleRequest(HttpListenerContext context)
        {
            try
            {
                if (context.Request.HttpMethod != "POST")
                {
                    context.Response.StatusCode = 405;
                    return;
                }

                string message;
                using (var reader = new StreamReader(context.Request.InputStream, context.Request.ContentEncoding))
                {
                    message = reader.ReadToEnd();
                }

                string response = ProcessJsonRPCRequest(message);
                byte[] responseData = Encoding.UTF8.GetBytes(response);
                context.Response.ContentType = "application/json";
                context.Response.ContentEncoding = Encoding.UTF8;
                context.Response.ContentLength64 = responseData.Length;
                context.Response.OutputStream.Write(responseData, 0, responseData.Length);
            }
            catch (Exception ex)
            {
                _logger.Error("Error handling HTTP request: {0}", ex.Message);
            }
            finally
            {
                context.Response.OutputStream.Close();
            }
        }

        private string ProcessJsonRPCRequest(string requestJson)
        {
            JsonRPCRequest request;
            try
            {
                request = JsonConvert.DeserializeObject<JsonRPCRequest>(requestJson);
                if (request == null || !request.IsValid())
                {
                    return CreateErrorResponse(
                        null,
                        JsonRPCErrorCodes.InvalidRequest,
                        "Invalid JSON-RPC request"
                    );
                }

                if (!_commandRegistry.TryGetCommand(request.Method, out var command))
                {
                    return CreateErrorResponse(request.Id, JsonRPCErrorCodes.MethodNotFound,
                        $"Method '{request.Method}' not found");
                }

                try
                {
                    object result = command.Execute(request.GetParamsObject(), request.Id);
                    return CreateSuccessResponse(request.Id, result);
                }
                catch (Exception ex)
                {
                    return CreateErrorResponse(request.Id, JsonRPCErrorCodes.InternalError, ex.Message);
                }
            }
            catch (JsonException)
            {
                return CreateErrorResponse(
                    null,
                    JsonRPCErrorCodes.ParseError,
                    "Invalid JSON"
                );
            }
            catch (Exception ex)
            {
                return CreateErrorResponse(
                    null,
                    JsonRPCErrorCodes.InternalError,
                    $"Internal error: {ex.Message}"
                );
            }
        }

        private string CreateSuccessResponse(string id, object result)
        {
            var response = new JsonRPCSuccessResponse
            {
                Id = id,
                Result = result is JToken jToken ? jToken : JToken.FromObject(result)
            };
            return response.ToJson();
        }

        private string CreateErrorResponse(string id, int code, string message, object data = null)
        {
            var response = new JsonRPCErrorResponse
            {
                Id = id,
                Error = new JsonRPCError
                {
                    Code = code,
                    Message = message,
                    Data = data != null ? JToken.FromObject(data) : null
                }
            };
            return response.ToJson();
        }
    }
}

