using Microsoft.EntityFrameworkCore;
using WatermelonApi;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers();
builder.Services.AddDbContext<AppDbContext>(opt => 
    opt.UseSqlServer(builder.Configuration.GetConnectionString("Default")));

builder.Services.AddScoped<WatermelonService>();

var app = builder.Build();

app.MapControllers();
app.Run();