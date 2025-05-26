using AnalysisService.Services;  // para AnalysisServiceImpl

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddGrpc();

var app = builder.Build();
app.MapGrpcService<AnalysisServiceImpl>();  // aqui regista o teu impl

app.MapGet("/", () => "gRPC AnalysisService a correr em http://localhost:5002");
app.Run("http://localhost:5002");
