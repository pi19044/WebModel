using Microsoft.ClearScript;
using Microsoft.ClearScript.V8;
using System;
using System.IO;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Twilight.WebModel
{
    class Program
    {
        private static V8ScriptEngine engine = new();
        private static WebSocket webSocket;

        static async Task Main(string[] args)
        {
            Console.WriteLine("Twilight WebModel [Version 1.0.0.0]");
            Console.WriteLine("Copyright (c) Twilight Incorporated. All rights reserved.");
            Console.WriteLine("");

            HttpListener listener = new HttpListener();
            listener.Prefixes.Add("http://localhost:8800/");
            listener.Start();
            Console.WriteLine("[*] Listening on port 8800");

            // Initialize V8 engine and host functions
            var hostFunctions = new HostFunctions(webSocket);
            engine.AddHostObject("host", hostFunctions);

            // Define custom console methods
            engine.Execute(@"
                console = {
                    log: function(value) { host.log(value); },
                    warn: function(value) { host.warn(value); },
                    error: function(value) { host.error(value); }
                };
            ");

            // Handle incoming connections
            while (true)
            {
                HttpListenerContext context = await listener.GetContextAsync();

                if (context.Request.IsWebSocketRequest)
                {
                    await HandleWebSocketConnection(context);
                }
                else
                {
                    await HandleHttpRequest(context);
                }
            }

            listener.Close();
        }

        private static async Task HandleHttpRequest(HttpListenerContext context)
        {
            HttpListenerRequest request = context.Request;
            HttpListenerResponse response = context.Response;

            Console.WriteLine($"[*] Received request for {request.Url}");

            // Map the request URL to a file path
            string localPath = GetLocalPath(request.Url.LocalPath);

            // If localPath points to a directory, default to "index.html"
            if (Directory.Exists(localPath))
            {
                localPath = Path.Combine(localPath, "index.html");
            }

            // Ensure the path is within the base directory
            string filePath = Path.GetFullPath(Path.Combine(localPath));

            Console.WriteLine($"[*] Path for request: \"{filePath}\"");

            try
            {
                if (File.Exists(filePath))
                {
                    // Serve the file content
                    byte[] fileContent = await File.ReadAllBytesAsync(filePath);
                    response.ContentType = GetContentType(filePath);
                    response.ContentLength64 = fileContent.Length;

                    using (var outputStream = response.OutputStream)
                    {
                        await outputStream.WriteAsync(fileContent, 0, fileContent.Length);
                        // Ensure output stream is closed after write
                        outputStream.Close();
                    }
                }
                else
                {
                    // File not found, return 404
                    response.StatusCode = 404;
                    byte[] buffer = Encoding.UTF8.GetBytes("<html><body><h1>404 Not Found</h1><p>The requested item is not found.<hr><em>Twilight WebModel</em></body></html>");
                    response.ContentLength64 = buffer.Length;

                    using (var outputStream = response.OutputStream)
                    {
                        await outputStream.WriteAsync(buffer, 0, buffer.Length);
                        // Ensure output stream is closed after write
                        outputStream.Close();
                    }
                }
            }
            catch (HttpListenerException ex) when (ex.ErrorCode == 995) // ERROR_OPERATION_ABORTED
            {
                // Handle the case where the request was aborted
                Console.WriteLine("Client connection was aborted.");
            }
            catch (Exception ex)
            {
                // Handle unexpected exceptions
                Console.WriteLine($"Error handling request: {ex.Message}");
                // Avoid sending a 500 status code and error message if the website is closed
                // You can log the error or handle it differently here
            }
            finally
            {
                // Ensure the response output stream is closed
                response.OutputStream.Close();
            }
        }

        private static async Task HandleWebSocketConnection(HttpListenerContext context)
        {
            var wsContext = await context.AcceptWebSocketAsync(null);
            webSocket = wsContext.WebSocket;

            // Initialize V8 script engine with the WebSocket
            var hostFunctions = new HostFunctions(webSocket);
            engine.AddHostObject("host", hostFunctions);

            // Define custom console methods
            engine.Execute(@"
                console = {
                    log: function(value) { host.Log(value); },
                    warn: function(value) { host.Warn(value); },
                    error: function(value) { host.Error(value); }
                };
            ");

            var buffer = new byte[1024];
            var inputBuilder = new StringBuilder();
            bool isProcessingInput = true;

            await SendMessage("V8 Terminal\r\nPowered by Microsoft ClearScript\r\nv8> ");

            while (webSocket.State == WebSocketState.Open)
            {
                var segment = new ArraySegment<byte>(buffer);
                var result = await webSocket.ReceiveAsync(segment, CancellationToken.None);

                if (result.MessageType == WebSocketMessageType.Text)
                {
                    string input = Encoding.UTF8.GetString(buffer, 0, result.Count);
                    await SendMessage(input);

                    if (input.Contains("\n"))
                    {
                        if (input == "") return;
                        inputBuilder.Append(input);
                        string script = inputBuilder.ToString().Trim();
                        inputBuilder.Clear();

                        string output;
                        try
                        {
                            output = engine.Evaluate(script)?.ToString() ?? "null";
                        }
                        catch (Exception ex)
                        {
                            output = $"Uncaught {ex.Message}\r\n";
                        }

                        output = output.Replace("[undefined]", "");

                        await SendMessage($"{output}\r\n");
                        await SendMessage("v8> ");
                    }
                    else
                    {
                        inputBuilder.Append(input);
                    }
                }
                else if (result.MessageType == WebSocketMessageType.Close)
                {
                    await webSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Closing", CancellationToken.None);
                }
            }
        }

        private static async Task SendMessage(string message)
        {
            var buffer = Encoding.UTF8.GetBytes(message);
            var segment = new ArraySegment<byte>(buffer);
            await webSocket.SendAsync(segment, WebSocketMessageType.Text, true, CancellationToken.None);
        }

        private static string GetLocalPath(string urlPath)
        {
            string baseDirectory = @"htdocs";
            string localPath = Path.Combine(baseDirectory, urlPath.TrimStart('/').Replace('/', '\\'));
            if (Directory.Exists(localPath))
            {
                localPath = Path.Combine(localPath, "index.html");
            }
            return localPath;
        }

        private static string GetContentType(string filePath)
        {
            string extension = Path.GetExtension(filePath).ToLower();

            return extension switch
            {
                ".html" => "text/html",
                ".htm" => "text/html",
                ".css" => "text/css",
                ".js" => "application/javascript",
                ".png" => "image/png",
                ".jpg" => "image/jpeg",
                ".jpeg" => "image/jpeg",
                ".gif" => "image/gif",
                ".ico" => "image/x-icon",
                ".svg" => "image/svg+xml",
                ".json" => "application/json",
                ".xml" => "application/xml",
                ".pdf" => "application/pdf",
                ".txt" => "text/plain",
                ".zip" => "application/zip",
                ".tar" => "application/x-tar",
                ".gz" => "application/gzip",
                _ => "application/octet-stream",
            };
        }
    }

    public class HostFunctions
    {
        private readonly WebSocket webSocket;

        public HostFunctions(WebSocket webSocket)
        {
            this.webSocket = webSocket;
        }

        public void Log(string message)
        {
            SendMessage(message);
        }

        public void Warn(string message)
        {
            SendMessage("WARNING: " + message);
        }

        public void Error(string message)
        {
            SendMessage("ERROR: " + message);
        }

        private async void SendMessage(string message)
        {
            if (webSocket.State == WebSocketState.Open)
            {
                var buffer = Encoding.UTF8.GetBytes(message);
                var segment = new ArraySegment<byte>(buffer);
                await webSocket.SendAsync(segment, WebSocketMessageType.Text, true, CancellationToken.None);
            }
        }
    }
}
