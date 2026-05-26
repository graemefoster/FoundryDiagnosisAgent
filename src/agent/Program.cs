using Azure.AI.AgentServer.Core;
using Azure.AI.AgentServer.Invocations;
using Azure.Identity;
using FoundryDiagnosisAgent.Agent;

var builder = AgentHost.CreateBuilder(args);

builder.Services.AddCors(options =>
    options.AddDefaultPolicy(policy =>
        policy.WithOrigins(
                builder.Configuration["Cors:AllowedOrigin"] ?? "http://localhost:5173")
              .AllowAnyHeader()
              .AllowAnyMethod()));

builder.Services.AddSingleton<DefaultAzureCredential>();
builder.Services.AddHttpClient();
builder.Services.Configure<CopilotHostedAgentOptions>(
    builder.Configuration.GetSection(CopilotHostedAgentOptions.SectionName));
builder.Services.AddSingleton<CopilotSessionManager>();
builder.Services.AddSingleton<HostedAgentDiagnostics>();

builder.Services.AddInvocationsServer();
builder.Services.AddScoped<InvocationHandler, GitHubCopilotInvocationHandler>();

builder.RegisterProtocol("invocations", endpoints => endpoints.MapInvocationsServer());

var app = builder.Build();

// Disable the developer exception page to avoid leaking auth headers in error responses
app.App.UseExceptionHandler(errorApp =>
{
    errorApp.Run(async context =>
    {
        context.Response.StatusCode = 500;
        context.Response.ContentType = "text/plain";
        await context.Response.WriteAsync("An internal error occurred.");
    });
});

app.App.UseCors();
await app.RunAsync();
