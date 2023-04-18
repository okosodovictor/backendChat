using ChatServer.Services;
using Microsoft.AspNetCore.Server.Kestrel.Core;

var builder = WebApplication.CreateBuilder(args);
const int port = 5219;
builder.WebHost.ConfigureKestrel(options =>
{
    // Setup a HTTP/2 endpoint without TLS.
    options.ListenLocalhost(port, o => o.Protocols =
        HttpProtocols.Http2);
});

builder.Services.AddGrpc();
builder.Services.AddSingleton<ChatService>();

var app = builder.Build();

app.UseRouting();
app.UseEndpoints(endpoints =>
{
    endpoints.MapGrpcService<ChatService>();
});

Console.WriteLine($"gRPC server about to listening on port:{port}");

app.Run();
